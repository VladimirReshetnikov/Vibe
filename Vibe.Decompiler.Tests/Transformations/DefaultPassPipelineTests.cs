using System.Collections.Generic;
using System.Reflection;
using Vibe.Decompiler.Transformations;
using Xunit;

namespace Vibe.Decompiler.Tests.Transformations;

public class DefaultPassPipelineTests
{
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

    [Fact]
    public void PipelineTransformsFunction()
    {
        var fn = new IR.FunctionIR("test");
        var bb = new IR.BasicBlock(new IR.LabelSymbol("L0", 0));
        bb.Statements.Add(new IR.AssignStmt(new IR.RegExpr("rax"), new IR.RegExpr("rax")));
        fn.Blocks.Add(bb);

        var pm = DefaultPassPipeline.Create();
        pm.Run(fn);

        Assert.IsType<IR.NopStmt>(fn.Blocks[0].Statements[0]);
    }
}
