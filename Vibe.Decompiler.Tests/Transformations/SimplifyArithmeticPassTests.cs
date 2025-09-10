using Vibe.Decompiler.Transformations;
using Xunit;

namespace Vibe.Decompiler.Tests.Transformations;

public class SimplifyArithmeticPassTests
{
    [Fact]
    public void SimplifiesAdditionWithZero()
    {
        var fn = new IR.FunctionIR("test");
        var bb = new IR.BasicBlock(new IR.LabelSymbol("L0", 0));
        bb.Statements.Add(new IR.AssignStmt(new IR.RegExpr("rdx"),
            new IR.BinOpExpr(IR.BinOp.Add, new IR.RegExpr("rax"), new IR.Const(0, 64))));
        fn.Blocks.Add(bb);

        var pass = new SimplifyArithmeticPass();
        pass.Run(fn);

        var stmt = Assert.IsType<IR.AssignStmt>(fn.Blocks[0].Statements[0]);
        var rhs = Assert.IsType<IR.RegExpr>(stmt.Rhs);
        Assert.Equal("rax", rhs.Name);
    }

    [Fact]
    public void SimplifiesMultiplicationByOne()
    {
        var fn = new IR.FunctionIR("test");
        var bb = new IR.BasicBlock(new IR.LabelSymbol("L0", 0));
        bb.Statements.Add(new IR.AssignStmt(new IR.RegExpr("rdx"),
            new IR.BinOpExpr(IR.BinOp.Mul, new IR.RegExpr("rax"), new IR.Const(1, 64))));
        fn.Blocks.Add(bb);

        var pass = new SimplifyArithmeticPass();
        pass.Run(fn);

        var stmt = Assert.IsType<IR.AssignStmt>(fn.Blocks[0].Statements[0]);
        var rhs = Assert.IsType<IR.RegExpr>(stmt.Rhs);
        Assert.Equal("rax", rhs.Name);
    }

    [Fact]
    public void SimplifiesXorWithItselfToZero()
    {
        var fn = new IR.FunctionIR("test");
        var bb = new IR.BasicBlock(new IR.LabelSymbol("L0", 0));
        var reg = new IR.RegExpr("rax");
        bb.Statements.Add(new IR.AssignStmt(new IR.RegExpr("rdx"),
            new IR.BinOpExpr(IR.BinOp.Xor, reg, reg)));
        fn.Blocks.Add(bb);

        var pass = new SimplifyArithmeticPass();
        pass.Run(fn);

        var stmt = Assert.IsType<IR.AssignStmt>(fn.Blocks[0].Statements[0]);
        var zero = Assert.IsType<IR.Const>(stmt.Rhs);
        Assert.Equal(0, zero.Value);
    }

    [Fact]
    public void SimplifiesAndWithItself()
    {
        var fn = new IR.FunctionIR("test");
        var bb = new IR.BasicBlock(new IR.LabelSymbol("L0", 0));
        var reg = new IR.RegExpr("rax");
        bb.Statements.Add(new IR.AssignStmt(new IR.RegExpr("rdx"),
            new IR.BinOpExpr(IR.BinOp.And, reg, reg)));
        fn.Blocks.Add(bb);

        var pass = new SimplifyArithmeticPass();
        pass.Run(fn);

        var stmt = Assert.IsType<IR.AssignStmt>(fn.Blocks[0].Statements[0]);
        var rhs = Assert.IsType<IR.RegExpr>(stmt.Rhs);
        Assert.Equal("rax", rhs.Name);
    }

    [Fact]
    public void SimplifiesOrWithItself()
    {
        var fn = new IR.FunctionIR("test");
        var bb = new IR.BasicBlock(new IR.LabelSymbol("L0", 0));
        var reg = new IR.RegExpr("rax");
        bb.Statements.Add(new IR.AssignStmt(new IR.RegExpr("rdx"),
            new IR.BinOpExpr(IR.BinOp.Or, reg, reg)));
        fn.Blocks.Add(bb);

        var pass = new SimplifyArithmeticPass();
        pass.Run(fn);

        var stmt = Assert.IsType<IR.AssignStmt>(fn.Blocks[0].Statements[0]);
        var rhs = Assert.IsType<IR.RegExpr>(stmt.Rhs);
        Assert.Equal("rax", rhs.Name);
    }

    [Fact]
    public void SimplifiesOrWithAllOnes()
    {
        var fn = new IR.FunctionIR("test");
        var bb = new IR.BasicBlock(new IR.LabelSymbol("L0", 0));
        bb.Statements.Add(new IR.AssignStmt(new IR.RegExpr("rdx"),
            new IR.BinOpExpr(IR.BinOp.Or, new IR.RegExpr("rax"), new IR.Const(-1, 64))));
        fn.Blocks.Add(bb);

        var pass = new SimplifyArithmeticPass();
        pass.Run(fn);

        var stmt = Assert.IsType<IR.AssignStmt>(fn.Blocks[0].Statements[0]);
        var allOnes = Assert.IsType<IR.Const>(stmt.Rhs);
        Assert.Equal(-1, allOnes.Value);
    }
}
