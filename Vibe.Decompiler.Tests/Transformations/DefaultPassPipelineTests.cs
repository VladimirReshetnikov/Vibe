using System.Collections.Generic;
using System.Reflection;
using Vibe.Decompiler.Transformations;
using Xunit;

namespace Vibe.Decompiler.Tests.Transformations;

/// <summary>
/// Unit tests ensuring that the default pass pipeline contains key
/// optimisation passes and that they perform their intended mutations on the
/// IR.
/// </summary>
public class DefaultPassPipelineTests
{
    /// <summary>
    /// The pipeline should include <see cref="SimplifyRedundantAssignPass"/>
    /// which removes pointless self-assignments.
    /// </summary>
    [Fact]
    public void CreateIncludesSimplifyRedundantAssignPass()
    {
        var pm = DefaultPassPipeline.Create();
        // Test behavior instead of implementation details
        var fn = new IR.FunctionIR("test");
        var bb = new IR.BasicBlock(new IR.LabelSymbol("L0", 0));
        bb.Statements.Add(new IR.AssignStmt(new IR.RegExpr("rax"), new IR.RegExpr("rax")));
        fn.Blocks.Add(bb);

        pm.Run(fn);

        // Verify that SimplifyRedundantAssignPass is included by checking its behavior
        Assert.IsType<IR.NopStmt>(fn.Blocks[0].Statements[0]);
    }

    /// <summary>
    /// The pipeline should fold constant expressions to their computed value.
    /// </summary>
    [Fact]
    public void CreateIncludesFoldConstantsPass()
    {
        var pm = DefaultPassPipeline.Create();
        var fn = new IR.FunctionIR("test");
        var bb = new IR.BasicBlock(new IR.LabelSymbol("L0", 0));
        bb.Statements.Add(new IR.AssignStmt(new IR.RegExpr("rax"),
            new IR.BinOpExpr(IR.BinOp.Add, new IR.Const(2, 32), new IR.Const(3, 32))));
        fn.Blocks.Add(bb);

        pm.Run(fn);

        var stmt = Assert.IsType<IR.AssignStmt>(fn.Blocks[0].Statements[0]);
        var c = Assert.IsType<IR.Const>(stmt.Rhs);
        Assert.Equal(5, c.Value);
    }

    /// <summary>
    /// The pipeline includes <see cref="SimplifyBooleanTernaryPass"/> to reduce
    /// boolean ternary expressions to simple registers when possible.
    /// </summary>
    [Fact]
    public void CreateIncludesSimplifyBooleanTernaryPass()
    {
        var pm = DefaultPassPipeline.Create();
        var fn = new IR.FunctionIR("test");
        var bb = new IR.BasicBlock(new IR.LabelSymbol("L0", 0));
        bb.Statements.Add(new IR.AssignStmt(new IR.RegExpr("rax"),
            new IR.TernaryExpr(new IR.RegExpr("rcx"), new IR.Const(1, 1), new IR.Const(0, 1))));
        fn.Blocks.Add(bb);

        pm.Run(fn);

        var stmt = Assert.IsType<IR.AssignStmt>(fn.Blocks[0].Statements[0]);
        Assert.IsType<IR.RegExpr>(stmt.Rhs);
    }
}
