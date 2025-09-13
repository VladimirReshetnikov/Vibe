using System.Linq;

namespace Vibe.Decompiler.Transformations;

/// <summary>
/// Base class that provides virtual methods for rewriting IR statements and expressions.
/// Derived passes can override the relevant methods to transform specific nodes.
/// </summary>
public abstract class IRRewriter
{
    /// <summary>
    /// Dispatches to a more specific rewrite method based on the runtime type
    /// of the statement. Subclasses typically override the granular rewrite
    /// methods rather than this entry point.
    /// </summary>
    /// <param name="stmt">Statement to rewrite.</param>
    /// <returns>The rewritten statement.</returns>
    public virtual IR.Stmt RewriteStmt(IR.Stmt stmt) => stmt switch
    {
        IR.AssignStmt assign => RewriteAssign(assign),
        IR.StoreStmt store => RewriteStore(store),
        IR.CallStmt call => RewriteCall(call),
        IR.IfGotoStmt ifg => RewriteIfGoto(ifg),
        IR.ReturnStmt ret => RewriteReturn(ret),
        _ => stmt,
    };

    /// <summary>
    /// Rewrites an assignment statement by visiting both sides of the assignment.
    /// </summary>
    protected virtual IR.Stmt RewriteAssign(IR.AssignStmt assign)
        => new IR.AssignStmt(RewriteExpr(assign.Lhs), RewriteExpr(assign.Rhs));

    /// <summary>
    /// Rewrites a memory store statement.
    /// </summary>
    protected virtual IR.Stmt RewriteStore(IR.StoreStmt store)
        => new IR.StoreStmt(RewriteExpr(store.Address), RewriteExpr(store.Value), store.ElemType, store.Segment);

    /// <summary>
    /// Rewrites a call statement by rewriting the contained call expression.
    /// </summary>
    protected virtual IR.Stmt RewriteCall(IR.CallStmt call)
        => new IR.CallStmt((IR.CallExpr)RewriteExpr(call.Call));

    /// <summary>
    /// Rewrites the condition of a conditional branch statement.
    /// </summary>
    protected virtual IR.Stmt RewriteIfGoto(IR.IfGotoStmt ifg)
        => new IR.IfGotoStmt(RewriteExpr(ifg.Condition), ifg.Target);

    /// <summary>
    /// Rewrites the return value of a return statement when present.
    /// </summary>
    protected virtual IR.Stmt RewriteReturn(IR.ReturnStmt ret)
        => ret.Value is null ? ret : new IR.ReturnStmt(RewriteExpr(ret.Value));

    /// <summary>
    /// Dispatches to a more specific rewrite method based on the runtime type
    /// of the expression.
    /// </summary>
    public virtual IR.Expr RewriteExpr(IR.Expr expr) => expr switch
    {
        IR.BinOpExpr b => RewriteBinOp(b),
        IR.UnOpExpr u => RewriteUnOp(u),
        IR.CompareExpr c => RewriteCompare(c),
        IR.TernaryExpr t => RewriteTernary(t),
        IR.CastExpr ce => RewriteCast(ce),
        IR.CallExpr call => RewriteCallExpr(call),
        IR.LoadExpr ld => RewriteLoad(ld),
        _ => expr,
    };

    /// <summary>
    /// Rewrites a binary operation by visiting both operands.
    /// </summary>
    protected virtual IR.Expr RewriteBinOp(IR.BinOpExpr b)
        => new IR.BinOpExpr(b.Op, RewriteExpr(b.Left), RewriteExpr(b.Right));

    /// <summary>
    /// Rewrites a unary operation by visiting its operand.
    /// </summary>
    protected virtual IR.Expr RewriteUnOp(IR.UnOpExpr u)
        => new IR.UnOpExpr(u.Op, RewriteExpr(u.Operand));

    /// <summary>
    /// Rewrites a comparison expression.
    /// </summary>
    protected virtual IR.Expr RewriteCompare(IR.CompareExpr c)
        => new IR.CompareExpr(c.Op, RewriteExpr(c.Left), RewriteExpr(c.Right));

    /// <summary>
    /// Rewrites the components of a ternary expression.
    /// </summary>
    protected virtual IR.Expr RewriteTernary(IR.TernaryExpr t)
        => new IR.TernaryExpr(RewriteExpr(t.Condition), RewriteExpr(t.WhenTrue), RewriteExpr(t.WhenFalse));

    /// <summary>
    /// Rewrites a cast expression.
    /// </summary>
    protected virtual IR.Expr RewriteCast(IR.CastExpr ce)
        => new IR.CastExpr(RewriteExpr(ce.Value), ce.TargetType, ce.Kind);

    /// <summary>
    /// Rewrites a call expression by visiting each argument expression.
    /// </summary>
    protected virtual IR.Expr RewriteCallExpr(IR.CallExpr call)
        => new IR.CallExpr(call.Target, call.Args.Select(RewriteExpr).ToList());

    /// <summary>
    /// Rewrites a memory load expression.
    /// </summary>
    protected virtual IR.Expr RewriteLoad(IR.LoadExpr ld)
        => new IR.LoadExpr(RewriteExpr(ld.Address), ld.ElemType, ld.Segment);
}
