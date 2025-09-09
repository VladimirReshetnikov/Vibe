using Iced.Intel;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using static PseudoIr;
using Decoder = Iced.Intel.Decoder;

/* ============================================================
 *                P S E U D O  -  I R
 * Medium-level IR (MIR) for C-like pseudocode reconstructed from x64.
 * The decompiler below now *builds* this IR first, then pretty-prints it.
 * ============================================================ */

public static class PseudoIr
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
                EmitLine(" * Pseudocode (approximate) reconstructed from x64 instructions.");
                EmitLine(" * Assumptions: MSVC on Windows, Microsoft x64 calling convention.");
                EmitLine(" * Parameters: p1 (RCX), p2 (RDX), p3 (R8), p4 (R9); return in RAX.");
                EmitLine(" * NOTE: This is not a full decompiler; conditions, types, and pointer types");
                EmitLine(" *       are inferred heuristically and may be imprecise.");
                EmitLine(" */");
                _sb.AppendLine();
            }

            // Signature
            var ret = RenderType(fn.ReturnType);
            var paramList = string.Join(", ",
                fn.Parameters.OrderBy(p => p.Index).Select(p => $"{RenderType(p.Type)} {p.Name}"));

            EmitLine($"{ret} {fn.Name}({paramList}) {{");
            _indent++;

            // Prologue comments (for parity with previous string emitter)
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
            EmitLine("// registers shown when helpful; memory shown via *(uintXX_t*)(addr)");
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

        // ---------- emit helpers ----------

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

/* ============================================================
 *   M S V C   F u n c t i o n   P s e u d o - D e c o m p i l e r
 * Now: builds PseudoIr.FunctionIR and then pretty-prints it.
 * ============================================================ */

public sealed class MsvcFunctionPseudoDecompiler
{
    public sealed class Options
    {
        /// <summary>Base address to assume for the function (used for RIP-relative and labels)</summary>
        public ulong BaseAddress { get; set; } = 0x0000000140000000UL;

        /// <summary>Optional pretty function name to show in the pseudocode header</summary>
        public string FunctionName { get; set; } = "func";

        /// <summary>Emit original labels for branch targets (L1, L2, ...)</summary>
        public bool EmitLabels { get; set; } = true;

        /// <summary>Try to detect MSVC prologue/epilogue and hide low-level stack chaff</summary>
        public bool DetectPrologue { get; set; } = true;

        /// <summary>Also emit trailing comments for some instructions that are purely flag-setting</summary>
        public bool CommentCompare { get; set; } = true;

        /// <summary>Include very short assembly comments (mnemonic+operands) for context</summary>
        public bool InlineAsmComments { get; set; } = false;

        /// <summary>Maximum bytes to decode; null = all provided bytes</summary>
        public int? MaxBytes { get; set; } = null;

        /// <summary>Optional name resolver for indirect/IAT calls.</summary>
        public Func<ulong, string?>? ResolveImportName { get; set; } = null;
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
        public readonly Dictionary<ulong, string> InsnToAsm = new(); // optional ASM comments

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

        // Pretty print IR
        var pp = new PseudoIr.PrettyPrinter(new PseudoIr.PrettyPrinter.Options
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

            if (ctx.Opt.InlineAsmComments)
                ctx.InsnToAsm[instr.IP] = QuickAsm(instr);
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

    private PseudoIr.FunctionIR BuildFunctionIr(Ctx ctx)
    {
        var fn = new PseudoIr.FunctionIR(ctx.Opt.FunctionName, ctx.Opt.BaseAddress, ctx.StartIp)
        {
            ReturnType = PseudoIr.X.U64
        };
        // Parameters (for signature only; body uses RegExpr "pX")
        fn.Parameters.Add(new PseudoIr.Parameter("p1", PseudoIr.X.U64, 0));
        fn.Parameters.Add(new PseudoIr.Parameter("p2", PseudoIr.X.U64, 1));
        fn.Parameters.Add(new PseudoIr.Parameter("p3", PseudoIr.X.U64, 2));
        fn.Parameters.Add(new PseudoIr.Parameter("p4", PseudoIr.X.U64, 3));

        // Set tags used by pretty-printer for header/body comments
        fn.Tags["UsesFramePointer"] = ctx.UsesFramePointer;
        fn.Tags["LocalSize"] = ctx.LocalSize;

        // Optional PEB alias local with initializer
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

        // Walk instructions and append statements
        var ins = ctx.Insns;
        for (int idx = 0; idx < ins.Count; idx++)
        {
            var i = ins[idx];

            // Label line if needed
            if (ctx.Opt.EmitLabels && ctx.LabelByIp.TryGetValue(i.IP, out int lab))
                block.Statements.Add(new PseudoIr.LabelStmt(new PseudoIr.LabelSymbol($"L{lab}", lab)));

            // Peephole: coalesce zero-store runs into one memset
            if (TryCoalesceZeroStoresIR(ins, idx, ctx, out var zeroStmts, out int consumedZero))
            {
                block.Statements.AddRange(zeroStmts);
                idx += consumedZero - 1;
                continue;
            }

            // Peephole: memcpy 16B blocks
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
        Expr? baseAddr = null;
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
            // memset((void*)(base + firstOff), 0, bytes)
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
        Expr? srcBase = null, dstBase = null;
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

    private static bool TrySplitBasePlusOffset(Expr addr, out Expr baseExpr, out long off)
    {
        // Recognize (base + const) or (base - const)
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

    private static bool ExprEquals(Expr a, Expr b)
    {
        // Cheap structural check for our simple base expressions (RegExpr/LocalExpr)
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
                yield return new PseudoIr.CommentStmt($"if ({ExprToText(cond)}) goto 0x{i.NearBranchTarget:X};");
            ctx.LastBt = null;
            yield break;
        }

        // Hide prologue/epilogue (emit comment only)
        if (ctx.Opt.DetectPrologue && IsPrologueOrEpilogue(i))
        {
            yield return new PseudoIr.CommentStmt(QuickAsm(i));
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
                        yield return new PseudoIr.CommentStmt("RDX:RAX = RAX * op (signed)");
                    }
                    yield break;
                }
            case Mnemonic.Mul:
                yield return new PseudoIr.CommentStmt("RDX:RAX = RAX * op (unsigned)"); yield break;
            case Mnemonic.Idiv:
                yield return new PseudoIr.CommentStmt("RAX = (RDX:RAX) / op; RDX = remainder (signed)"); yield break;
            case Mnemonic.Div:
                yield return new PseudoIr.CommentStmt("RAX = (RDX:RAX) / op; RDX = remainder (unsigned)"); yield break;

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
                    yield return new PseudoIr.CommentStmt($"CF = bit({v}, {ix})");
                    yield break;
                }

            // Flag setters
            case Mnemonic.Cmp:
                {
                    var (l, r, w, lc, lval, rc, rval) = ExtractCmpLike(i, ctx);
                    ctx.LastCmp = new LastCmp { Left = l, Right = r, BitWidth = w, IsTest = false, Ip = i.IP, LeftIsConst = lc, RightIsConst = rc, LeftConst = lval, RightConst = rval };
                    if (ctx.Opt.CommentCompare) yield return new PseudoIr.CommentStmt($"compare {l}, {r}");
                    yield break;
                }
            case Mnemonic.Test:
                {
                    var (l, r, w, lc, lval, rc, rval) = ExtractCmpLike(i, ctx);
                    ctx.LastCmp = new LastCmp { Left = l, Right = r, BitWidth = w, IsTest = true, Ip = i.IP, LeftIsConst = lc, RightIsConst = rc, LeftConst = lval, RightConst = rval };
                    if (ctx.Opt.CommentCompare) yield return new PseudoIr.CommentStmt($"test {l}, {r}");
                    yield break;
                }

            // Branching (unconditional)
            case Mnemonic.Jmp:
                if (HasNearTarget(i) && ctx.LabelByIp.TryGetValue(i.NearBranchTarget, out int lab))
                    yield return new PseudoIr.GotoStmt(new PseudoIr.LabelSymbol($"L{lab}", lab));
                else
                    yield return new PseudoIr.CommentStmt($"jmp 0x{i.NearBranchTarget:X}");
                yield break;

            // Calls / returns
            case Mnemonic.Call:
                {
                    // memset(rcx, edx, r8d) call-site
                    if (TryRenderMemsetCallSiteIR(i, ctx, out var ms))
                    {
                        yield return ms;
                        yield break;
                    }

                    var callExpr = BuildCallExpr(i, ctx, out bool assignsToRet);
                    if (assignsToRet)
                        yield return new PseudoIr.AssignStmt(PseudoIr.X.R("ret"), callExpr);
                    else
                        yield return new PseudoIr.CallStmt(callExpr);
                    ctx.LastWasCall = true;
                    yield break;
                }

            case Mnemonic.Ret:
            case Mnemonic.Retf:
                yield return new PseudoIr.ReturnStmt(PseudoIr.X.R("ret"));
                yield break;

            // Push / Pop
            case Mnemonic.Push:
                yield return new PseudoIr.CommentStmt($"push {OperandText(i, 0, ctx)}"); yield break;
            case Mnemonic.Pop:
                yield return new PseudoIr.CommentStmt($"pop -> {OperandText(i, 0, ctx)}"); yield break;

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
                else yield return new PseudoIr.CommentStmt("movs (element size varies)");
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
                else yield return new PseudoIr.CommentStmt("stos (element size varies)");
                yield break;

            // SSE zero idioms and stores
            case Mnemonic.Xorps:
            case Mnemonic.Pxor:
                if (i.Op0Kind == OpKind.Register && i.Op1Kind == OpKind.Register &&
                    GetOpRegister(i, 0) == GetOpRegister(i, 1) &&
                    RegisterBitWidth(GetOpRegister(i, 0)) == 128)
                {
                    ctx.LastZeroedXmm = GetOpRegister(i, 0);
                    yield return new PseudoIr.CommentStmt("zero xmm");
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
                    yield return new PseudoIr.CallStmt(PseudoIr.X.Call("memset",
                        new PseudoIr.Expr[] { new PseudoIr.CastExpr(addr, new PseudoIr.PointerType(new PseudoIr.VoidType()), PseudoIr.CastKind.Reinterpret), PseudoIr.X.C(0), PseudoIr.X.C(16) }));
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
                        Mnemonic.Addss or Mnemonic.Addsd => PseudoIr.BinOp.Add,
                        Mnemonic.Subss or Mnemonic.Subsd => PseudoIr.BinOp.Sub,
                        Mnemonic.Mulss or Mnemonic.Mulsd => PseudoIr.BinOp.Mul,
                        _ => PseudoIr.BinOp.UDiv
                    };
                    var dst = LhsExpr(i, ctx);
                    var src = OperandExpr(i, 1, ctx, forRead: true);
                    yield return new PseudoIr.AssignStmt(dst, new PseudoIr.BinOpExpr(op, dst, src));
                    yield break;
                }

            // misc
            case Mnemonic.Nop:
                yield return new PseudoIr.NopStmt(); yield break;
            case Mnemonic.Leave:
                yield return new PseudoIr.CommentStmt("leave (epilogue)"); yield break;
            case Mnemonic.Cdq:
            case Mnemonic.Cqo:
                yield return new PseudoIr.CommentStmt("sign-extend: RDX:RAX <- sign(RAX)"); yield break;
        }

        // Default: comment with asm text
        yield return new PseudoIr.CommentStmt(QuickAsm(i));
    }

    // --------- Condition construction ----------------------------------------

    private PseudoIr.Expr ConditionExpr(in Instruction i, Ctx ctx)
    {
        // Special mnemonics
        if (i.Mnemonic == Mnemonic.Jrcxz) return PseudoIr.X.Eq(PseudoIr.X.R("rcx"), PseudoIr.X.C(0));
        if (i.Mnemonic == Mnemonic.Jecxz) return PseudoIr.X.Eq(PseudoIr.X.R("ecx"), PseudoIr.X.C(0));
        if (i.Mnemonic == Mnemonic.Jcxz) return PseudoIr.X.Eq(PseudoIr.X.R("cx"), PseudoIr.X.C(0));

        var cc = i.ConditionCode;

        // BT → CF predicate
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

        // test r,r → simplify je/jne
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

                // flag-only fallbacks
                case ConditionCode.s: return PseudoIr.X.Ne(PseudoIr.X.R("SF"), PseudoIr.X.C(0));
                case ConditionCode.ns: return PseudoIr.X.Eq(PseudoIr.X.R("SF"), PseudoIr.X.C(0));
                case ConditionCode.o: return PseudoIr.X.Ne(PseudoIr.X.R("OF"), PseudoIr.X.C(0));
                case ConditionCode.no: return PseudoIr.X.Eq(PseudoIr.X.R("OF"), PseudoIr.X.C(0));
                case ConditionCode.p: return PseudoIr.X.Ne(PseudoIr.X.R("PF"), PseudoIr.X.C(0));
                case ConditionCode.np: return PseudoIr.X.Eq(PseudoIr.X.R("PF"), PseudoIr.X.C(0));
            }
        }

        // Fallback to flags
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
            _ => new PseudoIr.IntrinsicExpr("/* unknown condition */", Array.Empty<PseudoIr.Expr>())
        };
    }

    private static string ExprToText(PseudoIr.Expr e)
    {
        // Used only for fallback comments when emitting unknown conditional jump
        return e switch
        {
            PseudoIr.RegExpr r => r.Name,
            PseudoIr.Const c => (c.Value >= 10 ? "0x" + c.Value.ToString("X") : c.Value.ToString()),
            _ => "cond"
        };
    }

    // --------- Operand & address helpers -------------------------------------

    private PseudoIr.Expr LhsExpr(in Instruction i, Ctx ctx)
    {
        // Only register or (rarely) memory treated as lvalue via StoreStmt; so here, registers only
        if (i.Op0Kind == OpKind.Register)
            return PseudoIr.X.R(AsVarName(ctx, i.Op0Register));
        if (i.Op0Kind == OpKind.Memory)
        {
            // used by callers that still choose AssignStmt(lvalue) for memory; but we prefer StoreStmt
            var addr = AddressExpr(i, ctx, isLoadOrStoreAddr: true);
            return new PseudoIr.LoadExpr(addr, MemType(i), Seg(i));
        }
        // default
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
        // for comments only
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
    {
        return i.MemorySegment switch
        {
            Register.FS => PseudoIr.SegmentReg.FS,
            Register.GS => PseudoIr.SegmentReg.GS,
            _ => PseudoIr.SegmentReg.None
        };
    }

    private static PseudoIr.Expr AddressExpr(in Instruction i, Ctx ctx, bool isLoadOrStoreAddr)
    {
        // [gs:0x60] → peb
        if (i.MemorySegment == Register.GS &&
            i.MemoryBase == Register.None &&
            i.MemoryIndex == Register.None &&
            i.MemoryDisplacement64 == 0x60)
        {
            return PseudoIr.X.L("peb");
        }

        // RIP-relative absolute
        if (i.IsIPRelativeMemoryOperand)
        {
            ulong target = i.IPRelativeMemoryAddress;
            return new PseudoIr.UConst(target, 64);
        }

        // rbp/ebp locals: negative disp & no index → &local_N
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
        {
            expr = PseudoIr.X.R(AsVarName(ctx, i.MemoryBase));
        }
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
        // We only need a few simple cases for conditions built from LastCmp/LastBt
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

        // Heuristic (same as before): small value and pointer-ish dst
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
        {
            return e is PseudoIr.RegExpr rr && (rr.Name.Contains("rsp", StringComparison.OrdinalIgnoreCase)
                                             || rr.Name.StartsWith("p", StringComparison.Ordinal)
                                             || rr.Name.Contains("+ 0x", StringComparison.Ordinal));
        }
        static bool IsSmallLiteralOrZero(PseudoIr.Expr e)
        {
            if (e is PseudoIr.Const c) return c.Value >= -255 && c.Value <= 255;
            return e is PseudoIr.RegExpr r && (r.Name == "0" || r.Name == "eax" || r.Name == "edx"); // weak fallback
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

        // Args by MS x64: RCX,RDX,R8,R9
        var args = new PseudoIr.Expr[]
        {
            PseudoIr.X.R(Friendly(ctx, Register.RCX)),
            PseudoIr.X.R(Friendly(ctx, Register.RDX)),
            PseudoIr.X.R(Friendly(ctx, Register.R8)),
            PseudoIr.X.R(Friendly(ctx, Register.R9)),
        };

        // keep RAX named 'ret' after call
        ctx.RegName[Register.RAX] = "ret";

        return addr is null
            ? new PseudoIr.CallExpr(PseudoIr.CallTarget.ByName(targetRepr), args)
            : new PseudoIr.CallExpr(PseudoIr.CallTarget.Indirect(addr), args);
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
        // Minimal asm-like format
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
