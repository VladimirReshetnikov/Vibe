using Iced.Intel;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using static PseudoIr;
using Decoder = Iced.Intel.Decoder;

/* ============================================================
 *                P S E U D O  -  I R
 * Medium-level IR (MIR) for C-like pseudocode reconstructed from x64.
 * The decompiler below now *builds* this IR first, then pretty-prints it.
 *
 * Updates in this revision:
 *   1) Free-form pseudocode is emitted via a fictitious keyword:
 *        __pseudo(...)
 *      e.g., "__pseudo(compare eax, ebx);" instead of "/* compare eax, ebx *​/"
 *      Actual assembly is still shown as block comments interleaved with code.
 *
 *   2) New IR nodes:
 *        - AsmCommentStmt: keeps original disassembly lines (as comments)
 *        - PseudoStmt:     structured pseudo annotations -> __pseudo(...)
 *        - SymConst:       symbolic constants (e.g., STATUS_SUCCESS)
 *
 *   3) Refinement passes (simple & safe, run after IR construction):
 *        - ReplaceParamRegsWithParams: replace RegExpr("p1..p4") with ParamExpr
 *        - MapNamedReturnConstants:    map return immediates to named constants
 *        - SimplifyRedundantAssign:    drop x = x; and trivial no-ops
 *      (memset/memcpy peepholes from previous version remain in-place)
 *
 *   4) ConstantDatabase supports naming values (Win32 metadata). PrettyPrinter
 *      uses it for call-argument flags; MapNamedReturnConstants uses it for
 *      return codes (e.g., NTSTATUS).
 * ============================================================ */

public static class PseudoIr
{
    // ============================================================
    //  Types
    // ============================================================

    public abstract record IrType;

    public sealed record VoidType() : IrType { public override string ToString() => "void"; }
    public sealed record IntType(int Bits, bool IsSigned) : IrType { public override string ToString() => (IsSigned ? "i" : "u") + Bits; }
    public sealed record FloatType(int Bits) : IrType { public override string ToString() => "f" + Bits; }
    public sealed record PointerType(IrType Element) : IrType { public override string ToString() => $"ptr({Element})"; }
    public sealed record VectorType(int Bits) : IrType { public override string ToString() => $"v{Bits}"; }
    public sealed record UnknownType(string? Note = null) : IrType { public override string ToString() => Note is null ? "unknown" : $"unknown:{Note}"; }

    // ============================================================
    //  Expressions
    // ============================================================

    public abstract record Expr { public IrType? Type { get; init; } }

    public sealed record Const(long Value, int Bits) : Expr;
    public sealed record UConst(ulong Value, int Bits) : Expr;

    /// <summary>Symbolic constant (pretty-prints as Name, optionally with numeric)</summary>
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

    public enum BinOp { Add, Sub, Mul, UDiv, SDiv, URem, SRem, And, Or, Xor, Shl, Shr, Sar }
    public enum UnOp { Neg, Not, LNot }
    public enum CmpOp { EQ, NE, SLT, SLE, SGT, SGE, ULT, ULE, UGT, UGE }
    public enum CastKind { ZeroExtend, SignExtend, Trunc, Bitcast, Reinterpret }

    public sealed record CallTarget
    {
        public string? Symbol { get; init; }
        public Expr? Address { get; init; }
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

    /// <summary>Assembly disassembly line (always printed as a block comment)</summary>
    public sealed record AsmCommentStmt(string Text) : Stmt;

    /// <summary>Free-form pseudo annotation (printed as "__pseudo(...)")</summary>
    public sealed record PseudoStmt(string Text) : Stmt;

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

    public sealed record LabelSymbol(string Name, int Id) { public override string ToString() => Name; }

    public sealed class BasicBlock
    {
        public LabelSymbol Label { get; init; }
        public List<Stmt> Statements { get; } = new();
        public BasicBlock(LabelSymbol label) { Label = label; }
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
        public Expr? Initializer { get; set; }
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

        public FunctionIR(string name, ulong imageBase = 0, ulong entry = 0) { Name = name; ImageBase = imageBase; EntryAddress = entry; }
    }

    // ============================================================
    //  Constant naming hook
    // ============================================================

    public interface IConstantNameProvider
    {
        bool TryGetArgExpectedEnumType(string? callTargetSymbol, int argIndex, out string enumTypeFullName);
        bool TryFormatValue(string enumTypeFullName, ulong value, out string formatted);
    }

    // ============================================================
    //  Pretty-printer
    // ============================================================

    public sealed class PrettyPrinter
    {
        public sealed class Options
        {
            public bool EmitHeaderComment { get; set; } = true;
            public bool EmitBlockLabels { get; set; } = false;
            public bool CommentSignednessOnCmp { get; set; } = true;
            public bool UseStdIntNames { get; set; } = true;
            public string Indent { get; set; } = "    ";
            public IConstantNameProvider? ConstantProvider { get; set; }
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

            var ret = RenderType(fn.ReturnType);
            var paramList = string.Join(", ", fn.Parameters.OrderBy(p => p.Index).Select(p => $"{RenderType(p.Type)} {p.Name}"));

            EmitLine($"{ret} {fn.Name}({paramList}) {{");
            _indent++;

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
                    EmitLine("__pseudo(unsupported_structured_node);");
                    break;
            }
        }

        private void EmitStmt(Stmt s)
        {
            switch (s)
            {
                case AssignStmt a:
                    EmitIndent();
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
                    else { _sb.Append("return "); EmitExpr(r.Value, Precedence.Min); _sb.AppendLine(";"); }
                    break;

                case AsmCommentStmt c:
                    EmitLine($"/* {c.Text} */");
                    break;

                case PseudoStmt p:
                    EmitIndent(); _sb.Append("__pseudo(").Append(p.Text).AppendLine(");");
                    break;

                case NopStmt:
                    EmitIndent(); _sb.AppendLine("__pseudo(nop);");
                    break;

                default:
                    EmitIndent(); _sb.AppendLine("__pseudo(unsupported_stmt);");
                    break;
            }
        }

        private void EmitCall(CallExpr c)
        {
            if (c.Target.Symbol is not null) _sb.Append(c.Target.Symbol);
            else if (c.Target.Address is not null) { _sb.Append("(*"); EmitExpr(c.Target.Address, Precedence.Min); _sb.Append(")"); }
            else _sb.Append("indirect_call");

            _sb.Append("(");
            for (int i = 0; i < c.Args.Count; i++)
            {
                if (i > 0) _sb.Append(", ");
                if (_opt.ConstantProvider != null &&
                    _opt.ConstantProvider.TryGetArgExpectedEnumType(c.Target.Symbol, i, out var enumType) &&
                    TryEvalConst(c.Args[i], out var constVal) &&
                    _opt.ConstantProvider.TryFormatValue(enumType, constVal, out var fmt))
                {
                    _sb.Append(fmt);
                }
                else
                {
                    EmitExpr(c.Args[i], Precedence.Min);
                }
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
                    _sb.Append("__pseudo(unsupported_expr)");
                    break;
            }
        }

        private static bool TryEvalConst(Expr e, out ulong value)
        {
            switch (e)
            {
                case UConst uc: value = uc.Value; return true;
                case Const c: value = unchecked((ulong)c.Value); return true;
                case SymConst sc: value = sc.Value; return true;
                case BinOpExpr b when b.Op == BinOp.Or:
                    if (TryEvalConst(b.Left, out var l) && TryEvalConst(b.Right, out var r)) { value = l | r; return true; }
                    break;
                case BinOpExpr b when b.Op == BinOp.Add:
                    if (TryEvalConst(b.Left, out var l2) && TryEvalConst(b.Right, out var r2)) { value = l2 + r2; return true; }
                    break;
            }
            value = 0;
            return false;
        }

        private void EmitLValue(Expr lhs)
        {
            switch (lhs)
            {
                case RegExpr or LocalExpr or ParamExpr:
                    EmitExpr(lhs, Precedence.Min);
                    break;
                case LoadExpr ld:
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

        private void EmitConst(long v, int bits)
        {
            if (v < 0) _sb.Append("0x").Append(unchecked((ulong)v).ToString("X"));
            else if (v >= 10) _sb.Append("0x").Append(v.ToString("X"));
            else _sb.Append(v.ToString(CultureInfo.InvariantCulture));
        }

        private void EmitUConst(ulong v, int bits)
        {
            if (v >= 10) _sb.Append("0x").Append(v.ToString("X"));
            else _sb.Append(v.ToString(CultureInfo.InvariantCulture));
        }

        private static class Precedence
        {
            public const int Min = 0, Assign = 1, Cond = 2, Or = 3, And = 4, Xor = 5, BitAnd = 6, Rel = 7, Shift = 8, Add = 9, Mul = 10, Prefix = 11, Atom = 12;
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

        private string RenderCast(CastExpr c) => $"({RenderType(c.TargetType)})";

        private string RenderType(IrType t) => t switch
        {
            VoidType => "void",
            IntType it => RenderIntType(it),
            FloatType ft => ft.Bits == 32 ? "float" : ft.Bits == 64 ? "double" : $"float{ft.Bits}_t",
            PointerType pt => $"{RenderType(pt.Element)}*",
            VectorType vt => vt.Bits switch { 128 => "vec128_t", 256 => "vec256_t", 512 => "vec512_t", _ => $"vec{vt.Bits}_t" },
            UnknownType ut => ut.Note is null ? "uint64_t /* unknown */" : $"uint64_t /* {ut.Note} */",
            _ => "uint64_t /* ? */"
        };

        private string RenderIntType(IntType t)
            => _opt.UseStdIntNames ? (t.IsSigned ? "int" : "uint") + t.Bits + "_t"
                                   : t.Bits switch
                                   {
                                       8 => t.IsSigned ? "signed char" : "unsigned char",
                                       16 => t.IsSigned ? "short" : "unsigned short",
                                       32 => t.IsSigned ? "int" : "unsigned int",
                                       64 => t.IsSigned ? "long long" : "unsigned long long",
                                       _ => t.IsSigned ? $"int{t.Bits}_t" : $"uint{t.Bits}_t"
                                   };

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
        public static SymConst SC(ulong v, int bits, string name) => new(v, bits, name);

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
 *   - Builds PseudoIr.FunctionIR
 *   - Runs refinement passes
 *   - Pretty-prints (with disassembly comments and __pseudo annotations)
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

/* ============================================================
 *   C O N S T A N T   D A T A B A S E
 *   (Win32 Metadata-backed constant/flags naming)
 * ============================================================ */

public sealed class ConstantDatabase : PseudoIr.IConstantNameProvider
{
    private readonly Dictionary<string, EnumDesc> _enums = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<int, string>> _callArgEnums = new(StringComparer.OrdinalIgnoreCase);

    public ConstantDatabase()
    {
        MapArgEnum("VirtualAlloc", 2, "Windows.Win32.System.Memory.MEMORY_ALLOCATION_TYPE");
        MapArgEnum("VirtualAlloc", 3, "Windows.Win32.System.Memory.PAGE_PROTECTION_FLAGS");
        MapArgEnum("VirtualProtect", 2, "Windows.Win32.System.Memory.PAGE_PROTECTION_FLAGS");
        MapArgEnum("OpenProcess", 1, "Windows.Win32.System.Threading.PROCESS_ACCESS_RIGHTS");
        MapArgEnum("LoadLibraryExW", 2, "Windows.Win32.System.LibraryLoader.LOAD_LIBRARY_FLAGS");
        MapArgEnum("CreateFileW", 1, "Windows.Win32.Storage.FileSystem.FILE_ACCESS_RIGHTS");
        MapArgEnum("CreateFileW", 2, "Windows.Win32.Storage.FileSystem.FILE_SHARE_MODE");
        MapArgEnum("CreateFileW", 5, "Windows.Win32.Storage.FileSystem.FILE_FLAGS_AND_ATTRIBUTES");
    }

    public void MapArgEnum(string callSymbolName, int argIndex, string enumFullName)
    {
        if (!_callArgEnums.TryGetValue(callSymbolName, out var map))
            _callArgEnums[callSymbolName] = map = new Dictionary<int, string>();
        map[argIndex] = enumFullName;
    }

    public bool TryGetArgExpectedEnumType(string? callTargetSymbol, int argIndex, out string enumTypeFullName)
    {
        enumTypeFullName = "";
        if (string.IsNullOrEmpty(callTargetSymbol)) return false;
        var sym = callTargetSymbol!;
        int bang = sym.IndexOf('!');
        if (bang >= 0 && bang + 1 < sym.Length) sym = sym[(bang + 1)..];

        if (_callArgEnums.TryGetValue(sym, out var map) && map.TryGetValue(argIndex, out enumTypeFullName))
            return _enums.ContainsKey(enumTypeFullName);
        return false;
    }

    public bool TryFormatValue(string enumTypeFullName, ulong value, out string formatted)
    {
        formatted = "";
        if (!_enums.TryGetValue(enumTypeFullName, out var ed)) return false;

        if (ed.ValueToName.TryGetValue(value, out var exact))
        {
            formatted = exact;
            return true;
        }

        if (ed.Flags || ed.LooksLikeFlags)
        {
            var parts = new List<string>();
            ulong remaining = value;
            foreach (var p in ed.FlagParts)
            {
                if ((remaining & p.Mask) == p.Mask)
                {
                    parts.Add(p.Name);
                    remaining &= ~p.Mask;
                }
            }
            if (parts.Count > 0 && remaining == 0)
            {
                formatted = string.Join(" | ", parts);
                return true;
            }
        }

        formatted = $"0x{value:X}";
        return true;
    }

    public void LoadWin32MetadataFromWinmd(string winmdPath)
    {
        using var fs = File.OpenRead(winmdPath);
        using var pe = new PEReader(fs, PEStreamOptions.PrefetchEntireImage);
        var md = pe.GetMetadataReader();

        foreach (var tdHandle in md.TypeDefinitions)
        {
            var td = md.GetTypeDefinition(tdHandle);
            string ns = md.GetString(td.Namespace);
            string name = md.GetString(td.Name);
            string full = string.IsNullOrEmpty(ns) ? name : ns + "." + name;

            if (!IsEnum(md, td)) continue;

            var desc = new EnumDesc(full);
            desc.Flags = HasFlagsAttribute(md, td);

            int bits = 32;
            foreach (var fHandle in td.GetFields())
            {
                var f = md.GetFieldDefinition(fHandle);
                string fName = md.GetString(f.Name);
                if (fName == "value__")
                {
                    bits = UnderlyingBitsFromSignature(md, f);
                    break;
                }
            }
            desc.UnderlyingBits = bits;

            foreach (var fHandle in td.GetFields())
            {
                var f = md.GetFieldDefinition(fHandle);
                if (!f.GetDefaultValue().IsNil)
                {
                    string fName = md.GetString(f.Name);
                    ulong val = ReadConstantValueAsUInt64(md, f.GetDefaultValue());
                    if (!desc.ValueToName.ContainsKey(val))
                        desc.ValueToName[val] = $"{full}.{fName}";
                }
            }

            desc.FinalizeAfterLoad();
            _enums[full] = desc;
        }
    }

    public void LoadFromAssembly(Assembly asm)
    {
        foreach (var t in asm.GetTypes())
        {
            if (t.IsEnum)
            {
                var full = t.FullName ?? t.Name;
                var desc = new EnumDesc(full)
                {
                    Flags = t.GetCustomAttributes(typeof(FlagsAttribute), inherit: false).Any(),
                    UnderlyingBits = Math.Max(8, Math.Min(64, System.Runtime.InteropServices.Marshal.SizeOf(Enum.GetUnderlyingType(t)) * 8))
                };
                foreach (var name in Enum.GetNames(t))
                {
                    var valObj = Convert.ChangeType(Enum.Parse(t, name), Enum.GetUnderlyingType(t));
                    ulong v = ConvertToUInt64(valObj);
                    if (!desc.ValueToName.ContainsKey(v))
                        desc.ValueToName[v] = $"{full}.{name}";
                }
                desc.FinalizeAfterLoad();
                _enums[full] = desc;
            }
            else if (t.IsAbstract && t.IsSealed)
            {
                foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (!f.IsLiteral) continue;
                    object? val = f.GetRawConstantValue();
                    if (val is null) continue;
                    var full = t.FullName ?? t.Name;
                    if (!_enums.TryGetValue(full, out var desc))
                    {
                        desc = new EnumDesc(full) { Flags = false, UnderlyingBits = 32 };
                        _enums[full] = desc;
                    }
                    ulong v = ConvertToUInt64(val);
                    string key = $"{full}.{f.Name}";
                    if (!desc.ValueToName.ContainsKey(v))
                        desc.ValueToName[v] = key;
                }
            }
        }
        foreach (var e in _enums.Values) e.FinalizeAfterLoad();
    }

    private static bool IsEnum(MetadataReader md, TypeDefinition td)
    {
        var bt = td.BaseType;
        if (bt.Kind != HandleKind.TypeReference) return false;
        var tr = md.GetTypeReference((TypeReferenceHandle)bt);
        string ns = md.GetString(tr.Namespace);
        string n = md.GetString(tr.Name);
        return ns == "System" && n == "Enum";
    }

    private static bool HasFlagsAttribute(MetadataReader md, TypeDefinition td)
    {
        foreach (var caHandle in td.GetCustomAttributes())
        {
            var ca = md.GetCustomAttribute(caHandle);
            if (TryGetAttributeTypeName(md, ca.Constructor, out var attNs, out var attName))
            {
                if (attNs == "System" && attName == "FlagsAttribute") return true;
            }
        }
        return false;
    }

    private static bool TryGetAttributeTypeName(MetadataReader md, EntityHandle ctor, out string ns, out string name)
    {
        ns = ""; name = "";
        if (ctor.Kind == HandleKind.MemberReference)
        {
            var mr = md.GetMemberReference((MemberReferenceHandle)ctor);
            if (mr.Parent.Kind == HandleKind.TypeReference)
            {
                var tr = md.GetTypeReference((TypeReferenceHandle)mr.Parent);
                ns = md.GetString(tr.Namespace);
                name = md.GetString(tr.Name);
                return true;
            }
            if (mr.Parent.Kind == HandleKind.TypeDefinition)
            {
                var td = md.GetTypeDefinition((TypeDefinitionHandle)mr.Parent);
                ns = md.GetString(td.Namespace);
                name = md.GetString(td.Name);
                return true;
            }
        }
        else if (ctor.Kind == HandleKind.MethodDefinition)
        {
            var mdh = (MethodDefinitionHandle)ctor;
            var mdDef = md.GetMethodDefinition(mdh);
            var td = md.GetTypeDefinition(mdDef.GetDeclaringType());
            ns = md.GetString(td.Namespace);
            name = md.GetString(td.Name);
            return true;
        }
        return false;
    }

    private static int UnderlyingBitsFromSignature(MetadataReader md, FieldDefinition f)
    {
        var sig = md.GetBlobReader(f.Signature);
        if (sig.ReadByte() != 0x06) return 32;
        var (bits, _) = ReadTypeCode(sig);
        return bits == 0 ? 32 : bits;
    }

    private static (int bits, bool isSigned) ReadTypeCode(BlobReader br)
    {
        var code = (SignatureTypeCode)br.ReadCompressedInteger();
        return code switch
        {
            SignatureTypeCode.SByte => (8, true),
            SignatureTypeCode.Byte => (8, false),
            SignatureTypeCode.Int16 => (16, true),
            SignatureTypeCode.UInt16 => (16, false),
            SignatureTypeCode.Int32 => (32, true),
            SignatureTypeCode.UInt32 => (32, false),
            SignatureTypeCode.Int64 => (64, true),
            SignatureTypeCode.UInt64 => (64, false),
            _ => (0, false)
        };
    }

    private static ulong ReadConstantValueAsUInt64(MetadataReader md, ConstantHandle ch)
    {
        var c = md.GetConstant(ch);
        var br = md.GetBlobReader(c.Value);
        return c.TypeCode switch
        {
            ConstantTypeCode.SByte => unchecked((ulong)(sbyte)br.ReadSByte()),
            ConstantTypeCode.Byte => br.ReadByte(),
            ConstantTypeCode.Int16 => unchecked((ulong)br.ReadInt16()),
            ConstantTypeCode.UInt16 => br.ReadUInt16(),
            ConstantTypeCode.Int32 => unchecked((ulong)br.ReadInt32()),
            ConstantTypeCode.UInt32 => br.ReadUInt32(),
            ConstantTypeCode.Int64 => unchecked((ulong)br.ReadInt64()),
            ConstantTypeCode.UInt64 => br.ReadUInt64(),
            _ => 0UL
        };
    }

    private static ulong ConvertToUInt64(object v)
        => v switch
        {
            sbyte a => unchecked((ulong)a),
            byte b => b,
            short s => unchecked((ulong)s),
            ushort us => us,
            int i => unchecked((ulong)i),
            uint ui => ui,
            long l => unchecked((ulong)l),
            ulong ul => ul,
            _ => 0UL
        };

    private sealed class EnumDesc
    {
        public string FullName { get; }
        public int UnderlyingBits { get; set; } = 32;
        public bool Flags { get; set; }
        public bool LooksLikeFlags { get; private set; }
        public readonly Dictionary<ulong, string> ValueToName = new();
        public readonly List<(ulong Mask, string Name)> FlagParts = new();

        public EnumDesc(string full) { FullName = full; }

        public void FinalizeAfterLoad()
        {
            var singles = ValueToName.Keys.Where(v => v != 0 && (v & (v - 1)) == 0).ToList();
            LooksLikeFlags = Flags || singles.Count >= Math.Max(1, ValueToName.Count / 2);

            if (LooksLikeFlags)
            {
                foreach (var s in singles) FlagParts.Add((s, ValueToName[s]));
                FlagParts.Sort((a, b) => b.Mask.CompareTo(a.Mask));
            }
        }
    }
}
