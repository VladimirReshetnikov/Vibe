namespace Vibe.Decompiler.Transformations;

/// <summary>
/// Simplifies boolean ternary expressions such as <c>cond ? 1 : 0</c>.
/// </summary>
public sealed class SimplifyBooleanTernaryPass : IRRewriter, ITransformationPass
{
    /// <summary>
    /// Executes the transformation over all statements in the function.
    /// </summary>
    public void Run(IR.FunctionIR fn)
    {
        foreach (var bb in fn.Blocks)
        {
            for (int i = 0; i < bb.Statements.Count; i++)
            {
                bb.Statements[i] = RewriteStmt(bb.Statements[i]);
            }
        }
    }

    /// <summary>
    /// Rewrites ternary expressions and applies boolean simplification rules.
    /// </summary>
    protected override IR.Expr RewriteTernary(IR.TernaryExpr t)
    {
        var cond = RewriteExpr(t.Condition);
        var whenTrue = RewriteExpr(t.WhenTrue);
        var whenFalse = RewriteExpr(t.WhenFalse);

        if (IsOne(whenTrue) && IsZero(whenFalse))
            return cond;
        if (IsZero(whenTrue) && IsOne(whenFalse))
            return new IR.UnOpExpr(IR.UnOp.LNot, cond);
        if (whenTrue.Equals(whenFalse))
            return IsSideEffectFree(cond) ? whenTrue : new IR.TernaryExpr(cond, whenTrue, whenFalse);

        return new IR.TernaryExpr(cond, whenTrue, whenFalse);
    }

    private static bool IsZero(IR.Expr x) => x is IR.Const c && c.Value == 0 || x is IR.UConst uc && uc.Value == 0;
    private static bool IsOne(IR.Expr x) => x is IR.Const c && c.Value == 1 || x is IR.UConst uc && uc.Value == 1;
    private static bool IsSideEffectFree(IR.Expr e) => e switch
    {
        IR.Const or IR.UConst or IR.SymConst or IR.RegExpr or IR.ParamExpr or IR.LocalExpr or IR.SegmentBaseExpr => true,
        IR.AddrOfExpr a => IsSideEffectFree(a.Operand),
        IR.BinOpExpr b => IsSideEffectFree(b.Left) && IsSideEffectFree(b.Right),
        IR.UnOpExpr u => IsSideEffectFree(u.Operand),
        IR.CompareExpr c => IsSideEffectFree(c.Left) && IsSideEffectFree(c.Right),
        IR.TernaryExpr t => IsSideEffectFree(t.Condition) && IsSideEffectFree(t.WhenTrue) && IsSideEffectFree(t.WhenFalse),
        IR.CastExpr ce => IsSideEffectFree(ce.Value),
        IR.LabelRefExpr => true,
        _ => false,
    };
}

