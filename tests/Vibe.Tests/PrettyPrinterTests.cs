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

    /// <summary>
    /// Prints basic block labels when the corresponding option is enabled.
    /// </summary>
    [Fact]
    public void PrintsBlockLabelsWhenEnabled()
    {
        var fn = new IR.FunctionIR("branch") { ReturnType = Void };
        var target = Lbl(1);
        var bb0 = new IR.BasicBlock(Lbl(0));
        bb0.Statements.Add(new IR.GotoStmt(target));
        var bb1 = new IR.BasicBlock(target);
        bb1.Statements.Add(new IR.ReturnStmt(null));
        fn.Blocks.Add(bb0);
        fn.Blocks.Add(bb1);

        var pp = new IR.PrettyPrinter(new IR.PrettyPrinter.Options
        {
            EmitHeaderComment = false,
            EmitBlockLabels = true
        });
        var text = pp.Print(fn).NormalizeLineEndings();

        Assert.Contains("L0:\n", text);
        Assert.Contains("L1:\n", text);
    }

    /// <summary>
    /// Emits signedness comments for comparison operations by default.
    /// </summary>
    [Fact]
    public void CommentsSignednessOnComparisons()
    {
        var fn = new IR.FunctionIR("cmp") { ReturnType = U32 };
        fn.Parameters.Add(new IR.Parameter("a", I32, 0));
        fn.Parameters.Add(new IR.Parameter("b", I32, 1));
        var bb = new IR.BasicBlock(Lbl(0));
        bb.Statements.Add(new IR.ReturnStmt(SLt(P("a", 0), P("b", 1))));
        fn.Blocks.Add(bb);

        var pp = new IR.PrettyPrinter(new IR.PrettyPrinter.Options { EmitHeaderComment = false });
        var text = pp.Print(fn);

        Assert.Contains("/* signed */ a < b", text);
    }

    /// <summary>
    /// Uses native C integer type names when stdint names are disabled.
    /// </summary>
    [Fact]
    public void UsesNativeIntNamesWhenStdIntDisabled()
    {
        var fn = new IR.FunctionIR("id") { ReturnType = U8 };
        fn.Parameters.Add(new IR.Parameter("a", U8, 0));
        var bb = new IR.BasicBlock(Lbl(0));
        bb.Statements.Add(new IR.ReturnStmt(P("a", 0)));
        fn.Blocks.Add(bb);

        var pp = new IR.PrettyPrinter(new IR.PrettyPrinter.Options
        {
            EmitHeaderComment = false,
            UseStdIntNames = false
        });
        var text = pp.Print(fn);

        Assert.Contains("unsigned char id(unsigned char a)", text);
        Assert.DoesNotContain("uint8_t", text);
    }
}
