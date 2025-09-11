using System;
using Xunit;
using Vibe.Decompiler;

public class TrivialAsmModeTests
{
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
