using Vibe.Decompiler;
using Xunit;
using static Vibe.Decompiler.IR.X;

namespace Vibe.Tests;

/// <summary>
/// Tests the <see cref="IR.PrettyPrinter"/> for generating readable IR output.
/// </summary>
public class PrettyPrinterTests
{
    /// <summary>
    /// Pretty-prints a simple function containing a local variable and return statement.
    /// </summary>
    [Fact]
    public void PrintsSimpleFunction()
    {
        var fn = new IR.FunctionIR("add") { ReturnType = U32 };
        fn.Parameters.Add(new IR.Parameter("a", U32, 0));
        var tmp = new IR.LocalVar("tmp", U32);
        fn.Locals.Add(tmp);
        var bb = new IR.BasicBlock(Lbl(0));
        bb.Statements.Add(new IR.AssignStmt(L(tmp.Name), Add(P("a", 0), C(1, 32))));
        bb.Statements.Add(new IR.ReturnStmt(L(tmp.Name)));
        fn.Blocks.Add(bb);

        var pp = new IR.PrettyPrinter(new IR.PrettyPrinter.Options { EmitHeaderComment = false });
        var text = pp.Print(fn).NormalizeLineEndings();

        var expected = @"uint32_t add(uint32_t a) {

    uint32_t tmp;

    tmp = a + 1;
    return tmp;
}
".NormalizeLineEndings();

        Assert.Equal(expected, text);
    }

    /// <summary>
    /// Ensures binary expressions are parenthesized according to precedence rules.
    /// </summary>
    [Fact]
    public void RespectsExpressionPrecedence()
    {
        var fn = new IR.FunctionIR("calc") { ReturnType = U32 };
        var bb = new IR.BasicBlock(Lbl(0));
        bb.Statements.Add(new IR.ReturnStmt(Mul(Add(C(1, 32), C(2, 32)), C(3, 32))));
        fn.Blocks.Add(bb);

        var pp = new IR.PrettyPrinter(new IR.PrettyPrinter.Options { EmitHeaderComment = false });
        var text = pp.Print(fn);

        Assert.Contains("return (1 + 2) * 3;", text);
    }

    /// <summary>
    /// Verifies that the pretty printer emits a header comment by default.
    /// </summary>
    [Fact]
    public void EmitsHeaderCommentByDefault()
    {
        var fn = new IR.FunctionIR("noop") { ReturnType = Void };
        var bb = new IR.BasicBlock(Lbl(0));
        bb.Statements.Add(new IR.ReturnStmt(null));
        fn.Blocks.Add(bb);

        var pp = new IR.PrettyPrinter();
        var text = pp.Print(fn);

        Assert.Contains("C-like pseudocode", text);
    }
}
