namespace Vibe.Decompiler.Transformations;

/// <summary>
/// Simplifies logical NOT expressions by eliminating double negations and
/// inverting comparison operators.
/// </summary>
public sealed class SimplifyLogicalNotsPass : IRRewriter, ITransformationPass
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
    /// Rewrites logical NOT expressions and applies simplification rules.
    /// </summary>
    protected override IR.Expr RewriteUnOp(IR.UnOpExpr u)
    {
        var operand = RewriteExpr(u.Operand);

        if (u.Op != IR.UnOp.LNot)
            return new IR.UnOpExpr(u.Op, operand);

        return operand switch
        {
            IR.UnOpExpr inner when inner.Op == IR.UnOp.LNot => inner.Operand,
            IR.CompareExpr cmp => new IR.CompareExpr(Invert(cmp.Op), cmp.Left, cmp.Right),
            _ => new IR.UnOpExpr(IR.UnOp.LNot, operand),
        };
    }

    private static IR.CmpOp Invert(IR.CmpOp op) => op switch
    {
        IR.CmpOp.EQ => IR.CmpOp.NE,
        IR.CmpOp.NE => IR.CmpOp.EQ,
        IR.CmpOp.SLT => IR.CmpOp.SGE,
        IR.CmpOp.SLE => IR.CmpOp.SGT,
        IR.CmpOp.SGT => IR.CmpOp.SLE,
        IR.CmpOp.SGE => IR.CmpOp.SLT,
        IR.CmpOp.ULT => IR.CmpOp.UGE,
        IR.CmpOp.ULE => IR.CmpOp.UGT,
        IR.CmpOp.UGT => IR.CmpOp.ULE,
        IR.CmpOp.UGE => IR.CmpOp.ULT,
        _ => op,
    };
}

