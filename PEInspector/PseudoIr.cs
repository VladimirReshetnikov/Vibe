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
public static partial class PseudoIr
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
