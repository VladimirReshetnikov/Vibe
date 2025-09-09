using System.Globalization;
using System.Text;
using Iced.Intel;
using Decoder = Iced.Intel.Decoder;

/// <summary>
/// A lightweight x64→C-like pseudocode printer tuned for MSVC-compiled Windows code.
/// Uses Iced.Intel for decoding, recognizes common prologues, Microsoft x64 calling convention,
/// and prints readable expressions for arithmetic, control flow, and memory accesses.
/// Includes small peephole improvements (memset/memcpy recognition, zero-store coalescing),
/// better conditions (bt/jcc and constant folding), and stack-region aliasing.
/// </summary>
public sealed class MsvcFunctionPseudoDecompiler
{
    public sealed class Options
    {
        /// <summary>Base address to assume for the function (used for RIP-relative and labels)</summary>
        public ulong BaseAddress { get; set; } = 0x0000000140000000UL;

        /// <summary>Optional pretty function name to show in the pseudocode header</summary>
        public string FunctionName { get; set; } = "func";

        /// <summary>Emit labels for branch targets (L1, L2, ...)</summary>
        public bool EmitLabels { get; set; } = true;

        /// <summary>Try to detect MSVC prologue/epilogue and hide low-level stack chaff</summary>
        public bool DetectPrologue { get; set; } = true;

        /// <summary>Emit comments for cmp/test that seed a following branch</summary>
        public bool CommentCompare { get; set; } = true;

        /// <summary>Include tiny asm comments (mnemonic+operands) for context</summary>
        public bool InlineAsmComments { get; set; } = false;

        /// <summary>Maximum bytes to decode; null = all provided bytes</summary>
        public int? MaxBytes { get; set; } = null;

        /// <summary>
        /// Optional name resolver for indirect/IAT calls. If provided and a call target can be
        /// resolved to an absolute address, this resolver can return a friendly symbol name.
        /// </summary>
        public Func<ulong, string?>? ResolveImportName { get; set; } = null;
    }

    private sealed class LastCmp
    {
        public string Left = "";
        public string Right = "";
        public bool IsTest;                  // true if TEST; false if CMP
        public int BitWidth;                 // 8/16/32/64 (best-effort)
        public ulong Ip;                     // where cmp/test occurred

        // Constant tracking for folding
        public bool LeftIsConst;
        public bool RightIsConst;
        public long LeftConst;
        public long RightConst;
    }

    private sealed class LastBt
    {
        public string Value = "";
        public string Index = "";
        public ulong Ip;
    }

    private sealed class Ctx
    {
        public readonly Options Opt;
        public readonly Dictionary<ulong, int> LabelByIp = new();
        public readonly Dictionary<long, string> LocalByNegRbpOffset = new(); // -8 -> local_8
        public readonly Dictionary<Register, string> RegName = new();
        public readonly HashSet<ulong> LabelNeeded = new();
        public readonly List<Instruction> Insns = new();
        public readonly Dictionary<ulong, string> InsnToAsm = new(); // optional ASM comments

        public LastCmp? LastCmp;
        public LastBt? LastBt;
        public bool UsesFramePointer;
        public int LocalSize; // 'sub rsp, imm' size if seen
        public ulong StartIp;

        // Heuristics / sugar
        public bool UsesGsPeb;                   // gs:[0x60] seen → emit peb alias
        public Register LastZeroedXmm = Register.None; // for memset(,0,16) recognition
        public bool LastWasCall;                 // used to keep RAX as 'ret' only right after call

        // Stack-region aliases (for RSP+offset blocks, eg attribute lists / local structs)
        public readonly Dictionary<long, string> RspAliasByOffset = new();

        public Ctx(Options opt) { Opt = opt; }
    }

    /// <summary>
    /// Convert a blob of raw x64 code (single function body) into C-like pseudocode.
    /// </summary>
    public string ToPseudoCode(byte[] code, Options? options = null)
    {
        var opt = options ?? new Options();
        var ctx = new Ctx(opt);

        // 1) Decode
        DecodeFunction(code, ctx);

        // 2) Label analysis
        AnalyzeLabels(ctx);

        // 3) Emit pseudocode
        var sb = new StringBuilder(16 * 1024);
        EmitHeader(sb, ctx);
        EmitBody(sb, ctx);
        sb.AppendLine("}");

        return sb.ToString();
    }

    // --------- Decode --------------------------------------------------------

    private static void DecodeFunction(byte[] code, Ctx ctx)
    {
        var reader = new ByteArrayCodeReader(code);
        var decoder = Decoder.Create(64, reader, ip: ctx.Opt.BaseAddress);
        var instr = default(Instruction);

        int byteLimit = ctx.Opt.MaxBytes ?? code.Length;
        ulong stopIp = ctx.Opt.BaseAddress + (ulong)byteLimit;

        ctx.StartIp = ctx.Opt.BaseAddress;

        // Seed register friendly names (params) at entry
        SeedEntryRegisterNames(ctx);

        // Usual Iced decode loop: advance while RIP is within our window
        while (decoder.IP < stopIp)
        {
            decoder.Decode(out instr);
            ctx.Insns.Add(instr);

            if (IsRet(instr))
                break; // likely end of function

            if (ctx.Opt.InlineAsmComments)
                ctx.InsnToAsm[instr.IP] = QuickAsm(instr);
        }

        // Prologue/locals detection (best-effort)
        if (ctx.Opt.DetectPrologue)
            DetectPrologueAndLocals(ctx);

        // Heuristic: did we see gs:[0x60]?
        DetectWellKnowns(ctx);
    }

    private static void DetectWellKnowns(Ctx ctx)
    {
        foreach (var ins in ctx.Insns)
        {
            for (int op = 0; op < ins.OpCount; op++)
            {
                if (GetOpKind(ins, op) == OpKind.Memory)
                {
                    if (ins.MemorySegment == Register.GS &&
                        ins.MemoryBase == Register.None &&
                        ins.MemoryIndex == Register.None &&
                        ins.MemoryDisplacement64 == 0x60)
                    {
                        ctx.UsesGsPeb = true;
                        return;
                    }
                }
            }
        }
    }

    private static bool IsRet(in Instruction i) =>
        i.Mnemonic == Mnemonic.Ret || i.Mnemonic == Mnemonic.Retf;

    private static string QuickAsm(in Instruction i)
    {
        // very small asm formatter
        var ops = new List<string>();
        for (int op = 0; op < i.OpCount; op++)
            ops.Add(QuickFormatOperand(i, op));
        return ops.Count == 0 ? i.Mnemonic.ToString().ToLowerInvariant()
                              : i.Mnemonic.ToString().ToLowerInvariant() + " " + string.Join(", ", ops);
    }

    private static string QuickFormatOperand(in Instruction i, int n)
    {
        var kind = GetOpKind(i, n);
        switch (kind)
        {
            case OpKind.Register:
                return GetOpRegister(i, n).ToString().ToLowerInvariant();
            case OpKind.NearBranch16:
            case OpKind.NearBranch32:
            case OpKind.NearBranch64:
                return "0x" + i.NearBranchTarget.ToString("X");
            case OpKind.Immediate8:
            case OpKind.Immediate16:
            case OpKind.Immediate32:
            case OpKind.Immediate64:
            case OpKind.Immediate8to16:
            case OpKind.Immediate8to32:
            case OpKind.Immediate8to64:
            case OpKind.Immediate32to64:
                return ImmStringFor(i, n, kind);
            case OpKind.Memory:
                return MemAddrString(i);
            default:
                return kind.ToString();
        }
    }

    // --------- Prologue / locals --------------------------------------------

    private static void DetectPrologueAndLocals(Ctx ctx)
    {
        var ins = ctx.Insns;
        int i = 0;
        if (ins.Count == 0) return;

        // Try to see "push rbp; mov rbp, rsp"
        if (ins[i].Mnemonic == Mnemonic.Push && ins[i].Op0Register == Register.RBP)
        {
            i++;
            if (i < ins.Count && ins[i].Mnemonic == Mnemonic.Mov &&
                ins[i].Op0Register == Register.RBP && ins[i].Op1Register == Register.RSP)
            {
                ctx.UsesFramePointer = true;
                i++;
            }
        }

        // Then maybe "sub rsp, imm"
        if (i < ins.Count && ins[i].Mnemonic == Mnemonic.Sub &&
            ins[i].Op0Register == Register.RSP &&
            IsImmediate(GetOpKind(ins[i], 1)))
        {
            var kind = GetOpKind(ins[i], 1);
            long imm = ParseImmediate(ins[i], kind);
            if (imm > 0 && (imm % 8 == 0))
                ctx.LocalSize = (int)imm;
        }
    }

    private static void SeedEntryRegisterNames(Ctx ctx)
    {
        // Microsoft x64 calling convention (integer/ptr args):
        // RCX, RDX, R8, R9; return in RAX. XMM0..XMM3 for fp, but we just name them for visibility.
        ctx.RegName[Register.RCX] = "p1";
        ctx.RegName[Register.RDX] = "p2";
        ctx.RegName[Register.R8] = "p3";
        ctx.RegName[Register.R9] = "p4";
        ctx.RegName[Register.RAX] = "ret"; // keep stable immediately after calls

        ctx.RegName[Register.XMM0] = "fp1";
        ctx.RegName[Register.XMM1] = "fp2";
        ctx.RegName[Register.XMM2] = "fp3";
        ctx.RegName[Register.XMM3] = "fp4";
    }

    // --------- Labeling / CFG-lite ------------------------------------------

    private static void AnalyzeLabels(Ctx ctx)
    {
        var ins = ctx.Insns;
        if (ins.Count == 0) return;

        var targetIps = new HashSet<ulong>();
        foreach (var i in ins)
        {
            switch (i.FlowControl)
            {
                case FlowControl.UnconditionalBranch:
                case FlowControl.ConditionalBranch:
                case FlowControl.Call:
                    if (HasNearTarget(i))
                        targetIps.Add(i.NearBranchTarget);
                    break;
            }
        }

        // Only label those inside our code range
        ulong lo = ins[0].IP;
        var last = ins[^1];
        ulong hi = last.IP + (ulong)last.Length;

        foreach (var ip in targetIps)
            if (ip >= lo && ip < hi)
                ctx.LabelNeeded.Add(ip);

        // Assign L1, L2, ... in program order
        int next = 1;
        foreach (var i in ins)
        {
            if (ctx.LabelNeeded.Contains(i.IP))
                ctx.LabelByIp[i.IP] = next++;
        }
    }

    // --------- Emission ------------------------------------------------------

    private static void EmitHeader(StringBuilder sb, Ctx ctx)
    {
        sb.AppendLine("/*");
        sb.AppendLine(" * Pseudocode (approximate) reconstructed from x64 instructions.");
        sb.AppendLine(" * Assumptions: MSVC on Windows, Microsoft x64 calling convention.");
        sb.AppendLine(" * Parameters: p1 (RCX), p2 (RDX), p3 (R8), p4 (R9); return in RAX.");
        sb.AppendLine(" * NOTE: This is not a full decompiler; conditions, types, and pointer types");
        sb.AppendLine(" *       are inferred heuristically and may be imprecise.");
        sb.AppendLine(" */");
        sb.AppendLine();

        sb.Append("uint64_t ").Append(ctx.Opt.FunctionName)
          .Append("(uint64_t p1, uint64_t p2, uint64_t p3, uint64_t p4");
        sb.AppendLine(") {");

        if (ctx.UsesFramePointer)
        {
            if (ctx.LocalSize > 0)
                sb.AppendLine($"    // stack frame: {ctx.LocalSize} bytes of locals (rbp-based)");
            else
                sb.AppendLine("    // stack frame: rbp-based");
        }
        else if (ctx.LocalSize > 0)
        {
            sb.AppendLine($"    // stack allocation: sub rsp, {ctx.LocalSize} (no rbp)");
        }
        sb.AppendLine("    // registers shown when helpful; memory shown via *(uintXX_t*)(addr)");
        if (ctx.UsesGsPeb)
            sb.AppendLine("    uint8_t* peb = (uint8_t*)__readgsqword(0x60);");
        sb.AppendLine();
    }

    private static void EmitBody(StringBuilder sb, Ctx ctx)
    {
        var ins = ctx.Insns;

        for (int idx = 0; idx < ins.Count; idx++)
        {
            var i = ins[idx];

            // Label if needed
            if (ctx.Opt.EmitLabels && ctx.LabelByIp.TryGetValue(i.IP, out int lab))
                sb.AppendLine($"L{lab}:");

            // ---- Peephole: coalesce zero-store runs into one memset ----
            if (TryCoalesceZeroStores(ins, idx, ctx, out string zeroLine, out int consumedZero))
            {
                sb.AppendLine("    " + zeroLine);
                idx += consumedZero - 1;
                continue;
            }

            // ---- Peephole: recognize memcpy via xmm16 load/store pairs ----
            if (TryCoalesceMemcpy16Blocks(ins, idx, ctx, out string memcpyLine, out int consumedMemcpy))
            {
                sb.AppendLine("    " + memcpyLine);
                idx += consumedMemcpy - 1;
                continue;
            }

            string line = TranslateInstruction(i, ctx);

            if (!string.IsNullOrWhiteSpace(line))
            {
                if (ctx.Opt.InlineAsmComments && ctx.InsnToAsm.TryGetValue(i.IP, out var asm))
                    sb.AppendLine("    " + line + "    // " + asm);
                else
                    sb.AppendLine("    " + line);
            }
        }
    }

    // --------- Peepholes -----------------------------------------------------

    private static bool TrySplitBasePlusOffset(string addr, out string @base, out long off)
    {
        @base = addr;
        off = 0;
        // Try forms: "X + 0xNN", "X - 0xNN"
        int plus = addr.LastIndexOf(" + 0x", StringComparison.Ordinal);
        int minus = addr.LastIndexOf(" - 0x", StringComparison.Ordinal);
        if (plus >= 0)
        {
            @base = addr.Substring(0, plus);
            string hex = addr.Substring(plus + 4);
            if (long.TryParse(hex.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long v))
            {
                off = v;
                return true;
            }
            @base = addr;
            off = 0;
            return false;
        }
        if (minus >= 0)
        {
            @base = addr.Substring(0, minus);
            string hex = addr.Substring(minus + 4);
            if (long.TryParse(hex.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long v))
            {
                off = -v;
                return true;
            }
            @base = addr;
            off = 0;
            return false;
        }
        // No explicit offset
        return true;
    }

    private static bool IsXmmLoad(in Instruction ins, out Register xmm, out string srcBase, out long srcOff)
    {
        xmm = Register.None; srcBase = ""; srcOff = 0;
        if (!(ins.Mnemonic == Mnemonic.Movups || ins.Mnemonic == Mnemonic.Movaps || ins.Mnemonic == Mnemonic.Movdqu))
            return false;
        if (!(ins.Op0Kind == OpKind.Register && ins.Op1Kind == OpKind.Memory)) return false;
        xmm = ins.Op0Register;
        string addr = FormatAddressExpression(ins, _tmpCtx);
        if (!TrySplitBasePlusOffset(addr, out srcBase, out srcOff)) return false;
        return true;
    }

    private static bool IsXmmStore(in Instruction ins, Register expectXmm, out string dstBase, out long dstOff)
    {
        dstBase = ""; dstOff = 0;
        if (!(ins.Mnemonic == Mnemonic.Movups || ins.Mnemonic == Mnemonic.Movaps || ins.Mnemonic == Mnemonic.Movdqu))
            return false;
        if (!(ins.Op0Kind == OpKind.Memory && ins.Op1Kind == OpKind.Register)) return false;
        if (ins.Op1Register != expectXmm) return false;
        string addr = FormatAddressExpression(ins, _tmpCtx);
        if (!TrySplitBasePlusOffset(addr, out dstBase, out dstOff)) return false;
        return true;
    }

    // A small temp context for address pretty-print in peepholes (no aliases)
    private static readonly Ctx _tmpCtx = new(new Options());

    private static bool TryCoalesceZeroStores(List<Instruction> ins, int idx, Ctx ctx, out string line, out int consumed)
    {
        line = ""; consumed = 0;
        var i = ins[idx];
        if (!((i.Mnemonic == Mnemonic.Xorps || i.Mnemonic == Mnemonic.Pxor) &&
              i.Op0Kind == OpKind.Register && i.Op1Kind == OpKind.Register &&
              i.Op0Register == i.Op1Register &&
              RegisterBitWidth(i.Op0Register) == 128))
            return false;

        var xmm = i.Op0Register;
        int j = idx + 1;
        string? baseExpr = null;
        long expectedOff = 0;
        int bytes = 0;

        while (j < ins.Count)
        {
            var s = ins[j];
            if (!(s.Mnemonic == Mnemonic.Movups || s.Mnemonic == Mnemonic.Movaps || s.Mnemonic == Mnemonic.Movdqu))
                break;
            if (!(s.Op0Kind == OpKind.Memory && s.Op1Kind == OpKind.Register && s.Op1Register == xmm))
                break;

            string addr = FormatAddressExpression(s, ctx);
            if (!TrySplitBasePlusOffset(addr, out string b, out long off)) break;
            if (baseExpr == null) { baseExpr = b; expectedOff = off; }
            else if (b != baseExpr || off != expectedOff) break;

            bytes += 16;
            expectedOff += 16;
            j++;
        }

        if (bytes >= 32 && baseExpr != null)
        {
            line = $"memset((void*)({baseExpr}), 0, {bytes});";
            consumed = j - idx;
            return true;
        }
        return false;
    }

    private static bool TryCoalesceMemcpy16Blocks(List<Instruction> ins, int idx, Ctx ctx, out string line, out int consumed)
    {
        line = ""; consumed = 0;

        // Expect repeating pairs: load xmmN, [src + off]; store [dst + off], xmmN
        int j = idx;
        string? srcBase = null, dstBase = null;
        long srcOff = 0, dstOff = 0, expectedSrc = 0, expectedDst = 0;
        int bytes = 0;
        bool haveAtLeastTwoPairs = false;

        while (j + 1 < ins.Count)
        {
            if (!IsXmmLoad(ins[j], out var xmm, out string sBase, out long sOff)) break;
            if (!IsXmmStore(ins[j + 1], xmm, out string dBase, out long dOff)) break;

            if (srcBase == null)
            {
                srcBase = sBase; dstBase = dBase; srcOff = sOff; dstOff = dOff;
                expectedSrc = sOff; expectedDst = dOff;
            }
            else
            {
                if (sBase != srcBase || dBase != dstBase || sOff != expectedSrc || dOff != expectedDst)
                    break;
            }

            bytes += 16;
            expectedSrc += 16;
            expectedDst += 16;
            j += 2;

            if (bytes >= 32) haveAtLeastTwoPairs = true;
        }

        if (haveAtLeastTwoPairs && srcBase != null && dstBase != null)
        {
            string srcExpr = srcOff == 0 ? srcBase : $"{srcBase} + 0x{srcOff:X}";
            string dstExpr = dstOff == 0 ? dstBase : $"{dstBase} + 0x{dstOff:X}";
            line = $"memcpy((void*)({dstExpr}), (void*)({srcExpr}), {bytes});";
            consumed = j - idx;
            return true;
        }

        return false;
    }

    // --------- Instruction translation --------------------------------------

    private static string TranslateInstruction(in Instruction i, Ctx ctx)
    {
        ctx.LastWasCall = false; // reset unless we see a call

        // Setcc / cmovcc families (Iced exposes specific mnemonics, not generic)
        if (IsSetcc(i))
        {
            var dest = FormatLhs(i, 0, ctx);
            var cond = FormatCondition(i, ctx);
            AssignRegNameIfRegister(i, 0, dest, ctx, "1");
            ctx.LastBt = null; // setcc doesn't depend on bt anymore
            return $"{dest} = {cond} ? 1 : 0;";
        }
        if (IsCmovcc(i))
        {
            var dest = FormatLhs(i, 0, ctx);
            var src = FormatOperand(i, 1, ctx);
            var cond = FormatCondition(i, ctx);
            AssignRegNameIfRegister(i, 0, dest, ctx, src);
            ctx.LastBt = null;
            return $"{dest} = ({cond}) ? {src} : {dest};";
        }
        if (i.FlowControl == FlowControl.ConditionalBranch)
        {
            string s = FormatCondJump(i, ctx);
            ctx.LastBt = null; // consume any pending bt context
            return s;
        }

        // Hide common prologue/epilogue chaff
        if (ctx.Opt.DetectPrologue && IsPrologueOrEpilogue(i))
            return CommentOnly(i);

        switch (i.Mnemonic)
        {
            // Moves and LEA
            case Mnemonic.Mov:
                return Assign(i, ctx, FormatLhs(i, 0, ctx), FormatRhsForMov(i, ctx));
            case Mnemonic.Movzx:
            case Mnemonic.Movsx:
            case Mnemonic.Movsxd:
                return Assign(i, ctx, FormatLhs(i, 0, ctx), FormatExtend(i, ctx));
            case Mnemonic.Lea:
                return Assign(i, ctx, FormatLhs(i, 0, ctx), FormatLeaExpression(i, ctx));

            // Zero-idioms and bitwise
            case Mnemonic.Xor:
                {
                    var a = FormatLhs(i, 0, ctx);
                    var b = FormatOperand(i, 1, ctx);
                    if (GetOpKind(i, 0) == OpKind.Register &&
                        GetOpKind(i, 1) == OpKind.Register &&
                        GetOpRegister(i, 0) == GetOpRegister(i, 1))
                    {
                        AssignRegNameIfRegister(i, 0, a, ctx, "0");
                        return $"{a} = 0;";
                    }
                    AssignRegNameIfRegister(i, 0, a, ctx, $"{a} ^ {b}");
                    return $"{a} = {a} ^ {b};";
                }
            case Mnemonic.Or:
                return BinOpAssign(i, ctx, "|");
            case Mnemonic.And:
                return BinOpAssign(i, ctx, "&");
            case Mnemonic.Not:
                return UnOpAssign(i, ctx, "~");
            case Mnemonic.Neg:
                return UnOpAssign(i, ctx, "-");

            // Arithmetic
            case Mnemonic.Add:
                return BinOpAssign(i, ctx, "+");
            case Mnemonic.Sub:
                return BinOpAssign(i, ctx, "-");
            case Mnemonic.Inc:
                return IncDec(i, ctx, +1);
            case Mnemonic.Dec:
                return IncDec(i, ctx, -1);

            case Mnemonic.Imul:
                return FormatImul(i, ctx);
            case Mnemonic.Mul:
                return "/* RDX:RAX = RAX * " + FormatOperand(i, 0, ctx) + " (unsigned) */";
            case Mnemonic.Idiv:
                return "/* RAX = (RDX:RAX) / " + FormatOperand(i, 0, ctx) + "; RDX = remainder (signed) */";
            case Mnemonic.Div:
                return "/* RAX = (RDX:RAX) / " + FormatOperand(i, 0, ctx) + "; RDX = remainder (unsigned) */";

            // Shifts/rotates
            case Mnemonic.Shl:
            case Mnemonic.Sal:
                return Shift(i, ctx, "<<");
            case Mnemonic.Shr:
                return Shift(i, ctx, ">>");
            case Mnemonic.Sar:
                return Shift(i, ctx, ">> /* arithmetic */");
            case Mnemonic.Rol:
                return Rotate(i, ctx, "rotl");
            case Mnemonic.Ror:
                return Rotate(i, ctx, "rotr");

            // Bit test family: keep comment and record for next jcc
            case Mnemonic.Bt:
            case Mnemonic.Bts:
            case Mnemonic.Btr:
            case Mnemonic.Btc:
                {
                    string v = FormatOperand(i, 0, ctx);
                    string ix = FormatOperand(i, 1, ctx);
                    ctx.LastBt = new LastBt { Value = v, Index = ix, Ip = i.IP };
                    return $"/* CF = bit({v}, {ix}) */";
                }

            // Flag setters used before branches
            case Mnemonic.Cmp:
                {
                    var (l, r, w, lc, lval, rc, rval) = ExtractCmpLike(i, ctx);
                    ctx.LastCmp = new LastCmp { Left = l, Right = r, BitWidth = w, IsTest = false, Ip = i.IP, LeftIsConst = lc, RightIsConst = rc, LeftConst = lval, RightConst = rval };
                    return ctx.Opt.CommentCompare ? $"/* compare {l}, {r} */" : "";
                }
            case Mnemonic.Test:
                {
                    var (l, r, w, lc, lval, rc, rval) = ExtractCmpLike(i, ctx);
                    ctx.LastCmp = new LastCmp { Left = l, Right = r, BitWidth = w, IsTest = true, Ip = i.IP, LeftIsConst = lc, RightIsConst = rc, LeftConst = lval, RightConst = rval };
                    return ctx.Opt.CommentCompare ? $"/* test {l}, {r} */" : "";
                }

            // Branching (unconditional)
            case Mnemonic.Jmp:
                return FormatJump(i, ctx);

            // Calls / returns
            case Mnemonic.Call:
                {
                    // Heuristic: detect memset-style helper by call site registers
                    if (TryRenderMemsetCallSite(i, ctx, out string msLine))
                        return msLine;

                    ctx.LastWasCall = true;
                    return FormatCall(i, ctx);
                }
            case Mnemonic.Ret:
            case Mnemonic.Retf:
                return FormatReturn(i, ctx);

            // Push/Pop (show as comments if not locals)
            case Mnemonic.Push:
                return $"/* push {FormatOperand(i, 0, ctx)} */";
            case Mnemonic.Pop:
                return $"/* pop -> {FormatOperand(i, 0, ctx)} */";

            // String ops / memset / memcpy (very rough)
            case Mnemonic.Movsb:
            case Mnemonic.Movsw:
            case Mnemonic.Movsd:
            case Mnemonic.Movsq:
                if (i.HasRepPrefix)
                {
                    int sz = i.Mnemonic switch { Mnemonic.Movsb => 1, Mnemonic.Movsw => 2, Mnemonic.Movsd => 4, _ => 8 };
                    return $"memcpy((void*)rdi, (void*)rsi, rcx*{sz}); /* rep movs{(sz == 8 ? "q" : (sz == 4 ? "d" : (sz == 2 ? "w" : "b")))} */";
                }
                return $"/* movs (element size varies) */";

            case Mnemonic.Stosb:
            case Mnemonic.Stosw:
            case Mnemonic.Stosd:
            case Mnemonic.Stosq:
                if (i.HasRepPrefix)
                {
                    int sz = i.Mnemonic switch { Mnemonic.Stosb => 1, Mnemonic.Stosw => 2, Mnemonic.Stosd => 4, _ => 8 };
                    string val = sz switch { 1 => "al", 2 => "ax", 4 => "eax", _ => "rax" };
                    return $"memset((void*)rdi, {val}, rcx*{sz}); /* rep stos{(sz == 8 ? "q" : (sz == 4 ? "d" : (sz == 2 ? "w" : "b")))} */";
                }
                return $"/* stos (element size varies) */";

            // SSE zero idioms → track for memset synthesis; scalar arithmetic (basic pretty-print)
            case Mnemonic.Xorps:
            case Mnemonic.Pxor:
                if (i.Op0Kind == OpKind.Register && i.Op1Kind == OpKind.Register &&
                    GetOpRegister(i, 0) == GetOpRegister(i, 1) &&
                    RegisterBitWidth(GetOpRegister(i, 0)) == 128)
                {
                    ctx.LastZeroedXmm = GetOpRegister(i, 0);
                    return "/* zero xmm */";
                }
                break;

            case Mnemonic.Movups:
            case Mnemonic.Movaps:
            case Mnemonic.Movdqu:
                // store from XMM to memory: single 16B zero store
                if (i.Op0Kind == OpKind.Memory && i.Op1Kind == OpKind.Register &&
                    RegisterBitWidth(GetOpRegister(i, 1)) == 128 &&
                    GetOpRegister(i, 1) == ctx.LastZeroedXmm)
                {
                    string addr = FormatAddressExpression(i, ctx);
                    ctx.LastZeroedXmm = Register.None;
                    return $"memset((void*)({addr}), 0, 16);";
                }
                break;

            case Mnemonic.Addss:
            case Mnemonic.Addsd:
            case Mnemonic.Subss:
            case Mnemonic.Subsd:
            case Mnemonic.Mulss:
            case Mnemonic.Mulsd:
            case Mnemonic.Divss:
            case Mnemonic.Divsd:
                {
                    var op = i.Mnemonic switch
                    {
                        Mnemonic.Addss or Mnemonic.Addsd => "+",
                        Mnemonic.Subss or Mnemonic.Subsd => "-",
                        Mnemonic.Mulss or Mnemonic.Mulsd => "*",
                        _ => "/"
                    };
                    string dst = FormatLhs(i, 0, ctx);
                    string src = FormatOperand(i, 1, ctx);
                    AssignRegNameIfRegister(i, 0, dst, ctx, $"{dst} {op} {src}");
                    return $"{dst} = {dst} {op} {src};";
                }

            // NOP and misc
            case Mnemonic.Nop:
                return "/* nop */";
            case Mnemonic.Leave:
                return "/* leave (epilogue) */";
            case Mnemonic.Cdq:
            case Mnemonic.Cqo:
                return "/* sign-extend: RDX:RAX <- sign(RAX) */";
        }

        // Default: show as a comment to avoid dropping semantics on the floor
        return CommentOnly(i);
    }

    // Heuristic call-site detection of memset(rcx, edx, r8d)
    private static bool TryRenderMemsetCallSite(in Instruction i, Ctx ctx, out string line)
    {
        line = "";
        if (i.Mnemonic != Mnemonic.Call) return false;

        // Pull current register expressions
        string dst = Friendly(ctx, Register.RCX);
        string val = Friendly(ctx, Register.EDX);
        string size = Friendly(ctx, Register.R8D);

        if (string.IsNullOrWhiteSpace(dst) || string.IsNullOrWhiteSpace(size))
            return false;

        // Heuristics: value is small/zero; destination looks like pointerish; size reasonable or constant
        bool valIsSmall = IsSmallIntegerLiteral(val);
        bool dstLooksPtr = LooksLikePointer(dst);
        if (!(valIsSmall && dstLooksPtr))
            return false;

        // Register RSP-alias if destination is "rsp + 0xNN"
        MaybeRegisterRspAlias(ctx, dst, size);

        // Consider this a memset; calls usually ignore the return value
        ctx.LastWasCall = true;
        ctx.RegName[Register.RAX] = "ret";
        line = $"memset((void*)({dst}), {val}, {size});";
        return true;
    }

    private static bool IsSmallIntegerLiteral(string s)
    {
        if (TryParseIntegerLiteral(s, out long v))
            return v >= -255 && v <= 255;
        return false;
    }

    private static bool TryParseIntegerLiteral(string s, out long v)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return long.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v);
        if (s.StartsWith("-", StringComparison.Ordinal))
            return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v);
        return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v);
    }

    private static bool LooksLikePointer(string s)
    {
        // quick heuristics: contains rsp/rbp/p/ local_ or '&'
        return s.Contains("rsp", StringComparison.OrdinalIgnoreCase)
            || s.Contains("rbp", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("p", StringComparison.Ordinal) // p1, p2...
            || s.StartsWith("&local_", StringComparison.Ordinal)
            || s.Contains("*(", StringComparison.Ordinal)
            || s.Contains("+ 0x", StringComparison.Ordinal);
    }

    private static void MaybeRegisterRspAlias(Ctx ctx, string expr, string sizeExpr)
    {
        if (TryParseRspOffset(expr, out long off))
        {
            if (!ctx.RspAliasByOffset.ContainsKey(off))
            {
                string alias = $"blk_{off:X}";
                ctx.RspAliasByOffset[off] = alias;
            }
        }
    }

    private static bool TryParseRspOffset(string expr, out long off)
    {
        off = 0;
        // expect "rsp + 0xNN" or "rsp - 0xNN"
        expr = expr.Replace(" ", "");
        int i = expr.IndexOf("rsp+0x", StringComparison.OrdinalIgnoreCase);
        if (i >= 0)
        {
            string hex = expr.Substring(i + 6);
            if (TryParseHex(hex, out off))
                return true;
        }
        i = expr.IndexOf("rsp-0x", StringComparison.OrdinalIgnoreCase);
        if (i >= 0)
        {
            if (TryParseHex(expr.Substring(i + 6), out long val))
            {
                off = -val;
                return true;
            }
        }
        return false;
    }

    private static bool TryParseHex(string s, out long v)
    {
        // trim trailing non-hex
        int end = 0;
        while (end < s.Length)
        {
            char c = s[end];
            if (!Uri.IsHexDigit(c)) break;
            end++;
        }
        if (end == 0) { v = 0; return false; }
        return long.TryParse(s.AsSpan(0, end), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v);
    }

    private static bool IsNonVolatile(Register r) =>
        r == Register.RBX || r == Register.RBP || r == Register.RSI || r == Register.RDI ||
        r == Register.R12 || r == Register.R13 || r == Register.R14 || r == Register.R15;

    private static bool IsPrologueOrEpilogue(in Instruction i)
    {
        if (i.Mnemonic == Mnemonic.Push && IsNonVolatile(i.Op0Register)) return true;
        if (i.Mnemonic == Mnemonic.Pop && IsNonVolatile(i.Op0Register)) return true;

        if (i.Mnemonic == Mnemonic.Mov && i.Op0Register == Register.RBP && i.Op1Register == Register.RSP) return true;
        if (i.Mnemonic == Mnemonic.Lea && i.Op0Register == Register.RBP && i.MemoryBase == Register.RSP) return true;

        if (i.Mnemonic == Mnemonic.Sub && i.Op0Register == Register.RSP && IsImmediate(GetOpKind(i, 1))) return true;
        if (i.Mnemonic == Mnemonic.Add && i.Op0Register == Register.RSP && IsImmediate(GetOpKind(i, 1))) return true;

        return false;
    }

    private static string CommentOnly(in Instruction i) => $"/* {QuickAsm(i)} */";

    // ---- Helpers for formatting operands, addresses, immediates  ------------

    private static string Assign(in Instruction i, Ctx ctx, string lhs, string rhs)
    {
        AssignRegNameIfRegister(i, 0, lhs, ctx, rhs);
        return $"{lhs} = {rhs};";
    }

    private static void AssignRegNameIfRegister(in Instruction i, int opIndex, string lhsText, Ctx ctx, string rhsExpr)
    {
        // If the destination is a register, update our friendly name mapping to carry expressions across
        if (GetOpKind(i, opIndex) == OpKind.Register)
        {
            var reg = GetOpRegister(i, opIndex);
            // keep RAX mapped as 'ret' only right after calls; otherwise prefer 'rax'
            if (reg == Register.RAX && !ctx.LastWasCall) rhsExpr = "rax";
            ctx.RegName[reg] = rhsExpr;
        }
    }

    private static bool IsIdentifier(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        if (!(char.IsLetter(s[0]) || s[0] == '_')) return false;
        for (int i = 1; i < s.Length; i++)
        {
            char c = s[i];
            if (!(char.IsLetterOrDigit(c) || c == '_')) return false;
        }
        return true;
    }

    private static string RegisterNameForLhs(Ctx ctx, Register r)
    {
        if (ctx.RegName.TryGetValue(r, out var nm) && IsIdentifier(nm))
            return nm; // eg 'p1'
        return r.ToString().ToLowerInvariant(); // eg 'rsi'
    }

    private static string FormatLhs(in Instruction i, int opIndex, Ctx ctx)
    {
        var k = GetOpKind(i, opIndex);
        if (k == OpKind.Register)
            return RegisterNameForLhs(ctx, GetOpRegister(i, opIndex));
        if (k == OpKind.Memory)
        {
            // special-case: writing to [gs:0x60] (rare) → keep as memory, not 'peb'
            if (i.MemorySegment == Register.GS &&
                i.MemoryBase == Register.None &&
                i.MemoryIndex == Register.None &&
                i.MemoryDisplacement64 == 0x60)
            {
                return $"*(({(MemoryBitWidth(i) == 64 ? "uint64_t" : "uint8_t")}*)(peb))";
            }
        }
        return FormatOperand(i, opIndex, ctx);
    }

    private static string FormatRhsForMov(in Instruction i, Ctx ctx)
    {
        // mov dst, src : prefer expressions we tracked for registers
        return FormatOperand(i, 1, ctx);
    }

    private static string FormatExtend(in Instruction i, Ctx ctx)
    {
        // Best-effort for movzx/movsx/movsxd
        string src = FormatOperand(i, 1, ctx);
        int srcBits = GuessBitWidth(i, operandIndex: 1);
        int dstBits = GuessBitWidth(i, operandIndex: 0);
        bool signed = i.Mnemonic == Mnemonic.Movsx || i.Mnemonic == Mnemonic.Movsxd;
        string cast = signed ? $"(int{dstBits}_t)" : $"(uint{dstBits}_t)";
        string inner = signed ? $"(int{srcBits}_t){src}" : $"(uint{srcBits}_t){src}";
        return $"{cast}{inner}";
    }

    private static string FormatLeaExpression(in Instruction i, Ctx ctx)
    {
        // lea r64, [base + index*scale + disp]  ==> r64 = base + index*scale + disp;
        return FormatAddressExpression(i, ctx);
    }

    private static string BinOpAssign(in Instruction i, Ctx ctx, string op)
    {
        var dst = FormatLhs(i, 0, ctx);
        var src = FormatOperand(i, 1, ctx);
        AssignRegNameIfRegister(i, 0, dst, ctx, $"{dst} {op} {src}");
        return $"{dst} = {dst} {op} {src};";
    }

    private static string UnOpAssign(in Instruction i, Ctx ctx, string op)
    {
        var dst = FormatLhs(i, 0, ctx);
        AssignRegNameIfRegister(i, 0, dst, ctx, $"{op}{dst}");
        return $"{dst} = {op}{dst};";
    }

    private static string IncDec(in Instruction i, Ctx ctx, int delta)
    {
        var dst = FormatLhs(i, 0, ctx);
        string op = delta > 0 ? " + 1" : " - 1";
        AssignRegNameIfRegister(i, 0, dst, ctx, $"{dst}{op}");
        return $"{dst} = {dst}{op};";
    }

    private static string Shift(in Instruction i, Ctx ctx, string op)
    {
        var dst = FormatLhs(i, 0, ctx);
        var cnt = FormatOperand(i, 1, ctx); // often 'cl'
        AssignRegNameIfRegister(i, 0, dst, ctx, $"{dst} {op} {cnt}");
        return $"{dst} = {dst} {op} {cnt};";
    }

    private static string Rotate(in Instruction i, Ctx ctx, string fn)
    {
        var dst = FormatLhs(i, 0, ctx);
        var cnt = FormatOperand(i, 1, ctx);
        AssignRegNameIfRegister(i, 0, dst, ctx, $"{fn}({dst}, {cnt})");
        return $"{dst} = {fn}({dst}, {cnt});";
    }

    private static string FormatImul(in Instruction i, Ctx ctx)
    {
        // Handle common encodings:
        //  - imul r64, r/m64                => r64 = r64 * r/m64   (two-operand in syntax)
        //  - imul r64, r/m64, imm8/imm32    => r64 = r/m64 * imm
        //  - imul r/m64                     => RDX:RAX = RAX * r/m64
        if (i.OpCount == 2 && GetOpKind(i, 0) == OpKind.Register)
        {
            string a = FormatLhs(i, 0, ctx);
            string b = FormatOperand(i, 1, ctx);
            AssignRegNameIfRegister(i, 0, a, ctx, $"{a} * {b}");
            return $"{a} = {a} * {b};";
        }
        if (i.OpCount == 3)
        {
            string dst = FormatLhs(i, 0, ctx);
            string src = FormatOperand(i, 1, ctx);
            string imm = FormatOperand(i, 2, ctx);
            AssignRegNameIfRegister(i, 0, dst, ctx, $"{src} * {imm}");
            return $"{dst} = {src} * {imm};";
        }
        // one-operand form:
        return "/* RDX:RAX = RAX * " + FormatOperand(i, 0, ctx) + " (signed) */";
    }

    private static string FormatJump(in Instruction i, Ctx ctx)
    {
        if (HasNearTarget(i) && ctx.LabelByIp.TryGetValue(i.NearBranchTarget, out int lab))
            return $"goto L{lab};";
        return $"/* jmp 0x{i.NearBranchTarget:X} */";
    }

    private static string FormatCondJump(in Instruction i, Ctx ctx)
    {
        string cond = FormatCondition(i, ctx);
        if (HasNearTarget(i) && ctx.LabelByIp.TryGetValue(i.NearBranchTarget, out int lab))
            return $"if ({cond}) goto L{lab};";
        return $"/* if ({cond}) goto 0x{i.NearBranchTarget:X}; */";
    }

    private static string FormatCondition(in Instruction i, Ctx ctx)
    {
        // Special cases not represented by ConditionCode enum:
        if (i.Mnemonic == Mnemonic.Jrcxz) return "rcx == 0";
        if (i.Mnemonic == Mnemonic.Jecxz) return "ecx == 0";
        if (i.Mnemonic == Mnemonic.Jcxz) return "cx == 0";

        var cc = i.ConditionCode;
        var c = ctx.LastCmp;

        // Bit-test (bt) → CF-based predicates
        if (ctx.LastBt is { } bt)
        {
            // Only CF-based codes are meaningful after BT: jB (CF=1), jAE (CF=0)
            if (cc == ConditionCode.b) return $"((({bt.Value}) >> ({bt.Index})) & 1) != 0";
            if (cc == ConditionCode.ae) return $"((({bt.Value}) >> ({bt.Index})) & 1) == 0";
        }

        // test r,r → simplify to r ==/!= 0 for je/jne
        if (c != null && c.IsTest && c.Left == c.Right)
        {
            if (cc == ConditionCode.e) return $"{c.Left} == 0";
            if (cc == ConditionCode.ne) return $"{c.Left} != 0";
        }

        // Constant folding where possible
        if (c != null && c.LeftIsConst && c.RightIsConst)
        {
            bool? res = c.IsTest
                ? EvalTest(cc, c.LeftConst, c.RightConst, c.BitWidth)
                : EvalCmp(cc, c.LeftConst, c.RightConst, c.BitWidth);
            if (res != null) return res.Value ? "true" : "false";
        }

        string Sign(string s) => $"/* signed */ {s}";
        string Uns(string s) => $"/* unsigned */ {s}";

        if (c != null)
        {
            var L = c.Left;
            var R = c.Right;

            switch (cc)
            {
                case ConditionCode.e: return c.IsTest ? $"(({L}) & ({R})) == 0" : $"({L}) == ({R})";
                case ConditionCode.ne: return c.IsTest ? $"(({L}) & ({R})) != 0" : $"({L}) != ({R})";

                case ConditionCode.l: return Sign($"({L}) < ({R})");
                case ConditionCode.ge: return Sign($"({L}) >= ({R})");
                case ConditionCode.le: return Sign($"({L}) <= ({R})");
                case ConditionCode.g: return Sign($"({L}) > ({R})");

                case ConditionCode.b: return Uns($"({L}) < ({R})");
                case ConditionCode.ae: return Uns($"({L}) >= ({R})");
                case ConditionCode.be: return Uns($"({L}) <= ({R})");
                case ConditionCode.a: return Uns($"({L}) > ({R})");

                case ConditionCode.s: return $"(sign({L})) != 0";
                case ConditionCode.ns: return $"(sign({L})) == 0";
                case ConditionCode.o: return "OF != 0";
                case ConditionCode.no: return "OF == 0";
                case ConditionCode.p: return "PF != 0";
                case ConditionCode.np: return "PF == 0";
            }
        }
        // Fallback if we don't have a preceding cmp/test context
        return cc switch
        {
            ConditionCode.e => "ZF != 0",
            ConditionCode.ne => "ZF == 0",
            ConditionCode.l => "SF != OF",
            ConditionCode.ge => "SF == OF",
            ConditionCode.le => "(ZF != 0) || (SF != OF)",
            ConditionCode.g => "(ZF == 0) && (SF == OF)",
            ConditionCode.b => "CF != 0",
            ConditionCode.ae => "CF == 0",
            ConditionCode.be => "(CF != 0) || (ZF != 0)",
            ConditionCode.a => "(CF == 0) && (ZF == 0)",
            ConditionCode.s => "SF != 0",
            ConditionCode.ns => "SF == 0",
            ConditionCode.o => "OF != 0",
            ConditionCode.no => "OF == 0",
            ConditionCode.p => "PF != 0",
            ConditionCode.np => "PF == 0",
            _ => "/* unknown condition */"
        };
    }

    private static bool? EvalCmp(ConditionCode cc, long l, long r, int width)
    {
        // Signed/unsigned both appear; only handle those with unambiguous semantics without flags:
        switch (cc)
        {
            case ConditionCode.e: return l == r;
            case ConditionCode.ne: return l != r;
            case ConditionCode.l: return ToSigned(l, width) < ToSigned(r, width);
            case ConditionCode.le: return ToSigned(l, width) <= ToSigned(r, width);
            case ConditionCode.g: return ToSigned(l, width) > ToSigned(r, width);
            case ConditionCode.ge: return ToSigned(l, width) >= ToSigned(r, width);
            case ConditionCode.b: return ToUnsigned(l, width) < ToUnsigned(r, width);
            case ConditionCode.be: return ToUnsigned(l, width) <= ToUnsigned(r, width);
            case ConditionCode.a: return ToUnsigned(l, width) > ToUnsigned(r, width);
            case ConditionCode.ae: return ToUnsigned(l, width) >= ToUnsigned(r, width);
            default: return null;
        }
    }

    private static bool? EvalTest(ConditionCode cc, long l, long r, int width)
    {
        ulong m = (ToUnsigned(l, width) & ToUnsigned(r, width));
        return cc switch
        {
            ConditionCode.e => m == 0,
            ConditionCode.ne => m != 0,
            _ => null
        };
    }

    private static long ToSigned(long v, int width)
    {
        if (width >= 64) return v;
        long mask = (1L << width) - 1;
        long val = v & mask;
        long sign = 1L << (width - 1);
        return (val ^ sign) - sign;
    }
    private static ulong ToUnsigned(long v, int width)
    {
        if (width >= 64) return unchecked((ulong)v);
        ulong mask = (1UL << width) - 1;
        return unchecked((ulong)v) & mask;
    }

    private static (string left, string right, int width, bool lIsConst, long lConst, bool rIsConst, long rConst)
        ExtractCmpLike(in Instruction i, Ctx ctx)
    {
        string l = FormatOperand(i, 0, ctx);
        string r = FormatOperand(i, 1, ctx);
        int w = Math.Max(GuessBitWidth(i, 0), GuessBitWidth(i, 1));
        if (w == 0) w = 64;

        bool lc = GetOpKind(i, 0) is OpKind.Immediate8 or OpKind.Immediate16 or OpKind.Immediate32 or OpKind.Immediate64
                                    or OpKind.Immediate8to16 or OpKind.Immediate8to32 or OpKind.Immediate8to64 or OpKind.Immediate32to64;
        bool rc = GetOpKind(i, 1) is OpKind.Immediate8 or OpKind.Immediate16 or OpKind.Immediate32 or OpKind.Immediate64
                                    or OpKind.Immediate8to16 or OpKind.Immediate8to32 or OpKind.Immediate8to64 or OpKind.Immediate32to64;
        long lval = lc ? ParseImmediate(i, GetOpKind(i, 0)) : 0;
        long rval = rc ? ParseImmediate(i, GetOpKind(i, 1)) : 0;

        return (l, r, w, lc, lval, rc, rval);
    }

    private static string FormatCall(in Instruction i, Ctx ctx)
    {
        string targetRepr;
        ulong? absTarget = null;

        if (HasNearTarget(i))
        {
            var t = i.NearBranchTarget;
            targetRepr = $"sub_{t:X}";
        }
        else
        {
            // Try RIP-relative memory operand (indirect call through IAT)
            if (i.Op0Kind == OpKind.Memory && i.IsIPRelativeMemoryOperand)
            {
                absTarget = i.IPRelativeMemoryAddress;
            }
            targetRepr = "indirect_call";
        }

        if (absTarget.HasValue && ctx.Opt.ResolveImportName is not null)
        {
            string? name = ctx.Opt.ResolveImportName(absTarget.Value);
            if (!string.IsNullOrEmpty(name))
                targetRepr = name;
        }

        // Microsoft x64: RCX,RDX,R8,R9 (use tracked expressions if available)
        string a1 = Friendly(ctx, Register.RCX);
        string a2 = Friendly(ctx, Register.RDX);
        string a3 = Friendly(ctx, Register.R8);
        string a4 = Friendly(ctx, Register.R9);

        // Keep RAX named 'ret' right after call
        ctx.RegName[Register.RAX] = "ret";

        return $"/* call */ ret = {targetRepr}({a1}, {a2}, {a3}, {a4});  // RAX";
    }

    private static string FormatReturn(in Instruction i, Ctx ctx)
    {
        string retExpr = Friendly(ctx, Register.RAX);
        return $"return {retExpr};";
    }

    // --------- Operand formatting & low-level utilities ----------------------

    private static string FormatOperand(in Instruction i, int opIndex, Ctx ctx)
    {
        var kind = GetOpKind(i, opIndex);
        switch (kind)
        {
            case OpKind.Register:
                return Friendly(ctx, GetOpRegister(i, opIndex));

            case OpKind.Immediate8:
            case OpKind.Immediate16:
            case OpKind.Immediate32:
            case OpKind.Immediate64:
            case OpKind.Immediate8to16:
            case OpKind.Immediate8to32:
            case OpKind.Immediate8to64:
            case OpKind.Immediate32to64:
                return ImmStringFor(i, opIndex, kind);

            case OpKind.NearBranch16:
            case OpKind.NearBranch32:
            case OpKind.NearBranch64:
                {
                    ulong t = i.NearBranchTarget;
                    return $"0x{t:X}";
                }

            case OpKind.Memory:
                {
                    // Special: [gs:0x60] → peb   (for *reads*)
                    if (i.MemorySegment == Register.GS &&
                        i.MemoryBase == Register.None &&
                        i.MemoryIndex == Register.None &&
                        i.MemoryDisplacement64 == 0x60)
                    {
                        return "peb";
                    }

                    // If rbp-based and negative disp with no index -> local_N (as a variable)
                    if ((i.MemoryBase == Register.RBP || i.MemoryBase == Register.EBP) && i.MemoryIndex == Register.None)
                    {
                        long disp = unchecked((long)i.MemoryDisplacement64);
                        if (disp < 0)
                        {
                            string local = RbpLocalName(ctx, -disp);
                            return local; // represent locals as variables
                        }
                    }

                    // Otherwise show as deref of specific width
                    int w = MemoryBitWidth(i);
                    var addr = FormatAddressExpression(i, ctx);
                    string type = w switch
                    {
                        8 => "uint8_t",
                        16 => "uint16_t",
                        32 => "uint32_t",
                        64 => "uint64_t",
                        128 => "vec128_t",
                        256 => "vec256_t",
                        512 => "vec512_t",
                        _ => "uint64_t"
                    };

                    // Optional: annotate well-known peb fields
                    string expr = $"*(({type}*)({addr}))";
                    if (addr.StartsWith("peb + 0x", StringComparison.OrdinalIgnoreCase))
                    {
                        string? field = PebFieldComment(addr);
                        if (field != null) expr += $" /* {field} */";
                    }
                    return expr;
                }
        }
        return kind.ToString();
    }

    private static string? PebFieldComment(string addrExpr)
    {
        // very small annotation for common offsets used in version queries
        // addrExpr is like "peb + 0x118"
        int plus = addrExpr.IndexOf('+');
        if (plus < 0) return null;
        string offHex = addrExpr.Substring(plus + 1).Trim();
        if (offHex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (uint.TryParse(offHex.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint off))
            {
                return off switch
                {
                    0x118 => "PEB->OSMajorVersion",
                    0x11C => "PEB->OSMinorVersion",
                    0x120 => "PEB->OSBuildNumber (WORD)",
                    0x124 => "PEB->OSPlatformId",
                    0x2F0 => "PEB->KUSER_SHARED_DATA pointer? (build dependent)",
                    _ => null
                };
            }
        }
        return null;
    }

    private static string RbpLocalName(Ctx ctx, long positiveDisp)
    {
        if (!ctx.LocalByNegRbpOffset.TryGetValue(positiveDisp, out var name))
        {
            name = $"local_{positiveDisp}";
            ctx.LocalByNegRbpOffset[positiveDisp] = name;
        }
        return name;
    }

    private static string FormatAddressExpression(in Instruction i, Ctx ctx)
    {
        var sb = new StringBuilder();

        // Only show FS/GS; DS/SS are implicit and noisy
        if (i.MemorySegment == Register.FS || i.MemorySegment == Register.GS)
        {
            // Special direct PEB alias
            if (i.MemorySegment == Register.GS &&
                i.MemoryBase == Register.None &&
                i.MemoryIndex == Register.None &&
                i.MemoryDisplacement64 == 0x60)
            {
                sb.Append("peb");
                return sb.ToString();
            }

            // Else show the segment prefix (rarely needed except TLS/TEB access)
            sb.Append(i.MemorySegment.ToString().ToLowerInvariant()).Append(":");
        }

        // RIP-relative using Iced's computed address (more accurate than recomputing)
        if (i.IsIPRelativeMemoryOperand)
        {
            ulong target = i.IPRelativeMemoryAddress;
            sb.Append("0x").Append(target.ToString("X"));
            return sb.ToString();
        }

        // RSP-based region alias (exact base)
        if (i.MemoryBase == Register.RSP && i.MemoryIndex == Register.None)
        {
            long disp = unchecked((long)i.MemoryDisplacement64);
            if (disp >= 0 && ctx.RspAliasByOffset.TryGetValue(disp, out var alias))
            {
                sb.Append(alias);
                return sb.ToString();
            }
        }

        // If rbp/ebp-based local with negative displacement and no index -> &local_N
        if ((i.MemoryBase == Register.RBP || i.MemoryBase == Register.EBP) && i.MemoryIndex == Register.None)
        {
            long disp = unchecked((long)i.MemoryDisplacement64);
            if (disp < 0)
            {
                string local = RbpLocalName(ctx, -disp);
                sb.Append("&").Append(local);
                return sb.ToString();
            }
        }

        bool any = false;
        if (i.MemoryBase != Register.None)
        {
            var baseName = AsVarName(ctx, i.MemoryBase);
            // If the base register currently aliases to "rsp+0xNN", honor the alias name
            if (TryParseRspOffset(baseName, out long off) && ctx.RspAliasByOffset.TryGetValue(off, out var alias))
                sb.Append(alias);
            else
                sb.Append(baseName);
            any = true;
        }
        if (i.MemoryIndex != Register.None)
        {
            if (any) sb.Append(" + ");
            var idxName = AsVarName(ctx, i.MemoryIndex);
            if (TryParseRspOffset(idxName, out long off) && ctx.RspAliasByOffset.TryGetValue(off, out var alias))
                sb.Append(alias);
            else
                sb.Append(idxName);
            int scale = i.MemoryIndexScale;
            if (scale != 1)
                sb.Append(" * ").Append(scale.ToString(CultureInfo.InvariantCulture));
            any = true;
        }

        long disp64 = unchecked((long)i.MemoryDisplacement64);
        if (disp64 != 0 || !any)
        {
            if (any) sb.Append(disp64 >= 0 ? " + " : " - ");
            sb.Append("0x").Append(Math.Abs(disp64).ToString("X"));
        }
        return sb.ToString();
    }

    private static string AsVarName(Ctx ctx, Register r)
    {
        if (ctx.RegName.TryGetValue(r, out var nm))
        {
            // Map common gs:[0x60] derived name to 'peb' if the expression contained that pattern
            if (nm.Contains("gs:0x60", StringComparison.OrdinalIgnoreCase))
                return "peb";
            // Map rsp+offset expressions to aliases, if present
            if (TryParseRspOffset(nm, out long off) && ctx.RspAliasByOffset.TryGetValue(off, out var alias))
                return alias;
            if (IsIdentifier(nm)) return nm;
        }
        return r.ToString().ToLowerInvariant();
    }

    private static string Friendly(Ctx ctx, Register r)
    {
        if (ctx.RegName.TryGetValue(r, out var nm))
            return nm;
        return r.ToString().ToLowerInvariant();
    }

    private static int GuessBitWidth(in Instruction i, int operandIndex)
    {
        var kind = GetOpKind(i, operandIndex);
        return kind switch
        {
            OpKind.Register => RegisterBitWidth(GetOpRegister(i, operandIndex)),
            OpKind.Memory => MemoryBitWidth(i),
            OpKind.Immediate8 or OpKind.Immediate8to16 or OpKind.Immediate8to32 or OpKind.Immediate8to64 => 8,
            OpKind.Immediate16 => 16,
            OpKind.Immediate32 or OpKind.Immediate32to64 => 32,
            OpKind.Immediate64 => 64,
            _ => 0
        };
    }

    private static int RegisterBitWidth(Register r)
    {
        if (r == Register.AL || r == Register.CL || r == Register.DL || r == Register.BL
            || (r >= Register.SPL && r <= Register.R15L)) return 8;
        if (r == Register.AX || r == Register.CX || r == Register.DX || r == Register.BX
            || r == Register.SP || r == Register.BP || r == Register.SI || r == Register.DI
            || (r >= Register.R8W && r <= Register.R15W)) return 16;
        if (r == Register.EAX || r == Register.ECX || r == Register.EDX || r == Register.EBX
            || r == Register.ESP || r == Register.EBP || r == Register.ESI || r == Register.EDI
            || (r >= Register.R8D && r <= Register.R15D)) return 32;
        if (r == Register.RAX || r == Register.RCX || r == Register.RDX || r == Register.RBX
            || r == Register.RSP || r == Register.RBP || r == Register.RSI || r == Register.RDI
            || (r >= Register.R8 && r <= Register.R15)) return 64;
        if (r >= Register.XMM0 && r <= Register.XMM31) return 128;
        if (r >= Register.YMM0 && r <= Register.YMM31) return 256;
        if (r >= Register.ZMM0 && r <= Register.ZMM31) return 512;
        return 0;
    }

    private static int MemoryBitWidth(in Instruction i)
    {
        var ms = i.MemorySize;
        return ms switch
        {
            MemorySize.UInt8 or MemorySize.Int8 => 8,
            MemorySize.UInt16 or MemorySize.Int16 => 16,
            MemorySize.UInt32 or MemorySize.Int32 or MemorySize.Float32 => 32,
            MemorySize.UInt64 or MemorySize.Int64 or MemorySize.Float64 => 64,
            MemorySize.Packed128_UInt64 or MemorySize.Packed128_Float64 or MemorySize.Packed128_Float32
                or MemorySize.UInt128 or MemorySize.Bcd or MemorySize.Packed128_Int16
                or MemorySize.Packed128_Int32 or MemorySize.Packed128_Int64 => 128,
            MemorySize.Packed256_Float32 or MemorySize.Packed256_Float64
                or MemorySize.UInt256 => 256,
            MemorySize.Packed512_Float32 or MemorySize.Packed512_Float64
                or MemorySize.UInt512 => 512,
            _ => 64
        };
    }

    private static bool HasNearTarget(in Instruction i)
    {
        return i.Op0Kind == OpKind.NearBranch16
            || i.Op0Kind == OpKind.NearBranch32
            || i.Op0Kind == OpKind.NearBranch64
            || i.Op1Kind == OpKind.NearBranch16
            || i.Op1Kind == OpKind.NearBranch32
            || i.Op1Kind == OpKind.NearBranch64;
    }

    private static string ImmStringFor(in Instruction i, int opIndex, OpKind kind)
    {
        long v = ParseImmediate(i, kind);
        int width = GuessBitWidth(i, opIndex);

        // Pretty for bitmasks in TEST/AND/OR
        if (i.Mnemonic == Mnemonic.Test || i.Mnemonic == Mnemonic.And || i.Mnemonic == Mnemonic.Or)
            return PrettyMask(v, width);

        // PROC_THREAD_ATTRIBUTE annotation if it looks like one (bits 16..18 flags, low 16 number)
        if ((unchecked((ulong)v) & 0xFFFFFFFFFFFF0000UL) != 0 && (unchecked((ulong)v) & 0xFFFFUL) != 0)
        {
            // simple heuristic: if only bits 16..18 and low 16 are set, annotate
            ulong uv = unchecked((ulong)v);
            ulong known = uv & 0x7_FFFFUL; // keep low 19 bits
            if (known == uv)
            {
                string s = PrettyNumber(v);
                return $"{s} /* {DecodeProcThreadAttribute(uv)} */";
            }
        }

        return PrettyNumber(v);
    }

    private static string PrettyNumber(long v)
    {
        if (v < 0) return $"0x{unchecked((ulong)v):X}";
        return v >= 10 ? $"0x{v:X}" : v.ToString(CultureInfo.InvariantCulture);
    }

    private static string PrettyMask(long v, int width)
    {
        if (v >= 0) return PrettyNumber(v);
        // Represent negative masks as ~0xXXXX (width-based)
        ulong full = width >= 64 ? ulong.MaxValue : ((1UL << width) - 1UL);
        ulong uv = unchecked((ulong)v) & full;
        ulong comp = (~uv) & full;
        if (comp == 0) return "~0x0";
        return $"~0x{comp:X}";
    }

    private static string DecodeProcThreadAttribute(ulong attr)
    {
        ulong number = attr & 0xFFFF;
        bool thread = (attr & 0x10000) != 0;
        bool input = (attr & 0x20000) != 0;
        bool additive = (attr & 0x40000) != 0;
        return $"ProcThreadAttribute(Number={number}, Thread={thread}, Input={input}, Additive={additive})";
    }

    private static long ParseImmediate(in Instruction i, OpKind kind)
    {
        return kind switch
        {
            OpKind.Immediate8 or OpKind.Immediate8to16 or OpKind.Immediate8to32 or OpKind.Immediate8to64
                => (sbyte)i.Immediate8,
            OpKind.Immediate16 => (short)i.Immediate16,
            OpKind.Immediate32 or OpKind.Immediate32to64 => (int)i.Immediate32,
            OpKind.Immediate64 => (long)i.Immediate64,
            _ => 0
        };
    }

    private static bool IsImmediate(OpKind k) =>
        k == OpKind.Immediate8 || k == OpKind.Immediate16 || k == OpKind.Immediate32 || k == OpKind.Immediate64
     || k == OpKind.Immediate8to16 || k == OpKind.Immediate8to32 || k == OpKind.Immediate8to64 || k == OpKind.Immediate32to64;

    private static bool IsRegister(OpKind k) => k == OpKind.Register;

    private static string MemAddrString(in Instruction i)
    {
        // Use a temporary context to reuse address logic; peb alias may not show here, that's fine for asm snippets.
        return FormatAddressExpression(i, new Ctx(new Options()));
    }

    private static OpKind GetOpKind(in Instruction i, int n) => n switch
    {
        0 => i.Op0Kind,
        1 => i.Op1Kind,
        2 => i.Op2Kind,
        3 => i.Op3Kind,
        _ => OpKind.Register
    };

    private static Register GetOpRegister(in Instruction i, int n) => n switch
    {
        0 => i.Op0Register,
        1 => i.Op1Register,
        2 => i.Op2Register,
        3 => i.Op3Register,
        _ => Register.None
    };

    // --------- helpers to recognize setcc / cmovcc families ------------------

    private static bool IsSetcc(in Instruction i) => i.Mnemonic switch
    {
        Mnemonic.Seta or Mnemonic.Setae or Mnemonic.Setb or Mnemonic.Setbe or
        Mnemonic.Sete or Mnemonic.Setne or Mnemonic.Setl or Mnemonic.Setle or
        Mnemonic.Setg or Mnemonic.Setge or Mnemonic.Seto or Mnemonic.Setno or
        Mnemonic.Sets or Mnemonic.Setns or Mnemonic.Setp or Mnemonic.Setnp => true,
        _ => false
    };

    private static bool IsCmovcc(in Instruction i) => i.Mnemonic switch
    {
        Mnemonic.Cmova or Mnemonic.Cmovae or Mnemonic.Cmovb or Mnemonic.Cmovbe or
        Mnemonic.Cmove or Mnemonic.Cmovne or Mnemonic.Cmovl or Mnemonic.Cmovle or
        Mnemonic.Cmovg or Mnemonic.Cmovge or Mnemonic.Cmovo or Mnemonic.Cmovno or
        Mnemonic.Cmovs or Mnemonic.Cmovns or Mnemonic.Cmovp or Mnemonic.Cmovnp => true,
        _ => false
    };
}
