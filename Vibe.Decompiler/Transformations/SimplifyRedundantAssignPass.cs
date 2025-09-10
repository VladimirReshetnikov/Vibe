namespace Vibe.Decompiler.Transformations;

/// <summary>
/// Replaces assignments of a register to itself with a no-op statement.
/// </summary>
public sealed class SimplifyRedundantAssignPass : IRRewriter, ITransformationPass
{
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

    protected override IR.Stmt RewriteAssign(IR.AssignStmt assign)
    {
        if (assign.Lhs is IR.RegExpr rl && assign.Rhs is IR.RegExpr rr && rl.Name == rr.Name)
            return new IR.NopStmt();
        return base.RewriteAssign(assign);
    }
}
