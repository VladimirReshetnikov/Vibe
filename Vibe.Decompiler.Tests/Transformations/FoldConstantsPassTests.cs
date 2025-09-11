using Vibe.Decompiler.Transformations;
using Xunit;

namespace Vibe.Decompiler.Tests.Transformations;

/// <summary>
/// Tests for <see cref="FoldConstantsPass"/>.
/// </summary>
public class FoldConstantsPassTests
{
    /// <summary>
    /// Folds simple arithmetic expressions composed only of constants.
    /// </summary>
    [Fact]
    public void FoldsArithmeticConstants()
    {
        var fn = new IR.FunctionIR("test");
        var bb = new IR.BasicBlock(new IR.LabelSymbol("L0", 0));
        bb.Statements.Add(new IR.AssignStmt(new IR.RegExpr("rdx"),
            new IR.BinOpExpr(IR.BinOp.Add, new IR.Const(2, 32), new IR.Const(3, 32))));
        fn.Blocks.Add(bb);

        var pass = new FoldConstantsPass();
        pass.Run(fn);

        var stmt = Assert.IsType<IR.AssignStmt>(fn.Blocks[0].Statements[0]);
        var c = Assert.IsType<IR.Const>(stmt.Rhs);
        Assert.Equal(5, c.Value);
    }

    /// <summary>
    /// Recursively folds nested constant expressions.
    /// </summary>
    [Fact]
    public void FoldsNestedConstants()
    {
        var fn = new IR.FunctionIR("test");
        var bb = new IR.BasicBlock(new IR.LabelSymbol("L0", 0));
        var mul = new IR.BinOpExpr(IR.BinOp.Mul, new IR.Const(3, 32), new IR.Const(4, 32));
        bb.Statements.Add(new IR.AssignStmt(new IR.RegExpr("rdx"),
            new IR.BinOpExpr(IR.BinOp.Add, new IR.Const(2, 32), mul)));
        fn.Blocks.Add(bb);

        var pass = new FoldConstantsPass();
        pass.Run(fn);

        var stmt = Assert.IsType<IR.AssignStmt>(fn.Blocks[0].Statements[0]);
        var c = Assert.IsType<IR.Const>(stmt.Rhs);
        Assert.Equal(14, c.Value);
    }

    /// <summary>
    /// Folds signed comparisons on unsigned constants with the high bit set.
    /// </summary>
    [Fact]
    public void FoldsSignedComparisonOnUConsts()
    {
        var fn = new IR.FunctionIR("test");
        var bb = new IR.BasicBlock(new IR.LabelSymbol("L0", 0));
        bb.Statements.Add(new IR.AssignStmt(new IR.RegExpr("rdx"),
            new IR.CompareExpr(IR.CmpOp.SLT,
                new IR.UConst(0x80000000, 32),
                new IR.UConst(0, 32))));
        fn.Blocks.Add(bb);

        var pass = new FoldConstantsPass();
        pass.Run(fn);

        var stmt = Assert.IsType<IR.AssignStmt>(fn.Blocks[0].Statements[0]);
        var c = Assert.IsType<IR.Const>(stmt.Rhs);
        Assert.Equal(1, c.Value);
    }

    /// <summary>
    /// Handles shift amounts greater than or equal to the bit width.
    /// </summary>
    [Fact]
    public void FoldsLargeShiftAmounts()
    {
        var fn = new IR.FunctionIR("test");
        var bb = new IR.BasicBlock(new IR.LabelSymbol("L0", 0));
        bb.Statements.Add(new IR.AssignStmt(new IR.RegExpr("rdx"),
            new IR.BinOpExpr(IR.BinOp.Shl,
                new IR.UConst(1, 32),
                new IR.UConst(32, 32))));
        fn.Blocks.Add(bb);

        var pass = new FoldConstantsPass();
        pass.Run(fn);

        var stmt = Assert.IsType<IR.AssignStmt>(fn.Blocks[0].Statements[0]);
        var c = Assert.IsType<IR.UConst>(stmt.Rhs);
        Assert.Equal((ulong)0, c.Value);
    }
}
