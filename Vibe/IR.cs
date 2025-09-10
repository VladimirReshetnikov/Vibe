// SPDX-License-Identifier: MIT-0
using System.Globalization;
using System.Text;

public static class IR
{
    // ============================================================
    //  Types
    // ============================================================

    public abstract record IrType;

    public sealed record VoidType() : IrType
    {
        public override string ToString() => "void";
    }

    public sealed record IntType(int Bits, bool IsSigned) : IrType
    {
        public override string ToString() => (IsSigned ? "i" : "u") + Bits;
    }

    public sealed record FloatType(int Bits) : IrType
    {
        public override string ToString() => "f" + Bits;
    }

    public sealed record PointerType(IrType Element) : IrType
    {
        public override string ToString() => $"ptr({Element})";
    }

    public sealed record VectorType(int Bits) : IrType
    {
        public override string ToString() => $"v{Bits}";
    }

    /// <summary>Use for unknown/opaque or "best-effort" types.</summary>
    public sealed record UnknownType(string? Note = null) : IrType
    {
        public override string ToString() => Note is null ? "unknown" : $"unknown:{Note}";
    }

    // ============================================================
    //  Expressions
    // ============================================================

    public abstract record Expr
    {
        /// <summary>Optional static type. Passes may fill this in.</summary>
        public IrType? Type { get; init; }
    }

    public sealed record Const(long Value, int Bits) : Expr;
    public sealed record UConst(ulong Value, int Bits) : Expr;

    /// <summary>Named constant with a pretty enum-like name (e.g., STATUS_*).</summary>
    public sealed record SymConst(ulong Value, int Bits, string Name) : Expr;

    /// <summary>Named register or pseudo-register.</summary>
    public sealed record RegExpr(string Name) : Expr;

    /// <summary>Function parameter reference (printed as its name).</summary>
    public sealed record ParamExpr(string Name, int Index) : Expr;

    /// <summary>Named local variable reference.</summary>
    public sealed record LocalExpr(string Name) : Expr;

    public sealed record SegmentBaseExpr(SegmentReg Segment) : Expr;

    /// <summary>Address-of: &expr</summary>
    public sealed record AddrOfExpr(Expr Operand) : Expr;

    /// <summary>Memory load: *((T*)addr) (optionally with FS/GS segment)</summary>
    public sealed record LoadExpr(Expr Address, IrType ElemType, SegmentReg Segment = SegmentReg.None) : Expr;

    public sealed record BinOpExpr(BinOp Op, Expr Left, Expr Right) : Expr;
    public sealed record UnOpExpr(UnOp Op, Expr Operand) : Expr;
    public sealed record CompareExpr(CmpOp Op, Expr Left, Expr Right) : Expr;
    public sealed record TernaryExpr(Expr Condition, Expr WhenTrue, Expr WhenFalse) : Expr;

    public sealed record CastExpr(Expr Value, IrType TargetType, CastKind Kind) : Expr;

    public sealed record CallExpr(CallTarget Target, IReadOnlyList<Expr> Args) : Expr;

    public sealed record IntrinsicExpr(string Name, IReadOnlyList<Expr> Args) : Expr;

    public sealed record LabelRefExpr(LabelSymbol Label) : Expr;

    public enum SegmentReg { None, FS, GS }

    public enum BinOp
    {
        Add, Sub, Mul, UDiv, SDiv, URem, SRem, And, Or, Xor, Shl, Shr, Sar
    }

    public enum UnOp
    {
        Neg, Not, LNot
    }

    public enum CmpOp
    {
        EQ, NE,
        SLT, SLE, SGT, SGE,
        ULT, ULE, UGT, UGE
    }

    public enum CastKind
    {
        ZeroExtend, SignExtend, Trunc, Bitcast, Reinterpret
    }

    public sealed record CallTarget
    {
        public string? Symbol { get; init; }         // e.g., "kernelbase!CreateFileW" or "memset"
        public Expr? Address { get; init; }          // e.g., RIP-relative IAT slot expression
        public bool IsIndirect => Address is not null && Symbol is null;

        public static CallTarget ByName(string name) => new() { Symbol = name };
        public static CallTarget Indirect(Expr addr) => new() { Address = addr };
    }

    // ============================================================
    //  Statements
    // ============================================================

    public abstract record Stmt;

    /// <summary>Assignment. LHS is a register/local. For memory stores, use StoreStmt.</summary>
    public sealed record AssignStmt(Expr Lhs, Expr Rhs) : Stmt;

    /// <summary>Store to memory: *({type}*)(addr) = value;</summary>
    public sealed record StoreStmt(Expr Address, Expr Value, IrType ElemType, SegmentReg Segment = SegmentReg.None) : Stmt;

    public sealed record CallStmt(CallExpr Call) : Stmt;
    public sealed record IfGotoStmt(Expr Condition, LabelSymbol Target) : Stmt;
    public sealed record GotoStmt(LabelSymbol Target) : Stmt;
    public sealed record LabelStmt(LabelSymbol Label) : Stmt;
    public sealed record ReturnStmt(Expr? Value) : Stmt;
    public sealed record BreakStmt() : Stmt;
    public sealed record ContinueStmt() : Stmt;

    /// <summary>Actual assembly line (address + original mnemonic/operands), shown as comment.</summary>
    public sealed record AsmStmt(string Text) : Stmt;

    /// <summary>Free-form pseudo line (non-assembly), printed as "__pseudo(...)";</summary>
    public sealed record PseudoStmt(string Text) : Stmt;

    public sealed record CommentStmt(string Text) : Stmt;
    public sealed record NopStmt() : Stmt;

    // ============================================================
    //  Higher-level structured forms (optional, for future passes)
    // ============================================================

    public abstract record HiNode;
    public sealed record SeqNode(IReadOnlyList<HiNode> Items) : HiNode;
    public sealed record StmtNode(Stmt Statement) : HiNode;
    public sealed record IfNode(Expr Condition, HiNode Then, HiNode? Else) : HiNode;
    public sealed record WhileNode(Expr Condition, HiNode Body) : HiNode;
    public sealed record DoWhileNode(Expr Condition, HiNode Body) : HiNode;
    public sealed record ForNode(IReadOnlyList<Stmt>? Init, Expr? Condition, IReadOnlyList<Stmt>? Post, HiNode Body) : HiNode;

    public sealed record SwitchNode(Expr Scrutinee, IReadOnlyList<SwitchCase> Cases, IReadOnlyList<Stmt>? Prologue = null, IReadOnlyList<Stmt>? Epilogue = null) : HiNode;
    public sealed record SwitchCase(Expr? MatchValue, IReadOnlyList<HiNode> Body, bool IsDefault = false);

    // ============================================================
    //  Labels / Blocks / Function container
    // ============================================================

    public sealed record LabelSymbol(string Name, int Id)
    {
        public override string ToString() => Name;
    }

    public sealed class BasicBlock
    {
        public LabelSymbol Label { get; init; }
        public List<Stmt> Statements { get; } = new();
        public BasicBlock(LabelSymbol label) => Label = label;
    }

    public sealed class Parameter
    {
        public string Name { get; init; }
        public IrType Type { get; init; }
        public int Index { get; init; }
        public Parameter(string name, IrType type, int index) { Name = name; Type = type; Index = index; }
    }

    public sealed class LocalVar
    {
        public string Name { get; init; }
        public IrType Type { get; init; }
        public Expr? Initializer { get; set; } // allow 'uint8_t* peb = (uint8_t*)__readgsqword(0x60);'

        public LocalVar(string name, IrType type, Expr? init = null) { Name = name; Type = type; Initializer = init; }
    }

    public sealed class FunctionIR
    {
        public string Name { get; init; } = "func";
        public ulong ImageBase { get; init; }
        public ulong EntryAddress { get; init; }

        public IrType ReturnType { get; set; } = new IntType(64, false);
        public List<Parameter> Parameters { get; } = new();
        public List<LocalVar> Locals { get; } = new();

        public List<BasicBlock> Blocks { get; } = new();
        public HiNode? StructuredBody { get; set; }

        public Dictionary<string, object> Tags { get; } = new();

        public FunctionIR(string name, ulong imageBase = 0, ulong entry = 0)
        {
            Name = name; ImageBase = imageBase; EntryAddress = entry;
        }
    }

    // ============================================================
    //  Pretty-printer
    // ============================================================

    public sealed class PrettyPrinter
    {
        public sealed class Options
        {
            public bool EmitHeaderComment { get; set; } = true;
            public bool EmitBlockLabels { get; set; } = false; // we print label statements explicitly; no need for block headers
            public bool CommentSignednessOnCmp { get; set; } = true;
            public bool UseStdIntNames { get; set; } = true; // uint8_t/uint16_t/...
            public string Indent { get; set; } = "    ";
        }

        private readonly Options _opt;
        private readonly StringBuilder _sb = new();
        private int _indent;

        public PrettyPrinter(Options? opt = null) => _opt = opt ?? new Options();

        public string Print(FunctionIR fn)
        {
            _sb.Clear();
            _indent = 0;

            if (_opt.EmitHeaderComment)
            {
                EmitLine("/*");
                EmitLine(" * C-like pseudocode reconstructed from x64 instructions.");
                EmitLine(" * Assumptions: MSVC on Windows, Microsoft x64 calling convention.");
                EmitLine(" * Parameters: p1 (RCX), p2 (RDX), p3 (R8), p4 (R9); return in RAX.");

                EmitLine(" */");
                _sb.AppendLine();
            }

            // Signature
            var ret = RenderType(fn.ReturnType);
            var paramList = string.Join(", ",
                fn.Parameters.OrderBy(p => p.Index).Select(p => $"{RenderType(p.Type)} {p.Name}"));

            EmitLine($"{ret} {fn.Name}({paramList}) {{");
            _indent++;

            // Prologue comments
            bool usesFp = GetTag(fn, "UsesFramePointer", false);
            int localSize = GetTag(fn, "LocalSize", 0);
            if (usesFp)
            {
                if (localSize > 0) EmitLine($"// stack frame: {localSize} bytes of locals (rbp-based)");
                else EmitLine("// stack frame: rbp-based");
            }
            else if (localSize > 0)
            {
                EmitLine($"// stack allocation: sub rsp, {localSize} (no rbp)");
            }
            _sb.AppendLine();

            // Locals (with optional initializers)
            foreach (var l in fn.Locals)
            {
                EmitIndent();
                _sb.Append(RenderType(l.Type)).Append(' ').Append(l.Name);
                if (l.Initializer is not null)
                {
                    _sb.Append(" = ");
                    EmitExpr(l.Initializer, Precedence.Min);
                }
                _sb.AppendLine(";");
            }
            if (fn.Locals.Count > 0) _sb.AppendLine();

            // Body
            if (fn.StructuredBody is not null)
                EmitHiNode(fn.StructuredBody);
            else
                EmitBlocks(fn);

            _indent--;
            EmitLine("}");
            return _sb.ToString();
        }

        private static TTag GetTag<TTag>(FunctionIR fn, string key, TTag def)
        {
            if (fn.Tags.TryGetValue(key, out var obj) && obj is TTag t) return t;
            return def;
        }

        private void EmitBlocks(FunctionIR fn)
        {
            foreach (var bb in fn.Blocks)
            {
                if (_opt.EmitBlockLabels)
                    EmitLine($"{bb.Label.Name}:");

                foreach (var s in bb.Statements)
                    EmitStmt(s);
            }
        }

        private void EmitHiNode(HiNode n)
        {
            switch (n)
            {
                case SeqNode seq:
                    foreach (var item in seq.Items) EmitHiNode(item);
                    break;

                case StmtNode stmt:
                    EmitStmt(stmt.Statement);
                    break;

                case IfNode ifn:
                    Emit("if (");
                    EmitExpr(ifn.Condition, Precedence.Min);
                    _sb.AppendLine(") {");
                    _indent++;
                    EmitHiNode(ifn.Then);
                    _indent--;
                    if (ifn.Else is not null)
                    {
                        EmitLine("} else {");
                        _indent++;
                        EmitHiNode(ifn.Else);
                        _indent--;
                    }
                    EmitLine("}");
                    break;

                case WhileNode wn:
                    Emit("while (");
                    EmitExpr(wn.Condition, Precedence.Min);
                    _sb.AppendLine(") {");
                    _indent++;
                    EmitHiNode(wn.Body);
                    _indent--;
                    EmitLine("}");
                    break;

                case DoWhileNode dwn:
                    EmitLine("do {");
                    _indent++;
                    EmitHiNode(dwn.Body);
                    _indent--;
                    Emit("} while (");
                    EmitExpr(dwn.Condition, Precedence.Min);
                    _sb.AppendLine(");");
                    break;

                case ForNode fn:
                    Emit("for (");
                    EmitStmtListInline(fn.Init);
                    _sb.Append("; ");
                    if (fn.Condition is not null) EmitExpr(fn.Condition, Precedence.Min);
                    _sb.Append("; ");
                    EmitStmtListInline(fn.Post);
                    _sb.AppendLine(") {");
                    _indent++;
                    EmitHiNode(fn.Body);
                    _indent--;
                    EmitLine("}");
                    break;

                case SwitchNode sw:
                    Emit("switch (");
                    EmitExpr(sw.Scrutinee, Precedence.Min);
                    _sb.AppendLine(") {");
                    _indent++;
                    foreach (var c in sw.Cases)
                    {
                        if (c.IsDefault) EmitLine("default:");
                        else
                        {
                            Emit("case ");
                            if (c.MatchValue is null) _sb.Append("/* null */");
                            else EmitExpr(c.MatchValue, Precedence.Min);
                            _sb.AppendLine(":");
                        }
                        _indent++;
                        foreach (var item in c.Body) EmitHiNode(item);
                        EmitLine("break;");
                        _indent--;
                    }
                    _indent--;
                    EmitLine("}");
                    break;

                default:
                    EmitLine("/* unsupported structured node */");
                    break;
            }
        }

        private void EmitStmtListInline(IReadOnlyList<Stmt>? stmts)
        {
            if (stmts is null) return;
            for (int i = 0; i < stmts.Count; i++)
            {
                if (i > 0) _sb.Append(", ");
                EmitStmtInline(stmts[i]);
            }
        }

        private void EmitStmtInline(Stmt s)
        {
            switch (s)
            {
                case AssignStmt a:
                    EmitLValue(a.Lhs);
                    _sb.Append(" = ");
                    EmitExpr(a.Rhs, Precedence.Min);
                    break;
                case CallStmt cs:
                    EmitCall(cs.Call);
                    break;
                case PseudoStmt ps:
                    _sb.Append("__pseudo(").Append(ps.Text).Append(')');
                    break;
                case CommentStmt c:
                    _sb.Append("/* ").Append(c.Text).Append(" */");
                    break;
                case NopStmt:
                    _sb.Append("/* nop */");
                    break;
                case BreakStmt:
                    _sb.Append("break");
                    break;
                case ContinueStmt:
                    _sb.Append("continue");
                    break;
                default:
                    _sb.Append("/* unsupported */");
                    break;
            }
        }

        private void EmitStmt(Stmt s)
        {
            switch (s)
            {
                case AsmStmt a:
                    EmitLine($"/* {a.Text} */");
                    break;

                case PseudoStmt ps:
                    EmitIndent();
                    _sb.Append("__pseudo(").Append(ps.Text).AppendLine(");");
                    break;

                case AssignStmt a:
                    EmitIndent();
                    // special: call assignment → keep previous "/* call */ ... // RAX" flavor
                    if (a.Rhs is CallExpr ce)
                    {
                        _sb.Append("/* call */ ");
                        EmitLValue(a.Lhs);
                        _sb.Append(" = ");
                        EmitCall(ce);
                        _sb.Append(";");
                        if (a.Lhs is RegExpr rx && (string.Equals(rx.Name, "ret", StringComparison.OrdinalIgnoreCase)
                                                  || string.Equals(rx.Name, "rax", StringComparison.OrdinalIgnoreCase)))
                            _sb.Append("  // RAX");
                        _sb.AppendLine();
                        break;
                    }
                    EmitLValue(a.Lhs);
                    _sb.Append(" = ");
                    EmitExpr(a.Rhs, Precedence.Min);
                    _sb.AppendLine(";");
                    break;

                case StoreStmt st:
                    EmitIndent();
                    EmitMemory(st.ElemType, st.Address, st.Segment);
                    _sb.Append(" = ");
                    EmitExpr(st.Value, Precedence.Min);
                    _sb.AppendLine(";");
                    break;

                case CallStmt cs:
                    EmitIndent();
                    EmitCall(cs.Call);
                    _sb.AppendLine(";");
                    break;

                case IfGotoStmt ig:
                    EmitIndent();
                    _sb.Append("if (");
                    EmitExpr(ig.Condition, Precedence.Min);
                    _sb.Append(") goto ");
                    _sb.Append(ig.Target.Name);
                    _sb.AppendLine(";");
                    break;

                case GotoStmt g:
                    EmitLine($"goto {g.Target.Name};");
                    break;

                case LabelStmt l:
                    EmitLine($"{l.Label.Name}:");
                    break;

                case ReturnStmt r:
                    EmitIndent();
                    if (r.Value is null) _sb.AppendLine("return;");
                    else
                    {
                        _sb.Append("return ");
                        EmitExpr(r.Value, Precedence.Min);
                        _sb.AppendLine(";");
                    }
                    break;

                case BreakStmt:
                    EmitLine("break;");
                    break;

                case ContinueStmt:
                    EmitLine("continue;");
                    break;

                case CommentStmt c:
                    EmitLine($"/* {c.Text} */");
                    break;

                case NopStmt:
                    EmitLine("/* nop */");
                    break;

                default:
                    EmitLine("/* unsupported stmt */");
                    break;
            }
        }

        // ---------- expression printing ----------

        private void EmitCall(CallExpr c)
        {
            if (c.Target.Symbol is not null)
                _sb.Append(c.Target.Symbol);
            else if (c.Target.Address is not null)
            {
                _sb.Append("(*");
                EmitExpr(c.Target.Address, Precedence.Min);
                _sb.Append(")");
            }
            else
                _sb.Append("indirect_call");

            _sb.Append("(");
            for (int i = 0; i < c.Args.Count; i++)
            {
                if (i > 0) _sb.Append(", ");
                EmitExpr(c.Args[i], Precedence.Min);
            }
            _sb.Append(")");
        }

        private void EmitExpr(Expr e, int parentPrec)
        {
            switch (e)
            {
                case Const c:
                    EmitConst(c.Value, c.Bits);
                    break;

                case UConst uc:
                    EmitUConst(uc.Value, uc.Bits);
                    break;

                case SymConst sc:
                    _sb.Append(sc.Name);
                    break;

                case RegExpr r:
                    _sb.Append(r.Name);
                    break;

                case ParamExpr p:
                    _sb.Append(p.Name);
                    break;

                case LocalExpr l:
                    _sb.Append(l.Name);
                    break;

                case SegmentBaseExpr seg:
                    _sb.Append(seg.Segment == SegmentReg.FS ? "__segfs" : "__seggs");
                    break;

                case AddrOfExpr aof:
                    _sb.Append("&");
                    EmitExpr(aof.Operand, Precedence.Prefix);
                    break;

                case LoadExpr ld:
                    EmitMemory(ld.ElemType, ld.Address, ld.Segment);
                    break;

                case BinOpExpr b:
                    {
                        var (opTxt, prec, assocRight) = OpInfo(b.Op);
                        bool need = prec < parentPrec;
                        if (need) _sb.Append("(");
                        EmitExpr(b.Left, assocRight ? prec : prec + 1);
                        _sb.Append(' ').Append(opTxt).Append(' ');
                        EmitExpr(b.Right, assocRight ? prec + 1 : prec);
                        if (need) _sb.Append(")");
                    }
                    break;

                case UnOpExpr u:
                    {
                        var (opTxt, prec) = UnOpInfo(u.Op);
                        bool need = prec < parentPrec;
                        if (need) _sb.Append("(");
                        _sb.Append(opTxt);
                        EmitExpr(u.Operand, prec);
                        if (need) _sb.Append(")");
                    }
                    break;

                case CompareExpr cmp:
                    {
                        var (opTxt, signedHint) = CmpInfo(cmp.Op);
                        if (_opt.CommentSignednessOnCmp && signedHint is not null)
                            _sb.Append("/* ").Append(signedHint).Append(" */ ");
                        EmitExpr(cmp.Left, Precedence.Rel);
                        _sb.Append(' ').Append(opTxt).Append(' ');
                        EmitExpr(cmp.Right, Precedence.Rel);
                    }
                    break;

                case TernaryExpr t:
                    {
                        bool need = Precedence.Cond < parentPrec;
                        if (need) _sb.Append("(");
                        EmitExpr(t.Condition, Precedence.Cond);
                        _sb.Append(" ? ");
                        EmitExpr(t.WhenTrue, Precedence.Cond);
                        _sb.Append(" : ");
                        EmitExpr(t.WhenFalse, Precedence.Cond);
                        if (need) _sb.Append(")");
                    }
                    break;

                case CastExpr c:
                    {
                        string castTxt = RenderCast(c);
                        bool need = Precedence.Prefix < parentPrec;
                        if (need) _sb.Append("(");
                        _sb.Append(castTxt);
                        EmitExpr(c.Value, Precedence.Prefix);
                        if (need) _sb.Append(")");
                    }
                    break;

                case CallExpr ce:
                    EmitCall(ce);
                    break;

                case IntrinsicExpr ie:
                    _sb.Append(ie.Name).Append("(");
                    for (int i = 0; i < ie.Args.Count; i++)
                    {
                        if (i > 0) _sb.Append(", ");
                        EmitExpr(ie.Args[i], Precedence.Min);
                    }
                    _sb.Append(")");
                    break;

                case LabelRefExpr lr:
                    _sb.Append(lr.Label.Name);
                    break;

                default:
                    _sb.Append("/* unsupported expr */");
                    break;
            }
        }

        private void EmitLValue(Expr lhs)
        {
            switch (lhs)
            {
                case RegExpr or LocalExpr or ParamExpr:
                    EmitExpr(lhs, Precedence.Min);
                    break;

                case LoadExpr ld: // LHS 'load' means deref target
                    EmitMemory(ld.ElemType, ld.Address, ld.Segment);
                    break;

                default:
                    EmitExpr(lhs, Precedence.Min);
                    break;
            }
        }

        private void EmitMemory(IrType elem, Expr addr, SegmentReg seg)
        {
            _sb.Append("*((").Append(RenderType(elem)).Append("*)(");
            if (seg is SegmentReg.FS) _sb.Append("fs:");
            if (seg is SegmentReg.GS) _sb.Append("gs:");
            EmitExpr(addr, Precedence.Min);
            _sb.Append("))");
        }

        // ---------- low-level printers ----------

        private void EmitConst(long v, int bits)
        {
            if (v < 0)
                _sb.Append("0x").Append(unchecked((ulong)v).ToString("X"));
            else if (v >= 10)
                _sb.Append("0x").Append(v.ToString("X"));
            else
                _sb.Append(v.ToString(CultureInfo.InvariantCulture));
        }

        private void EmitUConst(ulong v, int bits)
        {
            if (v >= 10)
                _sb.Append("0x").Append(v.ToString("X"));
            else
                _sb.Append(v.ToString(CultureInfo.InvariantCulture));
        }

        private static class Precedence
        {
            public const int Min = 0;
            public const int Assign = 1;
            public const int Cond = 2;
            public const int Or = 3;
            public const int And = 4;
            public const int Xor = 5;
            public const int BitAnd = 6;
            public const int Rel = 7;
            public const int Shift = 8;
            public const int Add = 9;
            public const int Mul = 10;
            public const int Prefix = 11;
            public const int Atom = 12;
        }

        private static (string op, int prec, bool assocRight) OpInfo(BinOp op) => op switch
        {
            BinOp.Mul => ("*", Precedence.Mul, false),
            BinOp.UDiv or BinOp.SDiv => ("/", Precedence.Mul, false),
            BinOp.URem or BinOp.SRem => ("%", Precedence.Mul, false),
            BinOp.Add => ("+", Precedence.Add, false),
            BinOp.Sub => ("-", Precedence.Add, false),
            BinOp.Shl => ("<<", Precedence.Shift, false),
            BinOp.Shr or BinOp.Sar => (">>", Precedence.Shift, false),
            BinOp.And => ("&", Precedence.BitAnd, false),
            BinOp.Xor => ("^", Precedence.Xor, false),
            BinOp.Or => ("|", Precedence.Or, false),
            _ => ("?", Precedence.Add, false)
        };

        private static (string op, int prec) UnOpInfo(UnOp op) => op switch
        {
            UnOp.Neg => ("-", Precedence.Prefix),
            UnOp.Not => ("~", Precedence.Prefix),
            UnOp.LNot => ("!", Precedence.Prefix),
            _ => ("/* ? */", Precedence.Prefix)
        };

        private static (string op, string? signedHint) CmpInfo(CmpOp op) => op switch
        {
            CmpOp.EQ => ("==", null),
            CmpOp.NE => ("!=", null),

            CmpOp.SLT => ("<", "signed"),
            CmpOp.SLE => ("<=", "signed"),
            CmpOp.SGT => (">", "signed"),
            CmpOp.SGE => (">=", "signed"),

            CmpOp.ULT => ("<", "unsigned"),
            CmpOp.ULE => ("<=", "unsigned"),
            CmpOp.UGT => (">", "unsigned"),
            CmpOp.UGE => (">=", "unsigned"),

            _ => ("/* ? */", null)
        };

        private string RenderCast(CastExpr c) => c.Kind switch
        {
            CastKind.Bitcast or CastKind.Reinterpret => $"({RenderType(c.TargetType)})",
            CastKind.Trunc => $"({RenderType(c.TargetType)})",
            CastKind.ZeroExtend => $"({RenderType(c.TargetType)})",
            CastKind.SignExtend => $"({RenderType(c.TargetType)})",
            _ => $"({RenderType(c.TargetType)})"
        };

        private string RenderType(IrType t) => t switch
        {
            VoidType => "void",
            IntType it => RenderIntType(it),
            FloatType ft => ft.Bits == 32 ? "float" : ft.Bits == 64 ? "double" : $"float{ft.Bits}_t",
            PointerType pt => $"{RenderType(pt.Element)}*",
            VectorType vt => vt.Bits switch
            {
                128 => "vec128_t",
                256 => "vec256_t",
                512 => "vec512_t",
                _ => $"vec{vt.Bits}_t"
            },
            UnknownType ut => ut.Note is null ? "uint64_t /* unknown */" : $"uint64_t /* {ut.Note} */",
            _ => "uint64_t /* ? */"
        };

        private string RenderIntType(IntType t)
        {
            if (_opt.UseStdIntNames)
            {
                return (t.IsSigned ? "int" : "uint") + t.Bits + "_t";
            }
            return t.Bits switch
            {
                8 => t.IsSigned ? "signed char" : "unsigned char",
                16 => t.IsSigned ? "short" : "unsigned short",
                32 => t.IsSigned ? "int" : "unsigned int",
                64 => t.IsSigned ? "long long" : "unsigned long long",
                _ => t.IsSigned ? $"int{t.Bits}_t" : $"uint{t.Bits}_t"
            };
        }

        private void Emit(string s) => _sb.Append(s);
        private void EmitLine(string s) { EmitIndent(); _sb.AppendLine(s); }
        private void EmitIndent() { for (int i = 0; i < _indent; i++) _sb.Append(_opt.Indent); }
    }

    // ============================================================
    //  Small construction helpers (optional)
    // ============================================================

    public static class X
    {
        public static readonly IntType U8 = new(8, false);
        public static readonly IntType U16 = new(16, false);
        public static readonly IntType U32 = new(32, false);
        public static readonly IntType U64 = new(64, false);
        public static readonly IntType I8 = new(8, true);
        public static readonly IntType I16 = new(16, true);
        public static readonly IntType I32 = new(32, true);
        public static readonly IntType I64 = new(64, true);

        public static readonly VoidType Void = new();

        public static Const C(long v, int bits = 64) => new(v, bits);
        public static UConst UC(ulong v, int bits = 64) => new(v, bits);

        public static RegExpr R(string name) => new(name);
        public static ParamExpr P(string name, int index) => new(name, index);
        public static LocalExpr L(string name) => new(name);

        public static AddrOfExpr Addr(Expr e) => new(e);

        public static LoadExpr LD(Expr addr, IrType t, SegmentReg seg = SegmentReg.None) => new(addr, t, seg);
        public static StoreStmt ST(Expr addr, Expr val, IrType t, SegmentReg seg = SegmentReg.None) => new(addr, val, t, seg);
        public static BreakStmt Break() => new();
        public static ContinueStmt Continue() => new();

        public static BinOpExpr Add(Expr a, Expr b) => new(BinOp.Add, a, b);
        public static BinOpExpr Sub(Expr a, Expr b) => new(BinOp.Sub, a, b);
        public static BinOpExpr Mul(Expr a, Expr b) => new(BinOp.Mul, a, b);
        public static BinOpExpr And(Expr a, Expr b) => new(BinOp.And, a, b);
        public static BinOpExpr Or(Expr a, Expr b) => new(BinOp.Or, a, b);
        public static BinOpExpr Xor(Expr a, Expr b) => new(BinOp.Xor, a, b);
        public static BinOpExpr Shl(Expr a, Expr b) => new(BinOp.Shl, a, b);
        public static BinOpExpr Shr(Expr a, Expr b) => new(BinOp.Shr, a, b);
        public static BinOpExpr Sar(Expr a, Expr b) => new(BinOp.Sar, a, b);

        public static UnOpExpr Neg(Expr a) => new(UnOp.Neg, a);
        public static UnOpExpr Not(Expr a) => new(UnOp.Not, a);
        public static UnOpExpr LNot(Expr a) => new(UnOp.LNot, a);

        public static CompareExpr Eq(Expr a, Expr b) => new(CmpOp.EQ, a, b);
        public static CompareExpr Ne(Expr a, Expr b) => new(CmpOp.NE, a, b);
        public static CompareExpr SLt(Expr a, Expr b) => new(CmpOp.SLT, a, b);
        public static CompareExpr SLe(Expr a, Expr b) => new(CmpOp.SLE, a, b);
        public static CompareExpr SGt(Expr a, Expr b) => new(CmpOp.SGT, a, b);
        public static CompareExpr SGe(Expr a, Expr b) => new(CmpOp.SGE, a, b);
        public static CompareExpr ULt(Expr a, Expr b) => new(CmpOp.ULT, a, b);
        public static CompareExpr ULe(Expr a, Expr b) => new(CmpOp.ULE, a, b);
        public static CompareExpr UGt(Expr a, Expr b) => new(CmpOp.UGT, a, b);
        public static CompareExpr UGe(Expr a, Expr b) => new(CmpOp.UGE, a, b);

        public static CastExpr ZExt(Expr v, IrType t) => new(v, t, CastKind.ZeroExtend);
        public static CastExpr SExt(Expr v, IrType t) => new(v, t, CastKind.SignExtend);
        public static CastExpr Trunc(Expr v, IrType t) => new(v, t, CastKind.Trunc);
        public static CastExpr Bitcast(Expr v, IrType t) => new(v, t, CastKind.Bitcast);
        public static CastExpr Reinterpret(Expr v, IrType t) => new(v, t, CastKind.Reinterpret);

        public static CallExpr Call(string name, params Expr[] args) => new(CallTarget.ByName(name), args);
        public static CallExpr Call(Expr addr, params Expr[] args) => new(CallTarget.Indirect(addr), args);

        public static LabelSymbol Lbl(int id) => new($"L{id}", id);
    }
}
