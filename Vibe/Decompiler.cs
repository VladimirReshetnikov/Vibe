// SPDX-License-Identifier: MIT-0
using System.Globalization;
using System.Text;
using Iced.Intel;
using Decoder = Iced.Intel.Decoder;


public sealed class Decompiler
{
    public sealed class Options
    {
        /// <summary>Base address to assume for the function (used for RIP-relative and labels)</summary>
        public ulong BaseAddress { get; set; } = 0x0000000140000000UL;

        /// <summary>Optional pretty function name to show in the pseudocode header</summary>
        public string FunctionName { get; set; } = "func";

        /// <summary>Emit original labels for branch targets (L1, L2, ...)</summary>
        public bool EmitLabels { get; set; } = true;

        /// <summary>Try to detect MSVC prologue/epilogue and hide low-level stack chaff (semantic), but assembly still printed</summary>
        public bool DetectPrologue { get; set; } = true;

        /// <summary>Also emit pseudo lines for cmp/test (helps readability)</summary>
        public bool CommentCompare { get; set; } = true;

        /// <summary>Maximum bytes to decode; null = all provided bytes</summary>
        public int? MaxBytes { get; set; } = null;

        /// <summary>Optional name resolver for indirect/IAT calls.</summary>
        public Func<ulong, string?>? ResolveImportName { get; set; } = null;

        /// <summary>Optional constant provider & enum name used to map return constants to symbols (default: NTSTATUS)</summary>
        public IConstantNameProvider? ConstantProvider { get; set; } = new ConstantDatabase();

        public string ReturnEnumTypeFullName { get; set; } = "Windows.Win32.Foundation.NTSTATUS";
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
        public readonly HashSet<ulong> LabelNeeded = new();
        public readonly List<Instruction> Insns = new();

        // Friendly names for registers (stable aliases only: p1..p4, ret)
        public readonly Dictionary<Register, string> RegName = new();

        public LastCmp? LastCmp;
        public LastBt? LastBt;
        public bool UsesFramePointer;
        public int LocalSize;
        public bool UsesGsPeb;
        public Register LastZeroedXmm = Register.None;
        public bool LastWasCall;

        // RSP alias names for stack regions
        public readonly Dictionary<long, string> RspAliasByOffset = new();

        public ulong StartIp;
        public Ctx(Options opt) { Opt = opt; }
    }

    /// <summary>Entry point: build IR and pretty-print.</summary>
    public string ToPseudoCode(byte[] code, Options? options = null)
    {
        var opt = options ?? new Options();
        var ctx = new Ctx(opt);

        // Decode and analyze
        DecodeFunction(code, ctx);
        AnalyzeLabels(ctx);

        // Build IR
        var fn = BuildFunctionIr(ctx);

        // --- Refinement passes (simple & safe) ---
        Transformations.ReplaceParamRegsWithParams(fn);

        Transformations.FrameObjectClusteringAndRspAlias(fn);              // Pass H
        Transformations.DropRedundantBitTestPseudo(fn);                    // Pass E (cleanup)

        if (opt.ConstantProvider is not null && !string.IsNullOrWhiteSpace(opt.ReturnEnumTypeFullName))
        {
            Transformations.MapNamedReturnConstants(fn, opt.ConstantProvider!, opt.ReturnEnumTypeFullName);
            Transformations.MapNamedRetAssignConstants(fn, opt.ConstantProvider!, opt.ReturnEnumTypeFullName);
        }

        Transformations.SimplifyRedundantAssign(fn);
        Transformations.SimplifyArithmeticIdentities(fn);
        Transformations.SimplifyBooleanTernary(fn);
        Transformations.SimplifyLogicalNots(fn);

        // Pretty print IR
        var pp = new IR.PrettyPrinter(new IR.PrettyPrinter.Options
        {
            EmitHeaderComment = true,
            EmitBlockLabels = false,
            CommentSignednessOnCmp = true,
            UseStdIntNames = true
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

        // Seed stable entry register names
        SeedEntryRegisterNames(ctx);

        while (decoder.IP < stopIp)
        {
            decoder.Decode(out instr);
            ctx.Insns.Add(instr);

            if (IsRet(instr))
                break;
        }

        // Prologue/locals detection
        if (ctx.Opt.DetectPrologue)
            DetectPrologueAndLocals(ctx);

        // gs:[0x60] presence
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
        var ops = new List<string>();
        for (int op = 0; op < i.OpCount; op++)
            ops.Add(QuickFormatOperand(i, op));
        return ops.Count == 0 ? i.Mnemonic.ToString().ToLowerInvariant()
                              : i.Mnemonic.ToString().ToLowerInvariant() + " " + string.Join(", ", ops);
    }

    private static string QuickAsmWithIp(in Instruction i) => $"0x{i.IP:X}: {QuickAsm(i)}";

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
                return ImmString(i, kind);
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

    // --------- Build IR & Pretty-print ---------------------------------------

    private IR.FunctionIR BuildFunctionIr(Ctx ctx)
    {
        var fn = new IR.FunctionIR(ctx.Opt.FunctionName, ctx.Opt.BaseAddress, ctx.StartIp)
        {
            ReturnType = IR.X.U64
        };
        // Parameters (for signature only; body uses RegExpr "pX")
        fn.Parameters.Add(new IR.Parameter("p1", IR.X.U64, 0));
        fn.Parameters.Add(new IR.Parameter("p2", IR.X.U64, 1));
        fn.Parameters.Add(new IR.Parameter("p3", IR.X.U64, 2));
        fn.Parameters.Add(new IR.Parameter("p4", IR.X.U64, 3));

        // Set tags used by pretty-printer for header/body comments
        fn.Tags["UsesFramePointer"] = ctx.UsesFramePointer;
        fn.Tags["LocalSize"] = ctx.LocalSize;

        // Optional PEB alias local with initializer
        if (ctx.UsesGsPeb)
        {
            var init = new IR.CastExpr(
                new IR.CallExpr(IR.CallTarget.ByName("__readgsqword"),
                    new IR.Expr[] { IR.X.C(0x60) }),
                new IR.PointerType(IR.X.U8),
                IR.CastKind.Reinterpret);
            fn.Locals.Add(new IR.LocalVar("peb", new IR.PointerType(IR.X.U8), init));
        }

        var block = new IR.BasicBlock(new IR.LabelSymbol("entry", 0));
        fn.Blocks.Add(block);

        // Walk instructions and append statements
        var ins = ctx.Insns;
        for (int idx = 0; idx < ins.Count; idx++)
        {
            var i = ins[idx];

            // Label line if needed
            if (ctx.Opt.EmitLabels && ctx.LabelByIp.TryGetValue(i.IP, out int lab))
                block.Statements.Add(new IR.LabelStmt(new IR.LabelSymbol($"L{lab}", lab)));

            // Always emit original disassembly as a comment (with IP)
            block.Statements.Add(new IR.AsmStmt(QuickAsmWithIp(i)));

            // Peephole: coalesce zero-store runs into one memset
            if (TryCoalesceZeroStoresIR(ins, idx, ctx, out var zeroStmts, out int consumedZero))
            {
                // Also show the original assembly of each consumed instruction
                for (int t = 1; t < consumedZero; t++)
                    block.Statements.Add(new IR.AsmStmt(QuickAsmWithIp(ins[idx + t])));

                block.Statements.AddRange(zeroStmts);
                idx += consumedZero - 1;
                continue;
            }

            // Peephole: memcpy 16B blocks
            if (TryCoalesceMemcpy16BlocksIR(ins, idx, ctx, out var memcpyStmts, out int consumedMemcpy))
            {
                for (int t = 1; t < consumedMemcpy; t++)
                    block.Statements.Add(new IR.AsmStmt(QuickAsmWithIp(ins[idx + t])));

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
        out List<IR.Stmt> stmts, out int consumed)
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
        IR.Expr? baseAddr = null;
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
            var addr = expectedOff == bytes ? baseAddr : IR.X.Add(baseAddr, IR.X.C(expectedOff - bytes));
            stmts.Add(new IR.PseudoStmt("zero xmm"));
            stmts.Add(new IR.CallStmt(IR.X.Call("memset",
                new IR.Expr[] {
                    new IR.CastExpr(addr, new IR.PointerType(new IR.VoidType()), IR.CastKind.Reinterpret),
                    IR.X.C(0),
                    IR.X.C(bytes)
                })));
            consumed = j - idx;
            return true;
        }
        return false;
    }

    private static bool TryCoalesceMemcpy16BlocksIR(List<Instruction> ins, int idx, Ctx ctx,
        out List<IR.Stmt> stmts, out int consumed)
    {
        stmts = new(); consumed = 0;

        int j = idx;
        IR.Expr? srcBase = null, dstBase = null;
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
            var src = startSrc == 0 ? srcBase : IR.X.Add(srcBase, IR.X.C(startSrc));
            var dst = startDst == 0 ? dstBase : IR.X.Add(dstBase, IR.X.C(startDst));
            stmts.Add(new IR.CallStmt(IR.X.Call("memcpy",
                new IR.Expr[] {
                    new IR.CastExpr(dst, new IR.PointerType(new IR.VoidType()), IR.CastKind.Reinterpret),
                    new IR.CastExpr(src, new IR.PointerType(new IR.VoidType()), IR.CastKind.Reinterpret),
                    IR.X.C(bytes)
                })));
            consumed = j - idx;
            return true;
        }

        return false;
    }

    private static bool TrySplitBasePlusOffset(IR.Expr addr, out IR.Expr baseExpr, out long off)
    {
        // Recognize (base + const) or (base - const); allow plain base (offset 0)
        baseExpr = null!;
        off = 0;

        if (addr is IR.BinOpExpr bop && (bop.Op == IR.BinOp.Add || bop.Op == IR.BinOp.Sub))
        {
            if (bop.Right is IR.Const c)
            {
                baseExpr = bop.Left;
                off = bop.Op == IR.BinOp.Add ? c.Value : -c.Value;
                return true;
            }
            if (bop.Right is IR.UConst uc)
            {
                baseExpr = bop.Left;
                var v = unchecked((long)uc.Value);
                off = bop.Op == IR.BinOp.Add ? v : -v;
                return true;
            }
            return false;
        }

        // Accept simple base terms as base+0
        if (addr is IR.RegExpr || addr is IR.LocalExpr || addr is IR.AddrOfExpr)
        {
            baseExpr = addr;
            off = 0;
            return true;
        }

        return false;
    }

    private static bool ExprEquals(IR.Expr a, IR.Expr b)
    {
        if (a is IR.RegExpr ra && b is IR.RegExpr rb) return string.Equals(ra.Name, rb.Name, StringComparison.Ordinal);
        if (a is IR.LocalExpr la && b is IR.LocalExpr lb) return string.Equals(la.Name, lb.Name, StringComparison.Ordinal);
        if (a is IR.AddrOfExpr aa && b is IR.AddrOfExpr ab) return ExprEquals(aa.Operand, ab.Operand);
        if (a is IR.BinOpExpr ba && b is IR.BinOpExpr bb && ba.Op == bb.Op) return ExprEquals(ba.Left, bb.Left) && ExprEquals(ba.Right, bb.Right);
        return false;
    }

    // --------- Instruction translation → IR ----------------------------------

    private IEnumerable<IR.Stmt> TranslateInstructionToStmts(Instruction i, Ctx ctx)
    {
        ctx.LastWasCall = false;

        // setcc → ternary assign
        if (IsSetcc(i))
        {
            var dest = LhsExpr(i, ctx);
            var cond = ConditionExpr(i, ctx);
            yield return new IR.AssignStmt(dest, new IR.TernaryExpr(cond, IR.X.C(1), IR.X.C(0)));
            ctx.LastBt = null;
            yield break;
        }

        // cmovcc
        if (IsCmovcc(i))
        {
            var dest = LhsExpr(i, ctx);
            var src = OperandExpr(i, 1, ctx, forRead: true);
            var cond = ConditionExpr(i, ctx);
            yield return new IR.AssignStmt(dest, new IR.TernaryExpr(cond, src, dest));
            ctx.LastBt = null;
            yield break;
        }

        // jcc
        if (i.FlowControl == FlowControl.ConditionalBranch)
        {
            var cond = ConditionExpr(i, ctx);
            if (HasNearTarget(i) && ctx.LabelByIp.TryGetValue(i.NearBranchTarget, out int lab))
                yield return new IR.IfGotoStmt(cond, new IR.LabelSymbol($"L{lab}", lab));
            else
                yield return new IR.PseudoStmt($"if ({ExprToText(cond)}) goto 0x{i.NearBranchTarget:X}");
            ctx.LastBt = null;
            yield break;
        }

        // Hide prologue/epilogue (semantic), but assembly is already printed separately
        if (ctx.Opt.DetectPrologue && IsPrologueOrEpilogue(i))
        {
            yield break;
        }

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
                        yield return new IR.StoreStmt(addr, rhs, ty, Seg(i));
                    }
                    else
                    {
                        var lhs = LhsExpr(i, ctx);
                        var rhs = OperandExpr(i, 1, ctx, forRead: true);
                        yield return new IR.AssignStmt(lhs, rhs);
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
                    var kind = i.Mnemonic == Mnemonic.Movzx ? IR.CastKind.ZeroExtend : IR.CastKind.SignExtend;
                    yield return new IR.AssignStmt(dst, new IR.CastExpr(src, dstTy, kind));
                    yield break;
                }
            case Mnemonic.Lea:
                {
                    var dst = LhsExpr(i, ctx);
                    var addr = AddressExpr(i, ctx, isLoadOrStoreAddr: false);
                    yield return new IR.AssignStmt(dst, addr);
                    yield break;
                }

            // Bitwise & zero idioms
            case Mnemonic.Xor:
                {
                    var a = LhsExpr(i, ctx);
                    var b = OperandExpr(i, 1, ctx, forRead: true);
                    if (i.Op0Kind == OpKind.Register && i.Op1Kind == OpKind.Register && i.Op0Register == i.Op1Register)
                        yield return new IR.AssignStmt(a, IR.X.C(0));
                    else
                        yield return new IR.AssignStmt(a, new IR.BinOpExpr(IR.BinOp.Xor, a, b));
                    yield break;
                }
            case Mnemonic.Or:
                {
                    var a = LhsExpr(i, ctx);
                    var b = OperandExpr(i, 1, ctx, forRead: true);
                    yield return new IR.AssignStmt(a, new IR.BinOpExpr(IR.BinOp.Or, a, b));
                    yield break;
                }
            case Mnemonic.And:
                {
                    var a = LhsExpr(i, ctx);
                    var b = OperandExpr(i, 1, ctx, forRead: true);
                    yield return new IR.AssignStmt(a, new IR.BinOpExpr(IR.BinOp.And, a, b));
                    yield break;
                }
            case Mnemonic.Not:
                {
                    var a = LhsExpr(i, ctx);
                    yield return new IR.AssignStmt(a, new IR.UnOpExpr(IR.UnOp.Not, a));
                    yield break;
                }
            case Mnemonic.Neg:
                {
                    var a = LhsExpr(i, ctx);
                    yield return new IR.AssignStmt(a, new IR.UnOpExpr(IR.UnOp.Neg, a));
                    yield break;
                }

            // Arithmetic
            case Mnemonic.Add:
                {
                    var a = LhsExpr(i, ctx);
                    var b = OperandExpr(i, 1, ctx, forRead: true);
                    yield return new IR.AssignStmt(a, new IR.BinOpExpr(IR.BinOp.Add, a, b));
                    yield break;
                }
            case Mnemonic.Sub:
                {
                    var a = LhsExpr(i, ctx);
                    var b = OperandExpr(i, 1, ctx, forRead: true);
                    yield return new IR.AssignStmt(a, new IR.BinOpExpr(IR.BinOp.Sub, a, b));
                    yield break;
                }
            case Mnemonic.Inc:
                {
                    var a = LhsExpr(i, ctx);
                    yield return new IR.AssignStmt(a, new IR.BinOpExpr(IR.BinOp.Add, a, IR.X.C(1)));
                    yield break;
                }
            case Mnemonic.Dec:
                {
                    var a = LhsExpr(i, ctx);
                    yield return new IR.AssignStmt(a, new IR.BinOpExpr(IR.BinOp.Sub, a, IR.X.C(1)));
                    yield break;
                }

            case Mnemonic.Imul:
                {
                    if (i.OpCount == 2 && i.Op0Kind == OpKind.Register)
                    {
                        var a = LhsExpr(i, ctx);
                        var b = OperandExpr(i, 1, ctx, forRead: true);
                        yield return new IR.AssignStmt(a, new IR.BinOpExpr(IR.BinOp.Mul, a, b));
                    }
                    else if (i.OpCount == 3)
                    {
                        var dst = LhsExpr(i, ctx);
                        var src = OperandExpr(i, 1, ctx, forRead: true);
                        var imm = OperandExpr(i, 2, ctx, forRead: true);
                        yield return new IR.AssignStmt(dst, new IR.BinOpExpr(IR.BinOp.Mul, src, imm));
                    }
                    else
                    {
                        yield return new IR.PseudoStmt("RDX:RAX = RAX * op (signed)");
                    }
                    yield break;
                }
            case Mnemonic.Mul:
                yield return new IR.PseudoStmt("RDX:RAX = RAX * op (unsigned)"); yield break;
            case Mnemonic.Idiv:
                yield return new IR.PseudoStmt("RAX = (RDX:RAX) / op; RDX = remainder (signed)"); yield break;
            case Mnemonic.Div:
                yield return new IR.PseudoStmt("RAX = (RDX:RAX) / op; RDX = remainder (unsigned)"); yield break;

            // Shifts/rotates
            case Mnemonic.Shl:
            case Mnemonic.Sal:
                {
                    var a = LhsExpr(i, ctx);
                    var c = OperandExpr(i, 1, ctx, forRead: true);
                    yield return new IR.AssignStmt(a, new IR.BinOpExpr(IR.BinOp.Shl, a, c));
                    yield break;
                }
            case Mnemonic.Shr:
                {
                    var a = LhsExpr(i, ctx);
                    var c = OperandExpr(i, 1, ctx, forRead: true);
                    yield return new IR.AssignStmt(a, new IR.BinOpExpr(IR.BinOp.Shr, a, c));
                    yield break;
                }
            case Mnemonic.Sar:
                {
                    var a = LhsExpr(i, ctx);
                    var c = OperandExpr(i, 1, ctx, forRead: true);
                    yield return new IR.AssignStmt(a, new IR.BinOpExpr(IR.BinOp.Sar, a, c));
                    yield break;
                }
            case Mnemonic.Rol:
                {
                    var a = LhsExpr(i, ctx);
                    var c = OperandExpr(i, 1, ctx, forRead: true);
                    yield return new IR.AssignStmt(a, new IR.IntrinsicExpr("rotl", new[] { a, c }));
                    yield break;
                }
            case Mnemonic.Ror:
                {
                    var a = LhsExpr(i, ctx);
                    var c = OperandExpr(i, 1, ctx, forRead: true);
                    yield return new IR.AssignStmt(a, new IR.IntrinsicExpr("rotr", new[] { a, c }));
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
                    yield return new IR.PseudoStmt($"CF = bit({v}, {ix})");
                    yield break;
                }

            // Flag setters
            case Mnemonic.Cmp:
                {
                    var (l, r, w, lc, lval, rc, rval) = ExtractCmpLike(i, ctx);
                    ctx.LastCmp = new LastCmp { Left = l, Right = r, BitWidth = w, IsTest = false, Ip = i.IP, LeftIsConst = lc, RightIsConst = rc, LeftConst = lval, RightConst = rval };
                    if (ctx.Opt.CommentCompare) yield return new IR.PseudoStmt($"compare {l}, {r}");
                    yield break;
                }
            case Mnemonic.Test:
                {
                    var (l, r, w, lc, lval, rc, rval) = ExtractCmpLike(i, ctx);
                    ctx.LastCmp = new LastCmp { Left = l, Right = r, BitWidth = w, IsTest = true, Ip = i.IP, LeftIsConst = lc, RightIsConst = rc, LeftConst = lval, RightConst = rval };
                    if (ctx.Opt.CommentCompare) yield return new IR.PseudoStmt($"test {l}, {r}");
                    yield break;
                }

            // Branching (unconditional)
            case Mnemonic.Jmp:
                if (HasNearTarget(i) && ctx.LabelByIp.TryGetValue(i.NearBranchTarget, out int lab))
                    yield return new IR.GotoStmt(new IR.LabelSymbol($"L{lab}", lab));
                else
                    yield return new IR.PseudoStmt($"jmp 0x{i.NearBranchTarget:X}");
                yield break;

            // Calls / returns
            case Mnemonic.Call:
                {
                    // memset(rcx, edx, r8d) heuristic
                    if (TryRenderMemsetCallSiteIR(i, ctx, out var ms))
                    {
                        yield return ms;
                        yield break;
                    }

                    var callExpr = BuildCallExpr(i, ctx, out bool assignsToRet);
                    if (assignsToRet)
                        yield return new IR.AssignStmt(IR.X.R("ret"), callExpr);
                    else
                        yield return new IR.CallStmt(callExpr);
                    ctx.LastWasCall = true;
                    yield break;
                }

            case Mnemonic.Ret:
            case Mnemonic.Retf:
                yield return new IR.ReturnStmt(IR.X.R("ret"));
                yield break;

            // Push / Pop (assembly already printed)
            case Mnemonic.Push:
            case Mnemonic.Pop:
                yield return new IR.NopStmt(); yield break;

            // String ops
            case Mnemonic.Movsb:
            case Mnemonic.Movsw:
            case Mnemonic.Movsd:
            case Mnemonic.Movsq:
                if (i.HasRepPrefix)
                {
                    int sz = i.Mnemonic switch { Mnemonic.Movsb => 1, Mnemonic.Movsw => 2, Mnemonic.Movsd => 4, _ => 8 };
                    yield return new IR.CallStmt(IR.X.Call("memcpy",
                        new IR.Expr[] { IR.X.R("rdi"), IR.X.R("rsi"), IR.X.Mul(IR.X.R("rcx"), IR.X.C(sz)) }));
                }
                else yield return new IR.PseudoStmt("movs (element size varies)");
                yield break;

            case Mnemonic.Stosb:
            case Mnemonic.Stosw:
            case Mnemonic.Stosd:
            case Mnemonic.Stosq:
                if (i.HasRepPrefix)
                {
                    int sz = i.Mnemonic switch { Mnemonic.Stosb => 1, Mnemonic.Stosw => 2, Mnemonic.Stosd => 4, _ => 8 };
                    string val = sz switch { 1 => "al", 2 => "ax", 4 => "eax", _ => "rax" };
                    yield return new IR.CallStmt(IR.X.Call("memset",
                        new IR.Expr[] { IR.X.R("rdi"), IR.X.R(val), IR.X.Mul(IR.X.R("rcx"), IR.X.C(sz)) }));
                }
                else yield return new IR.PseudoStmt("stos (element size varies)");
                yield break;

            // SSE zero idioms and stores
            case Mnemonic.Xorps:
            case Mnemonic.Pxor:
                if (i.Op0Kind == OpKind.Register && i.Op1Kind == OpKind.Register &&
                    GetOpRegister(i, 0) == GetOpRegister(i, 1) &&
                    RegisterBitWidth(GetOpRegister(i, 0)) == 128)
                {
                    ctx.LastZeroedXmm = GetOpRegister(i, 0);
                    yield return new IR.PseudoStmt("zero xmm");
                    yield break;
                }
                break;

            case Mnemonic.Movups:
            case Mnemonic.Movaps:
            case Mnemonic.Movdqu:
                if (i.Op0Kind == OpKind.Memory && i.Op1Kind == OpKind.Register &&
                    RegisterBitWidth(GetOpRegister(i, 1)) == 128 &&
                    GetOpRegister(i, 1) == ctx.LastZeroedXmm)
                {
                    var addr = AddressExpr(i, ctx, isLoadOrStoreAddr: true);
                    yield return new IR.CallStmt(IR.X.Call("memset",
                        new IR.Expr[] { new IR.CastExpr(addr, new IR.PointerType(new IR.VoidType()), IR.CastKind.Reinterpret), IR.X.C(0), IR.X.C(16) }));
                    ctx.LastZeroedXmm = Register.None;
                    yield break;
                }
                break;

            // Scalar FP ops → "dst = dst op src;"
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
                        Mnemonic.Addss or Mnemonic.Addsd => IR.BinOp.Add,
                        Mnemonic.Subss or Mnemonic.Subsd => IR.BinOp.Sub,
                        Mnemonic.Mulss or Mnemonic.Mulsd => IR.BinOp.Mul,
                        _ => IR.BinOp.UDiv
                    };
                    var dst = LhsExpr(i, ctx);
                    var src = OperandExpr(i, 1, ctx, forRead: true);
                    yield return new IR.AssignStmt(dst, new IR.BinOpExpr(op, dst, src));
                    yield break;
                }

            // misc
            case Mnemonic.Nop:
                yield return new IR.NopStmt(); yield break;
            case Mnemonic.Leave:
                yield return new IR.PseudoStmt("leave (epilogue)"); yield break;
            case Mnemonic.Cdq:
            case Mnemonic.Cqo:
                yield return new IR.PseudoStmt("sign-extend: RDX:RAX <- sign(RAX)"); yield break;
        }

        // Default: nothing semantic (assembly already shown)
        yield return new IR.PseudoStmt("/* no semantic translation */");
    }

    // --------- Condition construction ----------------------------------------

    private IR.Expr ConditionExpr(in Instruction i, Ctx ctx)
    {
        // Special mnemonics
        if (i.Mnemonic == Mnemonic.Jrcxz) return IR.X.Eq(IR.X.R("rcx"), IR.X.C(0));
        if (i.Mnemonic == Mnemonic.Jecxz) return IR.X.Eq(IR.X.R("ecx"), IR.X.C(0));
        if (i.Mnemonic == Mnemonic.Jcxz) return IR.X.Eq(IR.X.R("cx"), IR.X.C(0));

        var cc = i.ConditionCode;

        // BT → CF predicate
        if (ctx.LastBt is { } bt)
        {
            if (cc == ConditionCode.b || cc == ConditionCode.ae)
            {
                var val = ParseTextAsExpr(bt.Value);
                var ix = ParseTextAsExpr(bt.Index);
                var bit = IR.X.And(IR.X.Shr(val, ix), IR.X.C(1));
                return cc == ConditionCode.b ? IR.X.Ne(bit, IR.X.C(0)) : IR.X.Eq(bit, IR.X.C(0));
            }
        }

        var c = ctx.LastCmp;

        // test r,r → simplify je/jne
        if (c != null && c.IsTest && c.Left == c.Right)
        {
            var r = ParseTextAsExpr(c.Left);
            if (cc == ConditionCode.e) return IR.X.Eq(r, IR.X.C(0));
            if (cc == ConditionCode.ne) return IR.X.Ne(r, IR.X.C(0));
        }

        if (c != null)
        {
            var L = ParseTextAsExpr(c.Left);
            var R = ParseTextAsExpr(c.Right);

            switch (cc)
            {
                case ConditionCode.e: return c.IsTest ? IR.X.Eq(IR.X.And(L, R), IR.X.C(0)) : IR.X.Eq(L, R);
                case ConditionCode.ne: return c.IsTest ? IR.X.Ne(IR.X.And(L, R), IR.X.C(0)) : IR.X.Ne(L, R);

                case ConditionCode.l: return IR.X.SLt(L, R);
                case ConditionCode.ge: return IR.X.SGe(L, R);
                case ConditionCode.le: return IR.X.SLe(L, R);
                case ConditionCode.g: return IR.X.SGt(L, R);

                case ConditionCode.b: return IR.X.ULt(L, R);
                case ConditionCode.ae: return IR.X.UGe(L, R);
                case ConditionCode.be: return IR.X.ULe(L, R);
                case ConditionCode.a: return IR.X.UGt(L, R);

                // flag-only fallbacks
                case ConditionCode.s: return IR.X.Ne(IR.X.R("SF"), IR.X.C(0));
                case ConditionCode.ns: return IR.X.Eq(IR.X.R("SF"), IR.X.C(0));
                case ConditionCode.o: return IR.X.Ne(IR.X.R("OF"), IR.X.C(0));
                case ConditionCode.no: return IR.X.Eq(IR.X.R("OF"), IR.X.C(0));
                case ConditionCode.p: return IR.X.Ne(IR.X.R("PF"), IR.X.C(0));
                case ConditionCode.np: return IR.X.Eq(IR.X.R("PF"), IR.X.C(0));
            }
        }

        // Fallback to flags
        return cc switch
        {
            ConditionCode.e => IR.X.Ne(IR.X.R("ZF"), IR.X.C(0)),
            ConditionCode.ne => IR.X.Eq(IR.X.R("ZF"), IR.X.C(0)),
            ConditionCode.l => IR.X.Ne(IR.X.R("SF"), IR.X.R("OF")),
            ConditionCode.ge => IR.X.Eq(IR.X.R("SF"), IR.X.R("OF")),
            ConditionCode.le => IR.X.Or(IR.X.Ne(IR.X.R("CF"), IR.X.C(0)), IR.X.Ne(IR.X.R("ZF"), IR.X.C(0))), // small tweak
            ConditionCode.g => IR.X.And(IR.X.Eq(IR.X.R("ZF"), IR.X.C(0)), IR.X.Eq(IR.X.R("SF"), IR.X.R("OF"))),
            ConditionCode.b => IR.X.Ne(IR.X.R("CF"), IR.X.C(0)),
            ConditionCode.ae => IR.X.Eq(IR.X.R("CF"), IR.X.C(0)),
            ConditionCode.be => IR.X.Or(IR.X.Ne(IR.X.R("CF"), IR.X.C(0)), IR.X.Ne(IR.X.R("ZF"), IR.X.C(0))),
            ConditionCode.a => IR.X.And(IR.X.Eq(IR.X.R("CF"), IR.X.C(0)), IR.X.Eq(IR.X.R("ZF"), IR.X.C(0))),
            _ => new IR.IntrinsicExpr("/* unknown condition */", Array.Empty<IR.Expr>())
        };
    }

    private static string ExprToText(IR.Expr e)
    {
        return e switch
        {
            IR.RegExpr r => r.Name,
            IR.Const c => (c.Value >= 10 ? "0x" + c.Value.ToString("X") : c.Value.ToString()),
            IR.UConst uc => "0x" + uc.Value.ToString("X"),
            _ => "cond"
        };
    }

    // --------- Operand & address helpers -------------------------------------

    private IR.Expr LhsExpr(in Instruction i, Ctx ctx)
    {
        if (i.Op0Kind == OpKind.Register)
            return IR.X.R(AsVarName(ctx, i.Op0Register));
        if (i.Op0Kind == OpKind.Memory)
        {
            var addr = AddressExpr(i, ctx, isLoadOrStoreAddr: true);
            return new IR.LoadExpr(addr, MemType(i), Seg(i));
        }
        return IR.X.R("tmp");
    }

    private IR.Expr OperandExpr(in Instruction i, int op, Ctx ctx, bool forRead)
    {
        var kind = GetOpKind(i, op);
        switch (kind)
        {
            case OpKind.Register:
                return IR.X.R(AsVarName(ctx, GetOpRegister(i, op)));

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
                    return new IR.Const(v, bits);
                }

            case OpKind.NearBranch16:
            case OpKind.NearBranch32:
            case OpKind.NearBranch64:
                return new IR.UConst(i.NearBranchTarget, 64);

            case OpKind.Memory:
                {
                    var addr = AddressExpr(i, ctx, isLoadOrStoreAddr: forRead);
                    var ty = MemType(i);
                    return new IR.LoadExpr(addr, ty, Seg(i));
                }
        }
        return IR.X.R("op");
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

    private static IR.IrType MemType(in Instruction i)
    {
        var ms = i.MemorySize;
        return ms switch
        {
            MemorySize.UInt8 or MemorySize.Int8 => IR.X.U8,
            MemorySize.UInt16 or MemorySize.Int16 => IR.X.U16,
            MemorySize.UInt32 or MemorySize.Int32 or MemorySize.Float32 => IR.X.U32,
            MemorySize.UInt64 or MemorySize.Int64 or MemorySize.Float64 or MemorySize.Unknown => IR.X.U64,
            MemorySize.Packed128_UInt64 or MemorySize.Packed128_Float64 or MemorySize.Packed128_Float32
                or MemorySize.UInt128 or MemorySize.Bcd or MemorySize.Packed128_Int16
                or MemorySize.Packed128_Int32 or MemorySize.Packed128_Int64 => new IR.VectorType(128),
            MemorySize.Packed256_Float32 or MemorySize.Packed256_Float64
                or MemorySize.UInt256 => new IR.VectorType(256),
            MemorySize.Packed512_Float32 or MemorySize.Packed512_Float64
                or MemorySize.UInt512 => new IR.VectorType(512),
            _ => IR.X.U64
        };
    }

    private static IR.SegmentReg Seg(in Instruction i)
    {
        return i.MemorySegment switch
        {
            Register.FS => IR.SegmentReg.FS,
            Register.GS => IR.SegmentReg.GS,
            _ => IR.SegmentReg.None
        };
    }

    private static IR.Expr AddressExpr(in Instruction i, Ctx ctx, bool isLoadOrStoreAddr)
    {
        // [gs:0x60] → peb
        if (i.MemorySegment == Register.GS &&
            i.MemoryBase == Register.None &&
            i.MemoryIndex == Register.None &&
            i.MemoryDisplacement64 == 0x60)
        {
            return IR.X.L("peb");
        }

        // RIP-relative absolute
        if (i.IsIPRelativeMemoryOperand)
        {
            ulong target = i.IPRelativeMemoryAddress;
            return new IR.UConst(target, 64);
        }

        // rbp/ebp locals: negative disp & no index → &local_N
        if ((i.MemoryBase == Register.RBP || i.MemoryBase == Register.EBP) && i.MemoryIndex == Register.None)
        {
            long disp = unchecked((long)i.MemoryDisplacement64);
            if (disp < 0)
            {
                string local = $"local_{-disp}";
                return new IR.AddrOfExpr(IR.X.L(local));
            }
        }

        IR.Expr? expr = null;
        if (i.MemoryBase != Register.None)
        {
            expr = IR.X.R(AsVarName(ctx, i.MemoryBase));
        }
        if (i.MemoryIndex != Register.None)
        {
            var idx = IR.X.R(AsVarName(ctx, i.MemoryIndex));
            int scale = i.MemoryIndexScale;
            IR.Expr scaled = scale == 1 ? idx : IR.X.Mul(idx, IR.X.C(scale));
            expr = expr is null ? scaled : IR.X.Add(expr, scaled);
        }

        long disp64 = unchecked((long)i.MemoryDisplacement64);
        if (disp64 != 0 || expr is null)
        {
            var c = IR.X.C(Math.Abs(disp64));
            expr = expr is null ? c : (disp64 >= 0 ? IR.X.Add(expr, c) : IR.X.Sub(expr, c));
        }
        return expr!;
    }

    private static IR.IrType ValueTypeGuess(in Instruction i, int opIndex)
    {
        var k = GetOpKind(i, opIndex);
        return k switch
        {
            OpKind.Register => GuessFromRegister(GetOpRegister(i, opIndex)),
            OpKind.Memory => MemType(i),
            OpKind.Immediate8 or OpKind.Immediate8to16 or OpKind.Immediate8to32 or OpKind.Immediate8to64 => IR.X.U8,
            OpKind.Immediate16 => IR.X.U16,
            OpKind.Immediate32 or OpKind.Immediate32to64 => IR.X.U32,
            OpKind.Immediate64 => IR.X.U64,
            _ => IR.X.U64
        };

        static IR.IrType GuessFromRegister(Register r)
        {
            int bits = RegisterBitWidth(r);
            bool signed = false;
            return new IR.IntType(bits == 0 ? 64 : bits, signed);
        }
    }

    private IR.Expr ParseTextAsExpr(string t)
    {
        if (t == "rcx") return IR.X.R("rcx");
        if (t == "ecx") return IR.X.R("ecx");
        if (t == "cx") return IR.X.R("cx");
        if (t == "0") return IR.X.C(0);
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (ulong.TryParse(t.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var uv))
                return new IR.UConst(uv, 64);
        }
        return IR.X.R(t);
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

    private bool TryRenderMemsetCallSiteIR(in Instruction i, Ctx ctx, out IR.Stmt stmt)
    {
        stmt = null!;
        if (i.Mnemonic != Mnemonic.Call) return false;

        var dst = IR.X.R(Friendly(ctx, Register.RCX));
        var val = IR.X.R(Friendly(ctx, Register.EDX));
        var siz = IR.X.R(Friendly(ctx, Register.R8D));

        // Heuristic (same as before): small value and pointer-ish dst
        if (!LooksLikePointerVar(dst) || !IsSmallLiteralOrZero(val)) return false;

        var call = IR.X.Call("memset",
            new IR.Expr[] {
                new IR.CastExpr(dst, new IR.PointerType(new IR.VoidType()), IR.CastKind.Reinterpret),
                val, siz
            });
        ctx.LastWasCall = true;
        stmt = new IR.CallStmt(call);
        return true;

        static bool LooksLikePointerVar(IR.Expr e)
        {
            return e is IR.RegExpr rr && (rr.Name.Contains("rsp", StringComparison.OrdinalIgnoreCase)
                                             || rr.Name.StartsWith("p", StringComparison.Ordinal)
                                             || rr.Name.Contains("+ 0x", StringComparison.Ordinal));
        }
        static bool IsSmallLiteralOrZero(IR.Expr e)
        {
            if (e is IR.Const c) return c.Value >= -255 && c.Value <= 255;
            return e is IR.RegExpr r && (r.Name == "0" || r.Name == "eax" || r.Name == "edx");
        }
    }

    private IR.CallExpr BuildCallExpr(in Instruction i, Ctx ctx, out bool assignsToRet)
    {
        assignsToRet = true;

        string targetRepr = "indirect_call";
        IR.Expr? addr = null;

        if (HasNearTarget(i))
        {
            var t = i.NearBranchTarget;
            targetRepr = $"sub_{t:X}";
        }
        else
        {
            if (i.Op0Kind == OpKind.Memory && i.IsIPRelativeMemoryOperand)
            {
                addr = new IR.UConst(i.IPRelativeMemoryAddress, 64);
                if (ctx.Opt.ResolveImportName is not null)
                {
                    string? name = ctx.Opt.ResolveImportName(i.IPRelativeMemoryAddress);
                    if (!string.IsNullOrEmpty(name)) { targetRepr = name; addr = null; }
                }
            }
        }

        var args = new IR.Expr[]
        {
            IR.X.R(Friendly(ctx, Register.RCX)),
            IR.X.R(Friendly(ctx, Register.RDX)),
            IR.X.R(Friendly(ctx, Register.R8)),
            IR.X.R(Friendly(ctx, Register.R9)),
        };

        ctx.RegName[Register.RAX] = "ret";

        return addr is null
            ? new IR.CallExpr(IR.CallTarget.ByName(targetRepr), args)
            : new IR.CallExpr(IR.CallTarget.Indirect(addr), args);
    }

    // --------- Misc helpers (from old emitter) -------------------------------

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
    {
        return i.Op0Kind == OpKind.NearBranch16
            || i.Op0Kind == OpKind.NearBranch32
            || i.Op0Kind == OpKind.NearBranch64
            || i.Op1Kind == OpKind.NearBranch16
            || i.Op1Kind == OpKind.NearBranch32
            || i.Op1Kind == OpKind.NearBranch64;
    }

    private static string ImmString(in Instruction i, OpKind kind)
    {
        long v = ParseImmediate(i, kind);
        return v >= 10 ? "0x" + v.ToString("X") : v.ToString(CultureInfo.InvariantCulture);
    }

    private static long ParseImmediate(in Instruction i, OpKind kind)
    {
        return kind switch
        {
            OpKind.Immediate8 or OpKind.Immediate8to16 or OpKind.Immediate8to32 or OpKind.Immediate8to64 => (sbyte)i.Immediate8,
            OpKind.Immediate16 => (short)i.Immediate16,
            OpKind.Immediate32 or OpKind.Immediate32to64 => (int)i.Immediate32,
            OpKind.Immediate64 => (long)i.Immediate64,
            _ => 0
        };
    }

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
