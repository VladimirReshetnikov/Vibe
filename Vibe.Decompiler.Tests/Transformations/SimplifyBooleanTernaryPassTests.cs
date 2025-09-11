using System;
using Vibe.Decompiler.Transformations;
using Xunit;

namespace Vibe.Decompiler.Tests.Transformations;

/// <summary>
/// Tests for the <see cref="SimplifyBooleanTernaryPass"/> transformation.
/// </summary>
public class SimplifyBooleanTernaryPassTests
{
    [Fact]
    public void ReplacesTrueFalsePatternWithCondition()
    {
        var fn = new IR.FunctionIR("test");
        var bb = new IR.BasicBlock(new IR.LabelSymbol("L0", 0));
        var cond = new IR.RegExpr("rcx");
        bb.Statements.Add(new IR.AssignStmt(new IR.RegExpr("rax"),
            new IR.TernaryExpr(cond, new IR.Const(1, 1), new IR.Const(0, 1))));
        fn.Blocks.Add(bb);

        var pass = new SimplifyBooleanTernaryPass();
        pass.Run(fn);

        var stmt = Assert.IsType<IR.AssignStmt>(fn.Blocks[0].Statements[0]);
        var reg = Assert.IsType<IR.RegExpr>(stmt.Rhs);
        Assert.Equal("rcx", reg.Name);
    }

    [Fact]
    public void ReplacesFalseTruePatternWithNotCondition()
    {
        var fn = new IR.FunctionIR("test");
        var bb = new IR.BasicBlock(new IR.LabelSymbol("L0", 0));
        var cond = new IR.RegExpr("rcx");
        bb.Statements.Add(new IR.AssignStmt(new IR.RegExpr("rax"),
            new IR.TernaryExpr(cond, new IR.Const(0, 1), new IR.Const(1, 1))));
        fn.Blocks.Add(bb);

        var pass = new SimplifyBooleanTernaryPass();
        pass.Run(fn);

        var stmt = Assert.IsType<IR.AssignStmt>(fn.Blocks[0].Statements[0]);
        var un = Assert.IsType<IR.UnOpExpr>(stmt.Rhs);
        Assert.Equal(IR.UnOp.LNot, un.Op);
    }

    [Fact]
    public void RemovesRedundantTernaryWhenBranchesEqualAndConditionSideEffectFree()
    {
        var fn = new IR.FunctionIR("test");
        var bb = new IR.BasicBlock(new IR.LabelSymbol("L0", 0));
        var cond = new IR.RegExpr("rcx");
        var branch = new IR.RegExpr("rdx");
        bb.Statements.Add(new IR.AssignStmt(new IR.RegExpr("rax"),
            new IR.TernaryExpr(cond, branch, branch)));
        fn.Blocks.Add(bb);

        var pass = new SimplifyBooleanTernaryPass();
        pass.Run(fn);

        var stmt = Assert.IsType<IR.AssignStmt>(fn.Blocks[0].Statements[0]);
        var reg = Assert.IsType<IR.RegExpr>(stmt.Rhs);
        Assert.Equal("rdx", reg.Name);
    }

    [Fact]
    public void PreservesTernaryWhenConditionHasSideEffects()
    {
        var fn = new IR.FunctionIR("test");
        var bb = new IR.BasicBlock(new IR.LabelSymbol("L0", 0));
        var cond = new IR.CallExpr(IR.CallTarget.ByName("foo"), Array.Empty<IR.Expr>());
        var branch = new IR.RegExpr("rdx");
        bb.Statements.Add(new IR.AssignStmt(new IR.RegExpr("rax"),
            new IR.TernaryExpr(cond, branch, branch)));
        fn.Blocks.Add(bb);

        var pass = new SimplifyBooleanTernaryPass();
        pass.Run(fn);

        var stmt = Assert.IsType<IR.AssignStmt>(fn.Blocks[0].Statements[0]);
        var tern = Assert.IsType<IR.TernaryExpr>(stmt.Rhs);
        Assert.IsType<IR.CallExpr>(tern.Condition);
    }
}

