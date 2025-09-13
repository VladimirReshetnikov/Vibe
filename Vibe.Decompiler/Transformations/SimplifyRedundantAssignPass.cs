namespace Vibe.Decompiler.Transformations;

/// <summary>
/// Replaces assignments of a register to itself with a no-op statement.
/// </summary>
public sealed class SimplifyRedundantAssignPass : IRRewriter, ITransformationPass
{
    /// <summary>
    /// Scans the function for trivial assignments where a register is assigned
    /// to itself and replaces those statements with <see cref="IR.NopStmt"/>.
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
    /// Performs the actual detection of self-assignments during the rewrite phase.
    /// </summary>
    protected override IR.Stmt RewriteAssign(IR.AssignStmt assign)
    {
        if (assign.Lhs is IR.RegExpr rl && assign.Rhs is IR.RegExpr rr && rl.Name == rr.Name)
            return new IR.NopStmt();
        return base.RewriteAssign(assign);
    }
}
