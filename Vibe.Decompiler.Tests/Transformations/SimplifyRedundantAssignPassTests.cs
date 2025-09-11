using Vibe.Decompiler.Transformations;
using Xunit;

namespace Vibe.Decompiler.Tests.Transformations;

/// <summary>
/// Tests for the <see cref="SimplifyRedundantAssignPass"/> transformation.
/// </summary>
public class SimplifyRedundantAssignPassTests
{
    /// <summary>
    /// Replaces assignments of a register to itself with a no-op statement.
    /// </summary>
    [Fact]
    public void RemovesSelfAssignments()
    {
        var fn = new IR.FunctionIR("test");
        var bb = new IR.BasicBlock(new IR.LabelSymbol("L0", 0));
        bb.Statements.Add(new IR.AssignStmt(new IR.RegExpr("rax"), new IR.RegExpr("rax")));
        fn.Blocks.Add(bb);

        var pass = new SimplifyRedundantAssignPass();
        pass.Run(fn);

        Assert.IsType<IR.NopStmt>(fn.Blocks[0].Statements[0]);
    }

    [Fact]
    public void LeavesNonSelfAssignmentsUnchanged()
    {
        var fn = new IR.FunctionIR("test");
        var bb = new IR.BasicBlock(new IR.LabelSymbol("L0", 0));
        var assign = new IR.AssignStmt(new IR.RegExpr("rax"), new IR.RegExpr("rbx"));
        bb.Statements.Add(assign);
        fn.Blocks.Add(bb);

        var pass = new SimplifyRedundantAssignPass();
        pass.Run(fn);

        var result = Assert.IsType<IR.AssignStmt>(fn.Blocks[0].Statements[0]);
        Assert.Equal("rax", ((IR.RegExpr)result.Lhs).Name);
        Assert.Equal("rbx", ((IR.RegExpr)result.Rhs).Name);
    }

    [Fact]
    public void SimplifiesAllSelfAssignments()
    {
        var fn = new IR.FunctionIR("test");
        var bb = new IR.BasicBlock(new IR.LabelSymbol("L0", 0));
        bb.Statements.Add(new IR.AssignStmt(new IR.RegExpr("rax"), new IR.RegExpr("rax")));
        bb.Statements.Add(new IR.AssignStmt(new IR.RegExpr("rbx"), new IR.RegExpr("rbx")));
        fn.Blocks.Add(bb);

        var pass = new SimplifyRedundantAssignPass();
        pass.Run(fn);

        foreach (var stmt in fn.Blocks[0].Statements)
            Assert.IsType<IR.NopStmt>(stmt);
    }
}
