using Vibe.Decompiler.Transformations;
using Xunit;

namespace Vibe.Decompiler.Tests.Transformations;

/// <summary>
/// Tests for the <see cref="SimplifyLogicalNotsPass"/> transformation.
/// </summary>
public class SimplifyLogicalNotsPassTests
{
    [Fact]
    public void RemovesDoubleNegation()
    {
        var fn = new IR.FunctionIR("test");
        var bb = new IR.BasicBlock(new IR.LabelSymbol("L0", 0));
        var inner = new IR.UnOpExpr(IR.UnOp.LNot, new IR.RegExpr("rax"));
        bb.Statements.Add(new IR.AssignStmt(new IR.RegExpr("rdx"),
            new IR.UnOpExpr(IR.UnOp.LNot, inner)));
        fn.Blocks.Add(bb);

        var pass = new SimplifyLogicalNotsPass();
        pass.Run(fn);

        var stmt = Assert.IsType<IR.AssignStmt>(fn.Blocks[0].Statements[0]);
        var reg = Assert.IsType<IR.RegExpr>(stmt.Rhs);
        Assert.Equal("rax", reg.Name);
    }

    [Fact]
    public void InvertsComparisonOperators()
    {
        var fn = new IR.FunctionIR("test");
        var bb = new IR.BasicBlock(new IR.LabelSymbol("L0", 0));
        var cmp = new IR.CompareExpr(IR.CmpOp.EQ, new IR.RegExpr("rax"), new IR.RegExpr("rbx"));
        bb.Statements.Add(new IR.AssignStmt(new IR.RegExpr("rdx"),
            new IR.UnOpExpr(IR.UnOp.LNot, cmp)));
        fn.Blocks.Add(bb);

        var pass = new SimplifyLogicalNotsPass();
        pass.Run(fn);

        var stmt = Assert.IsType<IR.AssignStmt>(fn.Blocks[0].Statements[0]);
        var newCmp = Assert.IsType<IR.CompareExpr>(stmt.Rhs);
        Assert.Equal(IR.CmpOp.NE, newCmp.Op);
    }

    [Fact]
    public void LeavesOtherExpressionsUnchanged()
    {
        var fn = new IR.FunctionIR("test");
        var bb = new IR.BasicBlock(new IR.LabelSymbol("L0", 0));
        bb.Statements.Add(new IR.AssignStmt(new IR.RegExpr("rdx"),
            new IR.UnOpExpr(IR.UnOp.LNot, new IR.RegExpr("rax"))));
        fn.Blocks.Add(bb);

        var pass = new SimplifyLogicalNotsPass();
        pass.Run(fn);

        var stmt = Assert.IsType<IR.AssignStmt>(fn.Blocks[0].Statements[0]);
        var lnot = Assert.IsType<IR.UnOpExpr>(stmt.Rhs);
        Assert.Equal(IR.UnOp.LNot, lnot.Op);
        Assert.IsType<IR.RegExpr>(lnot.Operand);
    }
}

