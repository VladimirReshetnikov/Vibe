using System;
using Xunit;
using Vibe.Decompiler;

/// <summary>
/// Tests the <see cref="Engine"/>'s <c>TrivialAsm</c> option which emits a
/// function body consisting solely of an inline assembly block.
/// </summary>
public class TrivialAsmModeTests
{
    /// <summary>
    /// Asserts that enabling <c>TrivialAsm</c> produces a raw mnemonic dump
    /// wrapped in a C style <c>__asm__</c> block instead of the usual IR based
    /// decompilation output.
    /// </summary>
    [Fact]
    public void EmitsInlineAsmBlockWithMnemonics()
    {
        var engine = new Engine();
        var bytes = new byte[] { 0x55, 0x48, 0x89, 0xE5, 0x5D, 0xC3 };
        var result = engine.ToPseudoCode(bytes, new Engine.Options
        {
            BaseAddress = 0x140000000,
            FunctionName = "test",
            TrivialAsm = true
        });

        Assert.Contains("__asm__", result);
        Assert.Contains("push rbp", result);
        Assert.Contains("mov rbp, rsp", result);
        Assert.Contains("ret", result);
        Assert.DoesNotContain("db", result, StringComparison.OrdinalIgnoreCase);
    }
}
