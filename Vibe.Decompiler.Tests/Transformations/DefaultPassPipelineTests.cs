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
        var field = typeof(PassManager).GetField("_passes", BindingFlags.NonPublic | BindingFlags.Instance);
        var passes = Assert.IsType<List<ITransformationPass>>(field!.GetValue(pm));
        Assert.Single(passes);
        Assert.IsType<SimplifyRedundantAssignPass>(passes[0]);
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
