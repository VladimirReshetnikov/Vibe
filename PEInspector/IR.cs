#if false
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

/// <summary>
/// Medium-level IR (MIR) for C-like pseudocode reconstructed from x64.
/// Design goals:
///   - Express *everything* we currently emit (labels/gotos, assignments, calls, loads/stores, compares, casts, intrinsics)
///   - Preserve enough detail for later normalization & refinement passes (bit width, signedness hints, segments, RIP-rel, etc.)
///   - Be flexible to host higher-level constructs later (if/else, loops, switches) without breaking the low-level form
///   - Pretty-print deterministically to the current human-readable style
/// 
/// This file provides:
///   1) IR node definitions (types, expressions, statements, blocks, function)
///   2) A pluggable pretty-printer with options
/// 
/// Integration plan (later steps, **not** in this file):
///   - Replace direct string-emission in the decompiler with IR construction
///   - Keep separate passes that transform IR (peepholes → struct copies, bt/jcc → conditions, struct inference, CFG structuring)
///   - End with PrettyPrinter rendering the final MIR
/// </summary>
public static class PseudoIr
{
    // ============================================================
    //  Types
    // ============================================================

    public abstract record IrType;

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

    /// <summary>Integer constant (signed payload). Use Bits to indicate width context.</summary>
    public sealed record Const(long Value, int Bits) : Expr;

    /// <summary>Unsigned integer constant when sign matters.</summary>
    public sealed record UConst(ulong Value, int Bits) : Expr;

    /// <summary>Named register (physical or pseudo). Keep as string to avoid coupling.</summary>
    public sealed record RegExpr(string Name) : Expr;

    /// <summary>Named parameter reference (index for stable ordering).</summary>
    public sealed record ParamExpr(string Name, int Index) : Expr;

    /// <summary>Named local variable reference (non-addressable unless made address-of).</summary>
    public sealed record LocalExpr(string Name) : Expr;

    /// <summary>Segment base (FS/GS). Address expressions can add offsets to this.</summary>
    public sealed record SegmentBaseExpr(SegmentReg Segment) : Expr;

    /// <summary>Raw address expression (any Expr). Typed memory load.</summary>
    public sealed record LoadExpr(Expr Address, IrType ElemType, SegmentReg Segment = SegmentReg.None) : Expr;

    /// <summary>
    /// Binary operator expression. Covers arithmetic, bitwise and shifts. Rotates are expressed via IntrinsicExpr("rotl"/"rotr",...).
    /// </summary>
    public sealed record BinOpExpr(BinOp Op, Expr Left, Expr Right) : Expr;

    /// <summary>Unary operator expression. Neg = arithmetic -, Not = bitwise ~, LNot = logical !.</summary>
    public sealed record UnOpExpr(UnOp Op, Expr Operand) : Expr;

    /// <summary>Comparison producing a boolean. Signedness is part of the op.</summary>
    public sealed record CompareExpr(CmpOp Op, Expr Left, Expr Right) : Expr;

    /// <summary>Ternary (cond ? a : b).</summary>
    public sealed record TernaryExpr(Expr Condition, Expr WhenTrue, Expr WhenFalse) : Expr;

    /// <summary>Cast with intent. Bitcast preserves bit pattern; Trunc cuts high bits; (Z|S)Ext extends to target width.</summary>
    public sealed record CastExpr(Expr Value, IrType TargetType, CastKind Kind) : Expr;

    /// <summary>Function call as an expression (with return value). For side-effect-only calls use CallStmt.</summary>
    public sealed record CallExpr(CallTarget Target, IReadOnlyList<Expr> Args) : Expr;

    /// <summary>Intrinsic expression (eg "memset","memcpy","rotl","sign","__readgsqword", etc.)</summary>
    public sealed record IntrinsicExpr(string Name, IReadOnlyList<Expr> Args) : Expr;

    /// <summary>Convenience to refer to a labeled address in the same function.</summary>
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

    /// <summary>LHS must be an assignable location: RegExpr, LocalExpr or LoadExpr used as lvalue (*ptr = ...).</summary>
    public sealed record AssignStmt(Expr Lhs, Expr Rhs) : Stmt;

    /// <summary>Call with ignored return value.</summary>
    public sealed record CallStmt(CallExpr Call) : Stmt;

    public sealed record IfGotoStmt(Expr Condition, LabelSymbol Target) : Stmt;
    public sealed record GotoStmt(LabelSymbol Target) : Stmt;
    public sealed record LabelStmt(LabelSymbol Label) : Stmt;

    public sealed record ReturnStmt(Expr? Value) : Stmt;

    public sealed record CommentStmt(string Text) : Stmt;
    public sealed record NopStmt() : Stmt;

    /// <summary>Store to memory: *({type}*)(addr) = value;</summary>
    public sealed record StoreStmt(Expr Address, Expr Value, IrType ElemType, SegmentReg Segment = SegmentReg.None) : Stmt;

    // ============================================================
    //  Higher-level structured forms (optional, for future passes)
    // ============================================================

    public abstract record HiNode;
    public sealed record SeqNode(IReadOnlyList<HiNode> Items) : HiNode;
    public sealed record StmtNode(Stmt Statement) : HiNode;
    public sealed record IfNode(Expr Condition, HiNode Then, HiNode? Else) : HiNode;
    public sealed record WhileNode(Expr Condition, HiNode Body) : HiNode;
    public sealed record DoWhileNode(Expr Condition, HiNode Body) : HiNode;

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

        public LocalVar(string name, IrType type) { Name = name; Type = type; }
    }

    public sealed class FunctionIR
    {
        public string Name { get; init; } = "func";
        public ulong ImageBase { get; init; }
        public ulong EntryAddress { get; init; }

        public IrType ReturnType { get; set; } = new IntType(64, false);
        public List<Parameter> Parameters { get; } = new();
        public List<LocalVar> Locals { get; } = new();

        /// <summary>Low-level form: labeled basic blocks + gotos.</summary>
        public List<BasicBlock> Blocks { get; } = new();

        /// <summary>Optional higher-level structured form (after structuring passes).</summary>
        public HiNode? StructuredBody { get; set; }

        /// <summary>Metadata for passes (aliases, symbol maps, analysis artifacts).</summary>
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
            public bool EmitLabels { get; set; } = true;
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
                EmitLine(" * Pseudocode (approximate) reconstructed from x64 instructions.");
                EmitLine(" * This is an IR pretty-print; later passes may refine structure and types.");
                EmitLine(" */");
                _sb.AppendLine();
            }

            // Signature
            var ret = RenderType(fn.ReturnType);
            var paramList = string.Join(", ",
                fn.Parameters.OrderBy(p => p.Index).Select(p => $"{RenderType(p.Type)} {p.Name}"));

            EmitLine($"{ret} {fn.Name}({paramList}) {{");
            _indent++;

            // Locals (if any)
            foreach (var l in fn.Locals)
                EmitLine($"{RenderType(l.Type)} {l.Name};");

            if (fn.Locals.Count > 0) _sb.AppendLine();

            // Prefer structured body if present; fall back to blocks
            if (fn.StructuredBody is not null)
                EmitHiNode(fn.StructuredBody);
            else
                EmitBlocks(fn);

            _indent--;
            EmitLine("}");
            return _sb.ToString();
        }

        // ---------- emit helpers ----------

        private void EmitBlocks(FunctionIR fn)
        {
            foreach (var bb in fn.Blocks)
            {
                if (_opt.EmitLabels)
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

        private void EmitStmt(Stmt s)
        {
            switch (s)
            {
                case AssignStmt a:
                    EmitIndent();
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
                    // Allow arbitrary lvalues (eg, *(T*)addr) by printing expression as-is
                    EmitExpr(lhs, Precedence.Min);
                    break;
            }
        }

        private void EmitMemory(IrType elem, Expr addr, SegmentReg seg)
        {
            _sb.Append("*((").Append(RenderType(elem)).Append("*)(");
            if (seg is SegmentReg.FS) _sb.Append("/* fs: */");
            if (seg is SegmentReg.GS) _sb.Append("/* gs: */");
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
            CastKind.ZeroExtend => $"({RenderType(c.TargetType)})(({RenderType(c.TargetType)})",
            CastKind.SignExtend => $"({RenderType(c.TargetType)})(({RenderType(c.TargetType)})",
            _ => $"({RenderType(c.TargetType)})"
        };

        private string RenderType(IrType t) => t switch
        {
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

            // Non-stdint style (C-like defaults)
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

        private void EmitLine(string s)
        {
            EmitIndent();
            _sb.AppendLine(s);
        }

        private void EmitIndent()
        {
            for (int i = 0; i < _indent; i++)
                _sb.Append(_opt.Indent);
        }
    }

    // ============================================================
    //  Small construction helpers (optional)
    // ============================================================

    public static class X
    {
        public static IntType U8 = new(8, false);
        public static IntType U16 = new(16, false);
        public static IntType U32 = new(32, false);
        public static IntType U64 = new(64, false);
        public static IntType I8 = new(8, true);
        public static IntType I16 = new(16, true);
        public static IntType I32 = new(32, true);
        public static IntType I64 = new(64, true);

        public static Const C(long v, int bits = 64) => new(v, bits);
        public static UConst UC(ulong v, int bits = 64) => new(v, bits);

        public static RegExpr R(string name) => new(name);
        public static ParamExpr P(string name, int index) => new(name, index);
        public static LocalExpr L(string name) => new(name);

        public static LoadExpr LD(Expr addr, IrType t, SegmentReg seg = SegmentReg.None) => new(addr, t, seg);
        public static StoreStmt ST(Expr addr, Expr val, IrType t, SegmentReg seg = SegmentReg.None) => new(addr, val, t, seg);

        public static BinOpExpr Add(Expr a, Expr b) => new(BinOp.Add, a, b);
        public static BinOpExpr Sub(Expr a, Expr b) => new(BinOp.Sub, a, b);
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
        public static CompareExpr ULt(Expr a, Expr b) => new(CmpOp.ULT, a, b);

        public static CastExpr ZExt(Expr v, IrType t) => new(v, t, CastKind.ZeroExtend);
        public static CastExpr SExt(Expr v, IrType t) => new(v, t, CastKind.SignExtend);
        public static CastExpr Trunc(Expr v, IrType t) => new(v, t, CastKind.Trunc);
        public static CastExpr Bitcast(Expr v, IrType t) => new(v, t, CastKind.Bitcast);

        public static CallExpr Call(string name, params Expr[] args) => new(CallTarget.ByName(name), args);
        public static CallExpr Call(Expr addr, params Expr[] args) => new(CallTarget.Indirect(addr), args);

        public static LabelSymbol Lbl(int id) => new($"L{id}", id);
    }

    // ============================================================
    //  Mini demonstration builder (optional)
    //  (This is not used by the decompiler; it shows how to build & print IR.)
    // ============================================================

    public static FunctionIR Demo()
    {
        // uint64_t demo(uint64_t p1, uint64_t p2) { if ((p1 & 0xFF) == 0) goto L1; memset((void*)p2,0,32); L1: return p1 + p2; }
        var fn = new FunctionIR("demo")
        {
            ReturnType = X.U64,
        };
        fn.Parameters.Add(new Parameter("p1", X.U64, 0));
        fn.Parameters.Add(new Parameter("p2", X.U64, 1));

        var L1 = X.Lbl(1);
        var entry = new BasicBlock(X.Lbl(0));
        entry.Statements.Add(new IfGotoStmt(
            new CompareExpr(CmpOp.EQ, new BinOpExpr(BinOp.And, X.P("p1", 0), X.C(0xFF)), X.C(0)),
            L1));
        entry.Statements.Add(new CallStmt(new CallExpr(CallTarget.ByName("memset"),
            new Expr[] { X.P("p2", 1), X.C(0), X.C(32) })));
        entry.Statements.Add(new LabelStmt(L1));
        entry.Statements.Add(new ReturnStmt(new BinOpExpr(BinOp.Add, X.P("p1", 0), X.P("p2", 1))));
        fn.Blocks.Add(entry);
        return fn;
    }
}
#endif