using System.Globalization;
using System.Text;
using Iced.Intel;
using Decoder = Iced.Intel.Decoder;

/* ============================================================
 *   M S V C   F u n c t i o n   P s e u d o - D e c o m p i l e r
 *   - Builds PseudoIr.FunctionIR
 *   - Runs refinement passes
 *   - Pretty-prints (with disassembly comments and __pseudo annotations)
 * ============================================================ */

/* ============================================================
 *   C O N S T A N T   D A T A B A S E
 *   (Win32 Metadata-backed constant/flags naming)
 * ============================================================ */

public sealed class MsvcFunctionPseudoDecompiler
{
    public sealed class Options
    {
        public ulong BaseAddress { get; set; } = 0x0000000140000000UL;
        public string FunctionName { get; set; } = "func";
        public bool EmitLabels { get; set; } = true;
        public bool DetectPrologue { get; set; } = true;
        public bool CommentCompare { get; set; } = true;
        public int? MaxBytes { get; set; } = null;
        public Func<ulong, string?>? ResolveImportName { get; set; } = null;

        /// <summary>Constant provider for naming flags and codes.</summary>
        public PseudoIr.IConstantNameProvider? ConstantProvider { get; set; }

        /// <summary>
        /// Optional: if set (e.g., "Windows.Win32.Foundation.NTSTATUS"),
        /// MapNamedReturnConstants pass will rewrite "return 0xC000000D;" to "return STATUS_INVALID_PARAMETER;"
        /// </summary>
        public string? ReturnEnumTypeFullName { get; set; }
    }

    private sealed class LastCmp
    {
        public string Left = "";
        public string Right = "";
        public bool IsTest;
        public int BitWidth;
        public ulong Ip;
        public bool LeftIsConst;
        public bool RightIsConst;
        public long LeftConst;
        public long RightConst;
    }

    private sealed class LastBt { public string Value = ""; public string Index = ""; public ulong Ip; }

    private sealed class Ctx
    {
        public readonly Options Opt;
        public readonly Dictionary<ulong, int> LabelByIp = new();
        public readonly HashSet<ulong> LabelNeeded = new();
        public readonly List<Instruction> Insns = new();

        public readonly Dictionary<Register, string> RegName = new();

        public LastCmp? LastCmp;
        public LastBt? LastBt;
        public bool UsesFramePointer;
        public int LocalSize;
        public bool UsesGsPeb;
        public Register LastZeroedXmm = Register.None;
        public bool LastWasCall;

        public readonly Dictionary<long, string> RspAliasByOffset = new();

        public ulong StartIp;

        public readonly Formatter Formatter = new IntelFormatter();

        public Ctx(Options opt) { Opt = opt; }
    }

    /// <summary>Entry point: build IR, run passes, pretty-print.</summary>
    public string ToPseudoCode(byte[] code, Options? options = null)
    {
        var opt = options ?? new Options();
        var ctx = new Ctx(opt);

        DecodeFunction(code, ctx);
        AnalyzeLabels(ctx);

        // Build IR
        var fn = BuildFunctionIr(ctx);

        // --- Refinement passes (simple & safe) ---
        IrPasses.ReplaceParamRegsWithParams(fn); // p1..p4 RegExpr -> ParamExpr
        if (!string.IsNullOrEmpty(opt.ReturnEnumTypeFullName) && opt.ConstantProvider is not null)
            IrPasses.MapNamedReturnConstants(fn, opt.ConstantProvider, opt.ReturnEnumTypeFullName!);
        IrPasses.SimplifyRedundantAssign(fn);

        // Pretty print
        var pp = new PseudoIr.PrettyPrinter(new PseudoIr.PrettyPrinter.Options
        {
            EmitHeaderComment = true,
            EmitBlockLabels = false,
            CommentSignednessOnCmp = true,
            UseStdIntNames = true,
            ConstantProvider = opt.ConstantProvider
        });
        return pp.Print(fn);
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
        SeedEntryRegisterNames(ctx);

        while (decoder.IP < stopIp)
        {
            decoder.Decode(out instr);
            ctx.Insns.Add(instr);
            if (IsRet(instr)) break;
        }

        if (ctx.Opt.DetectPrologue)
            DetectPrologueAndLocals(ctx);

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

    private static void DetectPrologueAndLocals(Ctx ctx)
    {
        var ins = ctx.Insns;
        int i = 0;
        if (ins.Count == 0) return;

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
        ctx.RegName[Register.RCX] = "p1";
        ctx.RegName[Register.RDX] = "p2";
        ctx.RegName[Register.R8] = "p3";
        ctx.RegName[Register.R9] = "p4";
        ctx.RegName[Register.RAX] = "ret";
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

        ulong lo = ins[0].IP;
        var last = ins[^1];
        ulong hi = last.IP + (ulong)last.Length;

        foreach (var ip in targetIps)
            if (ip >= lo && ip < hi)
                ctx.LabelNeeded.Add(ip);

        int next = 1;
        foreach (var i in ins)
        {
            if (ctx.LabelNeeded.Contains(i.IP))
                ctx.LabelByIp[i.IP] = next++;
        }
    }

    // --------- Disassembly formatting ----------------------------------------

    private static string FormatDisasm(Ctx ctx, in Instruction i)
    {
        var outBuf = new StringOutput();
        ctx.Formatter.Format(in i, outBuf);
        return $"0x{i.IP:X}: {outBuf.ToString()}";
    }

    private sealed class StringOutput : FormatterOutput
    {
        private readonly StringBuilder _sb = new();
        public override void Write(string text, FormatterTextKind kind) => _sb.Append(text);
        public override string ToString() => _sb.ToString();
    }

    // --------- Build IR  -----------------------------------------------------

    private PseudoIr.FunctionIR BuildFunctionIr(Ctx ctx)
    {
        var fn = new PseudoIr.FunctionIR(ctx.Opt.FunctionName, ctx.Opt.BaseAddress, ctx.StartIp)
        {
            ReturnType = PseudoIr.X.U64
        };
        fn.Parameters.Add(new PseudoIr.Parameter("p1", PseudoIr.X.U64, 0));
        fn.Parameters.Add(new PseudoIr.Parameter("p2", PseudoIr.X.U64, 1));
        fn.Parameters.Add(new PseudoIr.Parameter("p3", PseudoIr.X.U64, 2));
        fn.Parameters.Add(new PseudoIr.Parameter("p4", PseudoIr.X.U64, 3));

        fn.Tags["UsesFramePointer"] = ctx.UsesFramePointer;
        fn.Tags["LocalSize"] = ctx.LocalSize;

        if (ctx.UsesGsPeb)
        {
            var init = new PseudoIr.CastExpr(
                new PseudoIr.CallExpr(PseudoIr.CallTarget.ByName("__readgsqword"),
                    new PseudoIr.Expr[] { PseudoIr.X.C(0x60) }),
                new PseudoIr.PointerType(PseudoIr.X.U8),
                PseudoIr.CastKind.Reinterpret);
            fn.Locals.Add(new PseudoIr.LocalVar("peb", new PseudoIr.PointerType(PseudoIr.X.U8), init));
        }

        var block = new PseudoIr.BasicBlock(new PseudoIr.LabelSymbol("entry", 0));
        fn.Blocks.Add(block);

        var ins = ctx.Insns;
        for (int idx = 0; idx < ins.Count; idx++)
        {
            var i = ins[idx];

            if (ctx.Opt.EmitLabels && ctx.LabelByIp.TryGetValue(i.IP, out int lab))
                block.Statements.Add(new PseudoIr.LabelStmt(new PseudoIr.LabelSymbol($"L{lab}", lab)));

            // Always emit disassembly line
            block.Statements.Add(new PseudoIr.AsmCommentStmt(FormatDisasm(ctx, i)));

            // Peepholes that replace runs with single calls (memset/memcpy)
            if (TryCoalesceZeroStoresIR(ins, idx, ctx, out var zeroStmts, out int consumedZero))
            {
                block.Statements.AddRange(zeroStmts);
                idx += consumedZero - 1;
                continue;
            }

            if (TryCoalesceMemcpy16BlocksIR(ins, idx, ctx, out var memcpyStmts, out int consumedMemcpy))
            {
                block.Statements.AddRange(memcpyStmts);
                idx += consumedMemcpy - 1;
                continue;
            }

            foreach (var s in TranslateInstructionToStmts(i, ctx))
                block.Statements.Add(s);
        }

        return fn;
    }

    // --------- Peepholes (IR) ------------------------------------------------

    private static bool TryCoalesceZeroStoresIR(List<Instruction> ins, int idx, Ctx ctx,
        out List<PseudoIr.Stmt> stmts, out int consumed)
    {
        stmts = new(); consumed = 0;
        var i = ins[idx];
        if (!((i.Mnemonic == Mnemonic.Xorps || i.Mnemonic == Mnemonic.Pxor) &&
              i.Op0Kind == OpKind.Register && i.Op1Kind == OpKind.Register &&
              i.Op0Register == i.Op1Register &&
              RegisterBitWidth(i.Op0Register) == 128))
            return false;

        var xmm = i.Op0Register;
        int j = idx + 1;
        PseudoIr.Expr? baseAddr = null;
        long expectedOff = 0;
        int bytes = 0;

        while (j < ins.Count)
        {
            var s = ins[j];
            if (!(s.Mnemonic == Mnemonic.Movups || s.Mnemonic == Mnemonic.Movaps || s.Mnemonic == Mnemonic.Movdqu))
                break;
            if (!(s.Op0Kind == OpKind.Memory && s.Op1Kind == OpKind.Register && s.Op1Register == xmm))
                break;
            if (s.MemoryIndex != Register.None || s.MemoryBase == Register.RIP)
                break;

            var addrExpr = AddressExpr(s, ctx, isLoadOrStoreAddr: true);
            if (!TrySplitBasePlusOffset(addrExpr, out var baseExpr, out long off))
                break;

            if (baseAddr is null) { baseAddr = baseExpr; expectedOff = off; }
            else
            {
                if (!ExprEquals(baseAddr, baseExpr) || off != expectedOff) break;
            }

            bytes += 16;
            expectedOff += 16;
            j++;
        }

        if (bytes >= 32 && baseAddr is not null)
        {
            var addr = expectedOff == bytes ? baseAddr : PseudoIr.X.Add(baseAddr, PseudoIr.X.C(expectedOff - bytes));
            stmts.Add(new PseudoIr.CallStmt(PseudoIr.X.Call("memset",
                new PseudoIr.Expr[] {
                    new PseudoIr.CastExpr(addr, new PseudoIr.PointerType(new PseudoIr.VoidType()), PseudoIr.CastKind.Reinterpret),
                    PseudoIr.X.C(0),
                    PseudoIr.X.C(bytes)
                })));
            consumed = j - idx;
            return true;
        }
        return false;
    }

    private static bool TryCoalesceMemcpy16BlocksIR(List<Instruction> ins, int idx, Ctx ctx,
        out List<PseudoIr.Stmt> stmts, out int consumed)
    {
        stmts = new(); consumed = 0;

        int j = idx;
        PseudoIr.Expr? srcBase = null, dstBase = null;
        long expectedSrc = 0, expectedDst = 0, startSrc = 0, startDst = 0;
        int bytes = 0;
        bool haveTwoPairs = false;

        while (j + 1 < ins.Count)
        {
            var ld = ins[j];
            var st = ins[j + 1];

            if (!(ld.Op0Kind == OpKind.Register && ld.Op1Kind == OpKind.Memory)) break;
            if (!(st.Op0Kind == OpKind.Memory && st.Op1Kind == OpKind.Register)) break;
            if (ld.Op0Register != st.Op1Register) break;

            if (!(ld.Mnemonic == Mnemonic.Movups || ld.Mnemonic == Mnemonic.Movaps || ld.Mnemonic == Mnemonic.Movdqu)) break;
            if (!(st.Mnemonic == Mnemonic.Movups || st.Mnemonic == Mnemonic.Movaps || st.Mnemonic == Mnemonic.Movdqu)) break;

            if (ld.MemoryIndex != Register.None || ld.MemoryBase == Register.RIP) break;
            if (st.MemoryIndex != Register.None || st.MemoryBase == Register.RIP) break;

            var sBaseExpr = AddressExpr(ld, ctx, isLoadOrStoreAddr: true);
            var dBaseExpr = AddressExpr(st, ctx, isLoadOrStoreAddr: true);
            if (!TrySplitBasePlusOffset(sBaseExpr, out var sBase, out long sOff)) break;
            if (!TrySplitBasePlusOffset(dBaseExpr, out var dBase, out long dOff)) break;

            if (srcBase is null)
            {
                srcBase = sBase; dstBase = dBase; expectedSrc = sOff; expectedDst = dOff; startSrc = sOff; startDst = dOff;
            }
            else
            {
                if (!ExprEquals(srcBase, sBase) || !ExprEquals(dstBase, dBase) || sOff != expectedSrc || dOff != expectedDst)
                    break;
            }

            bytes += 16;
            expectedSrc += 16;
            expectedDst += 16;
            j += 2;

            if (bytes >= 32) haveTwoPairs = true;
        }

        if (haveTwoPairs && srcBase is not null && dstBase is not null)
        {
            var src = startSrc == 0 ? srcBase : PseudoIr.X.Add(srcBase, PseudoIr.X.C(startSrc));
            var dst = startDst == 0 ? dstBase : PseudoIr.X.Add(dstBase, PseudoIr.X.C(startDst));
            stmts.Add(new PseudoIr.CallStmt(PseudoIr.X.Call("memcpy",
                new PseudoIr.Expr[] {
                    new PseudoIr.CastExpr(dst, new PseudoIr.PointerType(new PseudoIr.VoidType()), PseudoIr.CastKind.Reinterpret),
                    new PseudoIr.CastExpr(src, new PseudoIr.PointerType(new PseudoIr.VoidType()), PseudoIr.CastKind.Reinterpret),
                    PseudoIr.X.C(bytes)
                })));
            consumed = j - idx;
            return true;
        }

        return false;
    }

    private static bool TrySplitBasePlusOffset(PseudoIr.Expr addr, out PseudoIr.Expr baseExpr, out long off)
    {
        baseExpr = addr; off = 0;
        if (addr is PseudoIr.BinOpExpr bop && (bop.Op == PseudoIr.BinOp.Add || bop.Op == PseudoIr.BinOp.Sub))
        {
            if (bop.Right is PseudoIr.Const c)
            {
                baseExpr = bop.Left;
                off = bop.Op == PseudoIr.BinOp.Add ? c.Value : -c.Value;
                return true;
            }
        }
        return true;
    }

    private static bool ExprEquals(PseudoIr.Expr a, PseudoIr.Expr b)
    {
        if (a is PseudoIr.RegExpr ra && b is PseudoIr.RegExpr rb) return string.Equals(ra.Name, rb.Name, StringComparison.Ordinal);
        if (a is PseudoIr.LocalExpr la && b is PseudoIr.LocalExpr lb) return string.Equals(la.Name, lb.Name, StringComparison.Ordinal);
        if (a is PseudoIr.AddrOfExpr aa && b is PseudoIr.AddrOfExpr ab) return ExprEquals(aa.Operand, ab.Operand);
        if (a is PseudoIr.BinOpExpr ba && b is PseudoIr.BinOpExpr bb && ba.Op == bb.Op) return ExprEquals(ba.Left, bb.Left) && ExprEquals(ba.Right, bb.Right);
        return false;
    }

    // --------- Instruction translation → IR ----------------------------------

    private IEnumerable<PseudoIr.Stmt> TranslateInstructionToStmts(Instruction i, Ctx ctx)
    {
        ctx.LastWasCall = false;

        // setcc
        if (IsSetcc(i))
        {
            var dest = LhsExpr(i, ctx);
            var cond = ConditionExpr(i, ctx);
            yield return new PseudoIr.AssignStmt(dest, new PseudoIr.TernaryExpr(cond, PseudoIr.X.C(1), PseudoIr.X.C(0)));
            ctx.LastBt = null;
            yield break;
        }

        // cmovcc
        if (IsCmovcc(i))
        {
            var dest = LhsExpr(i, ctx);
            var src = OperandExpr(i, 1, ctx, forRead: true);
            var cond = ConditionExpr(i, ctx);
            yield return new PseudoIr.AssignStmt(dest, new PseudoIr.TernaryExpr(cond, src, dest));
            ctx.LastBt = null;
            yield break;
        }

        // jcc
        if (i.FlowControl == FlowControl.ConditionalBranch)
        {
            var cond = ConditionExpr(i, ctx);
            if (HasNearTarget(i) && ctx.LabelByIp.TryGetValue(i.NearBranchTarget, out int lab))
                yield return new PseudoIr.IfGotoStmt(cond, new PseudoIr.LabelSymbol($"L{lab}", lab));
            else
                yield return new PseudoIr.PseudoStmt($"if ({ExprToText(cond)}) goto 0x{i.NearBranchTarget:X}");
            ctx.LastBt = null;
            yield break;
        }

        // Skip extra pseudo for prologue/epilogue—disasm line already emitted
        if (ctx.Opt.DetectPrologue && IsPrologueOrEpilogue(i))
            yield break;

        switch (i.Mnemonic)
        {
            // Moves and LEA
            case Mnemonic.Mov:
            {
                if (i.Op0Kind == OpKind.Memory)
                {
                    var rhs = OperandExpr(i, 1, ctx, forRead: true);
                    var addr = AddressExpr(i, ctx, isLoadOrStoreAddr: true);
                    var ty = MemType(i);
                    yield return new PseudoIr.StoreStmt(addr, rhs, ty, Seg(i));
                }
                else
                {
                    var lhs = LhsExpr(i, ctx);
                    var rhs = OperandExpr(i, 1, ctx, forRead: true);
                    yield return new PseudoIr.AssignStmt(lhs, rhs);
                }
                yield break;
            }
            case Mnemonic.Movzx:
            case Mnemonic.Movsx:
            case Mnemonic.Movsxd:
            {
                var dst = LhsExpr(i, ctx);
                var src = OperandExpr(i, 1, ctx, forRead: true);
                var dstTy = ValueTypeGuess(i, 0);
                var kind = i.Mnemonic == Mnemonic.Movzx ? PseudoIr.CastKind.ZeroExtend : PseudoIr.CastKind.SignExtend;
                yield return new PseudoIr.AssignStmt(dst, new PseudoIr.CastExpr(src, dstTy, kind));
                yield break;
            }
            case Mnemonic.Lea:
            {
                var dst = LhsExpr(i, ctx);
                var addr = AddressExpr(i, ctx, isLoadOrStoreAddr: false);
                yield return new PseudoIr.AssignStmt(dst, addr);
                yield break;
            }

            // Bitwise & zero idioms
            case Mnemonic.Xor:
            {
                var a = LhsExpr(i, ctx);
                var b = OperandExpr(i, 1, ctx, forRead: true);
                if (i.Op0Kind == OpKind.Register && i.Op1Kind == OpKind.Register && i.Op0Register == i.Op1Register)
                    yield return new PseudoIr.AssignStmt(a, PseudoIr.X.C(0));
                else
                    yield return new PseudoIr.AssignStmt(a, new PseudoIr.BinOpExpr(PseudoIr.BinOp.Xor, a, b));
                yield break;
            }
            case Mnemonic.Or:
            {
                var a = LhsExpr(i, ctx);
                var b = OperandExpr(i, 1, ctx, forRead: true);
                yield return new PseudoIr.AssignStmt(a, new PseudoIr.BinOpExpr(PseudoIr.BinOp.Or, a, b));
                yield break;
            }
            case Mnemonic.And:
            {
                var a = LhsExpr(i, ctx);
                var b = OperandExpr(i, 1, ctx, forRead: true);
                yield return new PseudoIr.AssignStmt(a, new PseudoIr.BinOpExpr(PseudoIr.BinOp.And, a, b));
                yield break;
            }
            case Mnemonic.Not:
            {
                var a = LhsExpr(i, ctx);
                yield return new PseudoIr.AssignStmt(a, new PseudoIr.UnOpExpr(PseudoIr.UnOp.Not, a));
                yield break;
            }
            case Mnemonic.Neg:
            {
                var a = LhsExpr(i, ctx);
                yield return new PseudoIr.AssignStmt(a, new PseudoIr.UnOpExpr(PseudoIr.UnOp.Neg, a));
                yield break;
            }

            // Arithmetic
            case Mnemonic.Add:
            {
                var a = LhsExpr(i, ctx);
                var b = OperandExpr(i, 1, ctx, forRead: true);
                yield return new PseudoIr.AssignStmt(a, new PseudoIr.BinOpExpr(PseudoIr.BinOp.Add, a, b));
                yield break;
            }
            case Mnemonic.Sub:
            {
                var a = LhsExpr(i, ctx);
                var b = OperandExpr(i, 1, ctx, forRead: true);
                yield return new PseudoIr.AssignStmt(a, new PseudoIr.BinOpExpr(PseudoIr.BinOp.Sub, a, b));
                yield break;
            }
            case Mnemonic.Inc:
            {
                var a = LhsExpr(i, ctx);
                yield return new PseudoIr.AssignStmt(a, new PseudoIr.BinOpExpr(PseudoIr.BinOp.Add, a, PseudoIr.X.C(1)));
                yield break;
            }
            case Mnemonic.Dec:
            {
                var a = LhsExpr(i, ctx);
                yield return new PseudoIr.AssignStmt(a, new PseudoIr.BinOpExpr(PseudoIr.BinOp.Sub, a, PseudoIr.X.C(1)));
                yield break;
            }

            case Mnemonic.Imul:
            {
                if (i.OpCount == 2 && i.Op0Kind == OpKind.Register)
                {
                    var a = LhsExpr(i, ctx);
                    var b = OperandExpr(i, 1, ctx, forRead: true);
                    yield return new PseudoIr.AssignStmt(a, new PseudoIr.BinOpExpr(PseudoIr.BinOp.Mul, a, b));
                }
                else if (i.OpCount == 3)
                {
                    var dst = LhsExpr(i, ctx);
                    var src = OperandExpr(i, 1, ctx, forRead: true);
                    var imm = OperandExpr(i, 2, ctx, forRead: true);
                    yield return new PseudoIr.AssignStmt(dst, new PseudoIr.BinOpExpr(PseudoIr.BinOp.Mul, src, imm));
                }
                else
                {
                    yield return new PseudoIr.PseudoStmt("RDX_RAX = RAX * op /* signed */");
                }
                yield break;
            }
            case Mnemonic.Mul:
                yield return new PseudoIr.PseudoStmt("RDX_RAX = RAX * op /* unsigned */"); yield break;
            case Mnemonic.Idiv:
                yield return new PseudoIr.PseudoStmt("RAX = (RDX_RAX) / op; RDX = remainder /* signed */"); yield break;
            case Mnemonic.Div:
                yield return new PseudoIr.PseudoStmt("RAX = (RDX_RAX) / op; RDX = remainder /* unsigned */"); yield break;

            // Shifts/rotates
            case Mnemonic.Shl:
            case Mnemonic.Sal:
            {
                var a = LhsExpr(i, ctx);
                var c = OperandExpr(i, 1, ctx, forRead: true);
                yield return new PseudoIr.AssignStmt(a, new PseudoIr.BinOpExpr(PseudoIr.BinOp.Shl, a, c));
                yield break;
            }
            case Mnemonic.Shr:
            {
                var a = LhsExpr(i, ctx);
                var c = OperandExpr(i, 1, ctx, forRead: true);
                yield return new PseudoIr.AssignStmt(a, new PseudoIr.BinOpExpr(PseudoIr.BinOp.Shr, a, c));
                yield break;
            }
            case Mnemonic.Sar:
            {
                var a = LhsExpr(i, ctx);
                var c = OperandExpr(i, 1, ctx, forRead: true);
                yield return new PseudoIr.AssignStmt(a, new PseudoIr.BinOpExpr(PseudoIr.BinOp.Sar, a, c));
                yield break;
            }
            case Mnemonic.Rol:
            {
                var a = LhsExpr(i, ctx);
                var c = OperandExpr(i, 1, ctx, forRead: true);
                yield return new PseudoIr.AssignStmt(a, new PseudoIr.IntrinsicExpr("rotl", new[] { a, c }));
                yield break;
            }
            case Mnemonic.Ror:
            {
                var a = LhsExpr(i, ctx);
                var c = OperandExpr(i, 1, ctx, forRead: true);
                yield return new PseudoIr.AssignStmt(a, new PseudoIr.IntrinsicExpr("rotr", new[] { a, c }));
                yield break;
            }

            // Bit test family (record for next jcc)
            case Mnemonic.Bt:
            case Mnemonic.Bts:
            case Mnemonic.Btr:
            case Mnemonic.Btc:
            {
                string v = OperandText(i, 0, ctx);
                string ix = OperandText(i, 1, ctx);
                ctx.LastBt = new LastBt { Value = v, Index = ix, Ip = i.IP };
                yield return new PseudoIr.PseudoStmt($"CF = bit({v}, {ix})");
                yield break;
            }

            // Flag setters
            case Mnemonic.Cmp:
            {
                var (l, r, w, lc, lval, rc, rval) = ExtractCmpLike(i, ctx);
                ctx.LastCmp = new LastCmp { Left = l, Right = r, BitWidth = w, IsTest = false, Ip = i.IP, LeftIsConst = lc, RightIsConst = rc, LeftConst = lval, RightConst = rval };
                if (ctx.Opt.CommentCompare) yield return new PseudoIr.PseudoStmt($"compare {l}, {r}");
                yield break;
            }
            case Mnemonic.Test:
            {
                var (l, r, w, lc, lval, rc, rval) = ExtractCmpLike(i, ctx);
                ctx.LastCmp = new LastCmp { Left = l, Right = r, BitWidth = w, IsTest = true, Ip = i.IP, LeftIsConst = lc, RightIsConst = rc, LeftConst = lval, RightConst = rval };
                if (ctx.Opt.CommentCompare) yield return new PseudoIr.PseudoStmt($"test {l}, {r}");
                yield break;
            }

            // Branching (unconditional)
            case Mnemonic.Jmp:
                if (HasNearTarget(i) && ctx.LabelByIp.TryGetValue(i.NearBranchTarget, out int lab))
                    yield return new PseudoIr.GotoStmt(new PseudoIr.LabelSymbol($"L{lab}", lab));
                else
                    yield return new PseudoIr.PseudoStmt($"jmp 0x{i.NearBranchTarget:X}");
                yield break;

            // Calls / returns
            case Mnemonic.Call:
            {
                if (TryRenderMemsetCallSiteIR(i, ctx, out var ms))
                {
                    yield return ms;
                    yield break;
                }

                var callExpr = BuildCallExpr(i, ctx, out bool assignsToRet);
                if (assignsToRet) yield return new PseudoIr.AssignStmt(PseudoIr.X.R("ret"), callExpr);
                else yield return new PseudoIr.CallStmt(callExpr);
                ctx.LastWasCall = true;
                yield break;
            }

            case Mnemonic.Ret:
            case Mnemonic.Retf:
                yield return new PseudoIr.ReturnStmt(PseudoIr.X.R("ret"));
                yield break;

            // Push/Pop/misc: disassembly already shown
            case Mnemonic.Push:
            case Mnemonic.Pop:
            case Mnemonic.Nop:
            case Mnemonic.Leave:
            case Mnemonic.Cdq:
            case Mnemonic.Cqo:
                yield break;

            // String ops
            case Mnemonic.Movsb:
            case Mnemonic.Movsw:
            case Mnemonic.Movsd:
            case Mnemonic.Movsq:
                if (i.HasRepPrefix)
                {
                    int sz = i.Mnemonic switch { Mnemonic.Movsb => 1, Mnemonic.Movsw => 2, Mnemonic.Movsd => 4, _ => 8 };
                    yield return new PseudoIr.CallStmt(PseudoIr.X.Call("memcpy",
                        new PseudoIr.Expr[] { PseudoIr.X.R("rdi"), PseudoIr.X.R("rsi"), PseudoIr.X.Mul(PseudoIr.X.R("rcx"), PseudoIr.X.C(sz)) }));
                }
                yield break;

            case Mnemonic.Stosb:
            case Mnemonic.Stosw:
            case Mnemonic.Stosd:
            case Mnemonic.Stosq:
                if (i.HasRepPrefix)
                {
                    int sz = i.Mnemonic switch { Mnemonic.Stosb => 1, Mnemonic.Stosw => 2, Mnemonic.Stosd => 4, _ => 8 };
                    string val = sz switch { 1 => "al", 2 => "ax", 4 => "eax", _ => "rax" };
                    yield return new PseudoIr.CallStmt(PseudoIr.X.Call("memset",
                        new PseudoIr.Expr[] { PseudoIr.X.R("rdi"), PseudoIr.X.R(val), PseudoIr.X.Mul(PseudoIr.X.R("rcx"), PseudoIr.X.C(sz)) }));
                }
                yield break;
        }

        // Fallback: translation omitted; disasm already printed
        yield break;
    }

    // --------- Condition construction ----------------------------------------

    private PseudoIr.Expr ConditionExpr(in Instruction i, Ctx ctx)
    {
        if (i.Mnemonic == Mnemonic.Jrcxz) return PseudoIr.X.Eq(PseudoIr.X.R("rcx"), PseudoIr.X.C(0));
        if (i.Mnemonic == Mnemonic.Jecxz) return PseudoIr.X.Eq(PseudoIr.X.R("ecx"), PseudoIr.X.C(0));
        if (i.Mnemonic == Mnemonic.Jcxz) return PseudoIr.X.Eq(PseudoIr.X.R("cx"), PseudoIr.X.C(0));

        var cc = i.ConditionCode;

        if (ctx.LastBt is { } bt)
        {
            if (cc == ConditionCode.b || cc == ConditionCode.ae)
            {
                var val = ParseTextAsExpr(bt.Value);
                var ix = ParseTextAsExpr(bt.Index);
                var bit = PseudoIr.X.And(PseudoIr.X.Shr(val, ix), PseudoIr.X.C(1));
                return cc == ConditionCode.b ? PseudoIr.X.Ne(bit, PseudoIr.X.C(0)) : PseudoIr.X.Eq(bit, PseudoIr.X.C(0));
            }
        }

        var c = ctx.LastCmp;

        if (c != null && c.IsTest && c.Left == c.Right)
        {
            var r = ParseTextAsExpr(c.Left);
            if (cc == ConditionCode.e) return PseudoIr.X.Eq(r, PseudoIr.X.C(0));
            if (cc == ConditionCode.ne) return PseudoIr.X.Ne(r, PseudoIr.X.C(0));
        }

        if (c != null)
        {
            var L = ParseTextAsExpr(c.Left);
            var R = ParseTextAsExpr(c.Right);

            switch (cc)
            {
                case ConditionCode.e: return c.IsTest ? PseudoIr.X.Eq(PseudoIr.X.And(L, R), PseudoIr.X.C(0)) : PseudoIr.X.Eq(L, R);
                case ConditionCode.ne: return c.IsTest ? PseudoIr.X.Ne(PseudoIr.X.And(L, R), PseudoIr.X.C(0)) : PseudoIr.X.Ne(L, R);

                case ConditionCode.l: return PseudoIr.X.SLt(L, R);
                case ConditionCode.ge: return PseudoIr.X.SGe(L, R);
                case ConditionCode.le: return PseudoIr.X.SLe(L, R);
                case ConditionCode.g: return PseudoIr.X.SGt(L, R);

                case ConditionCode.b: return PseudoIr.X.ULt(L, R);
                case ConditionCode.ae: return PseudoIr.X.UGe(L, R);
                case ConditionCode.be: return PseudoIr.X.ULe(L, R);
                case ConditionCode.a: return PseudoIr.X.UGt(L, R);

                case ConditionCode.s: return PseudoIr.X.Ne(PseudoIr.X.R("SF"), PseudoIr.X.C(0));
                case ConditionCode.ns: return PseudoIr.X.Eq(PseudoIr.X.R("SF"), PseudoIr.X.C(0));
                case ConditionCode.o: return PseudoIr.X.Ne(PseudoIr.X.R("OF"), PseudoIr.X.C(0));
                case ConditionCode.no: return PseudoIr.X.Eq(PseudoIr.X.R("OF"), PseudoIr.X.C(0));
                case ConditionCode.p: return PseudoIr.X.Ne(PseudoIr.X.R("PF"), PseudoIr.X.C(0));
                case ConditionCode.np: return PseudoIr.X.Eq(PseudoIr.X.R("PF"), PseudoIr.X.C(0));
            }
        }

        return cc switch
        {
            ConditionCode.e => PseudoIr.X.Ne(PseudoIr.X.R("ZF"), PseudoIr.X.C(0)),
            ConditionCode.ne => PseudoIr.X.Eq(PseudoIr.X.R("ZF"), PseudoIr.X.C(0)),
            ConditionCode.l => PseudoIr.X.Ne(PseudoIr.X.R("SF"), PseudoIr.X.R("OF")),
            ConditionCode.ge => PseudoIr.X.Eq(PseudoIr.X.R("SF"), PseudoIr.X.R("OF")),
            ConditionCode.le => PseudoIr.X.Or(PseudoIr.X.Ne(PseudoIr.X.R("ZF"), PseudoIr.X.C(0)), PseudoIr.X.Ne(PseudoIr.X.R("SF"), PseudoIr.X.R("OF"))),
            ConditionCode.g => PseudoIr.X.And(PseudoIr.X.Eq(PseudoIr.X.R("ZF"), PseudoIr.X.C(0)), PseudoIr.X.Eq(PseudoIr.X.R("SF"), PseudoIr.X.R("OF"))),
            ConditionCode.b => PseudoIr.X.Ne(PseudoIr.X.R("CF"), PseudoIr.X.C(0)),
            ConditionCode.ae => PseudoIr.X.Eq(PseudoIr.X.R("CF"), PseudoIr.X.C(0)),
            ConditionCode.be => PseudoIr.X.Or(PseudoIr.X.Ne(PseudoIr.X.R("CF"), PseudoIr.X.C(0)), PseudoIr.X.Ne(PseudoIr.X.R("ZF"), PseudoIr.X.C(0))),
            ConditionCode.a => PseudoIr.X.And(PseudoIr.X.Eq(PseudoIr.X.R("CF"), PseudoIr.X.C(0)), PseudoIr.X.Eq(PseudoIr.X.R("ZF"), PseudoIr.X.C(0))),
            _ => new PseudoIr.IntrinsicExpr("__unknown_cond", Array.Empty<PseudoIr.Expr>())
        };
    }

    private static string ExprToText(PseudoIr.Expr e)
        => e switch
        {
            PseudoIr.RegExpr r => r.Name,
            PseudoIr.Const c => (c.Value >= 10 ? "0x" + c.Value.ToString("X") : c.Value.ToString()),
            PseudoIr.UConst uc => "0x" + uc.Value.ToString("X"),
            _ => "cond"
        };

    // --------- Operand & address helpers -------------------------------------

    private PseudoIr.Expr LhsExpr(in Instruction i, Ctx ctx)
    {
        if (i.Op0Kind == OpKind.Register)
            return PseudoIr.X.R(AsVarName(ctx, i.Op0Register));
        if (i.Op0Kind == OpKind.Memory)
        {
            var addr = AddressExpr(i, ctx, isLoadOrStoreAddr: true);
            return new PseudoIr.LoadExpr(addr, MemType(i), Seg(i));
        }
        return PseudoIr.X.R("tmp");
    }

    private PseudoIr.Expr OperandExpr(in Instruction i, int op, Ctx ctx, bool forRead)
    {
        var kind = GetOpKind(i, op);
        switch (kind)
        {
            case OpKind.Register:
                return PseudoIr.X.R(AsVarName(ctx, GetOpRegister(i, op)));

            case OpKind.Immediate8:
            case OpKind.Immediate16:
            case OpKind.Immediate32:
            case OpKind.Immediate64:
            case OpKind.Immediate8to16:
            case OpKind.Immediate8to32:
            case OpKind.Immediate8to64:
            case OpKind.Immediate32to64:
            {
                long v = ParseImmediate(i, kind);
                int bits = kind switch
                {
                    OpKind.Immediate8 or OpKind.Immediate8to16 or OpKind.Immediate8to32 or OpKind.Immediate8to64 => 8,
                    OpKind.Immediate16 => 16,
                    OpKind.Immediate32 or OpKind.Immediate32to64 => 32,
                    OpKind.Immediate64 => 64,
                    _ => 64
                };
                return new PseudoIr.Const(v, bits);
            }

            case OpKind.NearBranch16:
            case OpKind.NearBranch32:
            case OpKind.NearBranch64:
                return new PseudoIr.UConst(i.NearBranchTarget, 64);

            case OpKind.Memory:
            {
                var addr = AddressExpr(i, ctx, isLoadOrStoreAddr: forRead);
                var ty = MemType(i);
                return new PseudoIr.LoadExpr(addr, ty, Seg(i));
            }
        }
        return PseudoIr.X.R("op");
    }

    private static string OperandText(in Instruction i, int op, Ctx ctx)
    {
        var kind = GetOpKind(i, op);
        switch (kind)
        {
            case OpKind.Register: return AsVarName(ctx, GetOpRegister(i, op));
            case OpKind.Immediate8:
            case OpKind.Immediate16:
            case OpKind.Immediate32:
            case OpKind.Immediate64:
            case OpKind.Immediate8to16:
            case OpKind.Immediate8to32:
            case OpKind.Immediate8to64:
            case OpKind.Immediate32to64:
                return ImmString(i, kind);
            case OpKind.NearBranch16:
            case OpKind.NearBranch32:
            case OpKind.NearBranch64:
                return $"0x{i.NearBranchTarget:X}";
            case OpKind.Memory:
                return MemAddrString(i);
        }
        return kind.ToString();
    }

    private static PseudoIr.IrType MemType(in Instruction i)
    {
        var ms = i.MemorySize;
        return ms switch
        {
            MemorySize.UInt8 or MemorySize.Int8 => PseudoIr.X.U8,
            MemorySize.UInt16 or MemorySize.Int16 => PseudoIr.X.U16,
            MemorySize.UInt32 or MemorySize.Int32 or MemorySize.Float32 => PseudoIr.X.U32,
            MemorySize.UInt64 or MemorySize.Int64 or MemorySize.Float64 or MemorySize.Unknown => PseudoIr.X.U64,
            MemorySize.Packed128_UInt64 or MemorySize.Packed128_Float64 or MemorySize.Packed128_Float32
                or MemorySize.UInt128 or MemorySize.Bcd or MemorySize.Packed128_Int16
                or MemorySize.Packed128_Int32 or MemorySize.Packed128_Int64 => new PseudoIr.VectorType(128),
            MemorySize.Packed256_Float32 or MemorySize.Packed256_Float64
                or MemorySize.UInt256 => new PseudoIr.VectorType(256),
            MemorySize.Packed512_Float32 or MemorySize.Packed512_Float64
                or MemorySize.UInt512 => new PseudoIr.VectorType(512),
            _ => PseudoIr.X.U64
        };
    }

    private static PseudoIr.SegmentReg Seg(in Instruction i)
        => i.MemorySegment switch { Register.FS => PseudoIr.SegmentReg.FS, Register.GS => PseudoIr.SegmentReg.GS, _ => PseudoIr.SegmentReg.None };

    private static PseudoIr.Expr AddressExpr(in Instruction i, Ctx ctx, bool isLoadOrStoreAddr)
    {
        if (i.MemorySegment == Register.GS &&
            i.MemoryBase == Register.None &&
            i.MemoryIndex == Register.None &&
            i.MemoryDisplacement64 == 0x60)
        {
            return PseudoIr.X.L("peb");
        }

        if (i.IsIPRelativeMemoryOperand)
        {
            ulong target = i.IPRelativeMemoryAddress;
            return new PseudoIr.UConst(target, 64);
        }

        if ((i.MemoryBase == Register.RBP || i.MemoryBase == Register.EBP) && i.MemoryIndex == Register.None)
        {
            long disp = unchecked((long)i.MemoryDisplacement64);
            if (disp < 0)
            {
                string local = $"local_{-disp}";
                return new PseudoIr.AddrOfExpr(PseudoIr.X.L(local));
            }
        }

        PseudoIr.Expr? expr = null;
        if (i.MemoryBase != Register.None)
            expr = PseudoIr.X.R(AsVarName(ctx, i.MemoryBase));

        if (i.MemoryIndex != Register.None)
        {
            var idx = PseudoIr.X.R(AsVarName(ctx, i.MemoryIndex));
            int scale = i.MemoryIndexScale;
            PseudoIr.Expr scaled = scale == 1 ? idx : PseudoIr.X.Mul(idx, PseudoIr.X.C(scale));
            expr = expr is null ? scaled : PseudoIr.X.Add(expr, scaled);
        }

        long disp64 = unchecked((long)i.MemoryDisplacement64);
        if (disp64 != 0 || expr is null)
        {
            var c = PseudoIr.X.C(Math.Abs(disp64));
            expr = expr is null ? c : (disp64 >= 0 ? PseudoIr.X.Add(expr, c) : PseudoIr.X.Sub(expr, c));
        }
        return expr!;
    }

    private static PseudoIr.IrType ValueTypeGuess(in Instruction i, int opIndex)
    {
        var k = GetOpKind(i, opIndex);
        return k switch
        {
            OpKind.Register => GuessFromRegister(GetOpRegister(i, opIndex)),
            OpKind.Memory => MemType(i),
            OpKind.Immediate8 or OpKind.Immediate8to16 or OpKind.Immediate8to32 or OpKind.Immediate8to64 => PseudoIr.X.U8,
            OpKind.Immediate16 => PseudoIr.X.U16,
            OpKind.Immediate32 or OpKind.Immediate32to64 => PseudoIr.X.U32,
            OpKind.Immediate64 => PseudoIr.X.U64,
            _ => PseudoIr.X.U64
        };

        static PseudoIr.IrType GuessFromRegister(Register r)
        {
            int bits = RegisterBitWidth(r);
            bool signed = false;
            return new PseudoIr.IntType(bits == 0 ? 64 : bits, signed);
        }
    }

    private PseudoIr.Expr ParseTextAsExpr(string t)
    {
        if (t == "rcx") return PseudoIr.X.R("rcx");
        if (t == "ecx") return PseudoIr.X.R("ecx");
        if (t == "cx") return PseudoIr.X.R("cx");
        if (t == "0") return PseudoIr.X.C(0);
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (ulong.TryParse(t.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var uv))
                return new PseudoIr.UConst(uv, 64);
        }
        return PseudoIr.X.R(t);
    }

    private static (string left, string right, int width, bool lIsConst, long lConst, bool rIsConst, long rConst)
        ExtractCmpLike(in Instruction i, Ctx ctx)
    {
        string l = OperandText(i, 0, ctx);
        string r = OperandText(i, 1, ctx);
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

    // --------- Call handling --------------------------------------------------

    private bool TryRenderMemsetCallSiteIR(in Instruction i, Ctx ctx, out PseudoIr.Stmt stmt)
    {
        stmt = null!;
        if (i.Mnemonic != Mnemonic.Call) return false;

        var dst = PseudoIr.X.R(Friendly(ctx, Register.RCX));
        var val = PseudoIr.X.R(Friendly(ctx, Register.EDX));
        var siz = PseudoIr.X.R(Friendly(ctx, Register.R8D));

        if (!LooksLikePointerVar(dst) || !IsSmallLiteralOrZero(val)) return false;

        var call = PseudoIr.X.Call("memset",
            new PseudoIr.Expr[] {
                new PseudoIr.CastExpr(dst, new PseudoIr.PointerType(new PseudoIr.VoidType()), PseudoIr.CastKind.Reinterpret),
                val, siz
            });
        ctx.LastWasCall = true;
        stmt = new PseudoIr.CallStmt(call);
        return true;

        static bool LooksLikePointerVar(PseudoIr.Expr e)
            => e is PseudoIr.RegExpr rr && (rr.Name.Contains("rsp", StringComparison.OrdinalIgnoreCase)
                                            || rr.Name.StartsWith("p", StringComparison.Ordinal)
                                            || rr.Name.Contains("+ 0x", StringComparison.Ordinal));

        static bool IsSmallLiteralOrZero(PseudoIr.Expr e)
        {
            if (e is PseudoIr.Const c) return c.Value >= -255 && c.Value <= 255;
            return e is PseudoIr.RegExpr r && (r.Name == "0" || r.Name == "eax" || r.Name == "edx");
        }
    }

    private PseudoIr.CallExpr BuildCallExpr(in Instruction i, Ctx ctx, out bool assignsToRet)
    {
        assignsToRet = true;
        string targetRepr = "indirect_call";
        PseudoIr.Expr? addr = null;

        if (HasNearTarget(i))
        {
            var t = i.NearBranchTarget;
            targetRepr = $"sub_{t:X}";
        }
        else
        {
            if (i.Op0Kind == OpKind.Memory && i.IsIPRelativeMemoryOperand)
            {
                addr = new PseudoIr.UConst(i.IPRelativeMemoryAddress, 64);
                if (ctx.Opt.ResolveImportName is not null)
                {
                    string? name = ctx.Opt.ResolveImportName(i.IPRelativeMemoryAddress);
                    if (!string.IsNullOrEmpty(name)) { targetRepr = name; addr = null; }
                }
            }
        }

        var args = new PseudoIr.Expr[]
        {
            PseudoIr.X.R(Friendly(ctx, Register.RCX)),
            PseudoIr.X.R(Friendly(ctx, Register.RDX)),
            PseudoIr.X.R(Friendly(ctx, Register.R8)),
            PseudoIr.X.R(Friendly(ctx, Register.R9)),
        };

        ctx.RegName[Register.RAX] = "ret";

        return addr is null
            ? new PseudoIr.CallExpr(PseudoIr.CallTarget.ByName(NormalizeCallName(targetRepr)), args)
            : new PseudoIr.CallExpr(PseudoIr.CallTarget.Indirect(addr), args);

        static string NormalizeCallName(string s)
        {
            int bang = s.IndexOf('!');
            if (bang >= 0 && bang + 1 < s.Length) return s[(bang + 1)..];
            return s;
        }
    }

    // --------- Misc helpers ---------------------------------------------------

    private static bool IsPrologueOrEpilogue(in Instruction i)
    {
        bool IsNonVolatile(Register r) =>
            r == Register.RBX || r == Register.RBP || r == Register.RSI || r == Register.RDI ||
            r == Register.R12 || r == Register.R13 || r == Register.R14 || r == Register.R15;

        if (i.Mnemonic == Mnemonic.Push && IsNonVolatile(i.Op0Register)) return true;
        if (i.Mnemonic == Mnemonic.Pop && IsNonVolatile(i.Op0Register)) return true;

        if (i.Mnemonic == Mnemonic.Mov && i.Op0Register == Register.RBP && i.Op1Register == Register.RSP) return true;
        if (i.Mnemonic == Mnemonic.Lea && i.Op0Register == Register.RBP && i.MemoryBase == Register.RSP) return true;

        if (i.Mnemonic == Mnemonic.Sub && i.Op0Register == Register.RSP && IsImmediate(GetOpKind(i, 1))) return true;
        if (i.Mnemonic == Mnemonic.Add && i.Op0Register == Register.RSP && IsImmediate(GetOpKind(i, 1))) return true;

        return false;
    }

    private static string AsVarName(Ctx ctx, Register r)
    {
        if (ctx.RegName.TryGetValue(r, out var nm))
        {
            if (nm.Contains("gs:0x60", StringComparison.OrdinalIgnoreCase)) return "peb";
            if (TryParseRspOffset(nm, out long off) && ctx.RspAliasByOffset.TryGetValue(off, out var alias)) return alias;
            return nm;
        }
        return r.ToString().ToLowerInvariant();
    }
    private static string Friendly(Ctx ctx, Register r) => AsVarName(ctx, r);

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
        if (r == Register.AL || r == Register.CL || r == Register.DL || r == Register.BL || (r >= Register.SPL && r <= Register.R15L)) return 8;
        if (r == Register.AX || r == Register.CX || r == Register.DX || r == Register.BX || r == Register.SP || r == Register.BP || r == Register.SI || r == Register.DI || (r >= Register.R8W && r <= Register.R15W)) return 16;
        if (r == Register.EAX || r == Register.ECX || r == Register.EDX || r == Register.EBX || r == Register.ESP || r == Register.EBP || r == Register.ESI || r == Register.EDI || (r >= Register.R8D && r <= Register.R15D)) return 32;
        if (r == Register.RAX || r == Register.RCX || r == Register.RDX || r == Register.RBX || r == Register.RSP || r == Register.RBP || r == Register.RSI || r == Register.RDI || (r >= Register.R8 && r <= Register.R15)) return 64;
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
        => i.Op0Kind == OpKind.NearBranch16 || i.Op0Kind == OpKind.NearBranch32 || i.Op0Kind == OpKind.NearBranch64
           || i.Op1Kind == OpKind.NearBranch16 || i.Op1Kind == OpKind.NearBranch32 || i.Op1Kind == OpKind.NearBranch64;

    private static string ImmString(in Instruction i, OpKind kind)
    {
        long v = ParseImmediate(i, kind);
        return v >= 10 ? "0x" + v.ToString("X") : v.ToString(CultureInfo.InvariantCulture);
    }

    private static long ParseImmediate(in Instruction i, OpKind kind)
        => kind switch
        {
            OpKind.Immediate8 or OpKind.Immediate8to16 or OpKind.Immediate8to32 or OpKind.Immediate8to64 => (sbyte)i.Immediate8,
            OpKind.Immediate16 => (short)i.Immediate16,
            OpKind.Immediate32 or OpKind.Immediate32to64 => (int)i.Immediate32,
            OpKind.Immediate64 => (long)i.Immediate64,
            _ => 0
        };

    private static bool IsImmediate(OpKind k) =>
        k == OpKind.Immediate8 || k == OpKind.Immediate16 || k == OpKind.Immediate32 || k == OpKind.Immediate64
        || k == OpKind.Immediate8to16 || k == OpKind.Immediate8to32 || k == OpKind.Immediate8to64 || k == OpKind.Immediate32to64;

    private static string MemAddrString(in Instruction i)
    {
        var sb = new StringBuilder();

        if (i.MemorySegment == Register.FS || i.MemorySegment == Register.GS)
            sb.Append(i.MemorySegment.ToString().ToLowerInvariant()).Append(":");

        if (i.IsIPRelativeMemoryOperand)
        {
            ulong target = i.IPRelativeMemoryAddress;
            sb.Append("0x").Append(target.ToString("X"));
            return sb.ToString();
        }

        bool any = false;
        if (i.MemoryBase != Register.None) { sb.Append(i.MemoryBase.ToString().ToLowerInvariant()); any = true; }
        if (i.MemoryIndex != Register.None)
        {
            if (any) sb.Append(" + ");
            sb.Append(i.MemoryIndex.ToString().ToLowerInvariant());
            int scale = i.MemoryIndexScale;
            if (scale != 1) sb.Append(" * ").Append(scale.ToString(CultureInfo.InvariantCulture));
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

    private static bool TryParseRspOffset(string expr, out long off)
    {
        off = 0;
        expr = expr.Replace(" ", "");
        int i = expr.IndexOf("rsp+0x", StringComparison.OrdinalIgnoreCase);
        if (i >= 0)
        {
            string hex = expr.Substring(i + 6);
            return TryParseHex(hex, out off);
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
        int end = 0;
        while (end < s.Length && Uri.IsHexDigit(s[end])) end++;
        if (end == 0) { v = 0; return false; }
        return long.TryParse(s.AsSpan(0, end), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v);
    }

    private static OpKind GetOpKind(in Instruction i, int n) => n switch { 0 => i.Op0Kind, 1 => i.Op1Kind, 2 => i.Op2Kind, 3 => i.Op3Kind, _ => OpKind.Register };
    private static Register GetOpRegister(in Instruction i, int n) => n switch { 0 => i.Op0Register, 1 => i.Op1Register, 2 => i.Op2Register, 3 => i.Op3Register, _ => Register.None };
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

    // ============================================================
    //  Refinement passes (simple, safe)
    // ============================================================

    private static class IrPasses
    {
        /// <summary>Replace RegExpr("p1..p4") with ParamExpr references.</summary>
        public static void ReplaceParamRegsWithParams(PseudoIr.FunctionIR fn)
        {
            var map = fn.Parameters.ToDictionary(p => p.Name, p => p);
            foreach (var bb in fn.Blocks)
                for (int i = 0; i < bb.Statements.Count; i++)
                    bb.Statements[i] = RewriteStmt(bb.Statements[i], e =>
                    {
                        if (e is PseudoIr.RegExpr r && map.TryGetValue(r.Name, out var param))
                            return new PseudoIr.ParamExpr(param.Name, param.Index);
                        return e;
                    });
        }

        /// <summary>Map "return CONST" to named constants via provider enum type.</summary>
        public static void MapNamedReturnConstants(PseudoIr.FunctionIR fn, PseudoIr.IConstantNameProvider provider, string enumFullName)
        {
            foreach (var bb in fn.Blocks)
            {
                for (int i = 0; i < bb.Statements.Count; i++)
                {
                    if (bb.Statements[i] is PseudoIr.ReturnStmt rs && rs.Value != null)
                    {
                        if (TryEvalULong(rs.Value, out var v) && provider.TryFormatValue(enumFullName, v, out var name))
                        {
                            bb.Statements[i] = new PseudoIr.ReturnStmt(new PseudoIr.SymConst(v, 32, name));
                        }
                    }
                }
            }

            static bool TryEvalULong(PseudoIr.Expr e, out ulong v)
            {
                switch (e)
                {
                    case PseudoIr.UConst uc: v = uc.Value; return true;
                    case PseudoIr.Const c: v = unchecked((ulong)c.Value); return true;
                    case PseudoIr.SymConst sc: v = sc.Value; return true;
                }
                v = 0; return false;
            }
        }

        /// <summary>Drop trivial assigns like "x = x;" and "__pseudo(nop)" around nothing.</summary>
        public static void SimplifyRedundantAssign(PseudoIr.FunctionIR fn)
        {
            foreach (var bb in fn.Blocks)
            {
                var dst = new List<PseudoIr.Stmt>(bb.Statements.Count);
                foreach (var s in bb.Statements)
                {
                    if (s is PseudoIr.AssignStmt a && ExprEq(a.Lhs, a.Rhs)) continue;
                    dst.Add(s);
                }
                bb.Statements.Clear();
                bb.Statements.AddRange(dst);
            }

            static bool ExprEq(PseudoIr.Expr x, PseudoIr.Expr y)
            {
                if (ReferenceEquals(x, y)) return true;
                return x switch
                {
                    PseudoIr.RegExpr rx when y is PseudoIr.RegExpr ry => string.Equals(rx.Name, ry.Name, StringComparison.Ordinal),
                    PseudoIr.ParamExpr px when y is PseudoIr.ParamExpr py => px.Index == py.Index && px.Name == py.Name,
                    PseudoIr.LocalExpr lx when y is PseudoIr.LocalExpr ly => lx.Name == ly.Name,
                    _ => false
                };
            }
        }

        // --- Rewriter utility ---
        private static PseudoIr.Stmt RewriteStmt(PseudoIr.Stmt s, Func<PseudoIr.Expr, PseudoIr.Expr> f)
        {
            switch (s)
            {
                case PseudoIr.AssignStmt a: return new PseudoIr.AssignStmt(f(a.Lhs), f(a.Rhs));
                case PseudoIr.StoreStmt st: return new PseudoIr.StoreStmt(f(st.Address), f(st.Value), st.ElemType, st.Segment);
                case PseudoIr.CallStmt cs: return new PseudoIr.CallStmt(RewriteCall(cs.Call, f));
                case PseudoIr.IfGotoStmt ig: return new PseudoIr.IfGotoStmt(f(ig.Condition), ig.Target);
                case PseudoIr.GotoStmt or PseudoIr.LabelStmt or PseudoIr.ReturnStmt or PseudoIr.AsmCommentStmt or PseudoIr.PseudoStmt or PseudoIr.NopStmt:
                    if (s is PseudoIr.ReturnStmt r && r.Value != null) return new PseudoIr.ReturnStmt(f(r.Value));
                    return s;
                default: return s;
            }
        }

        private static PseudoIr.CallExpr RewriteCall(PseudoIr.CallExpr c, Func<PseudoIr.Expr, PseudoIr.Expr> f)
        {
            var args = c.Args.Select(f).ToArray();
            PseudoIr.CallTarget target = c.Target.Address is null ? PseudoIr.CallTarget.ByName(c.Target.Symbol!) : PseudoIr.CallTarget.Indirect(f(c.Target.Address));
            return new PseudoIr.CallExpr(target, args);
        }
    }
}