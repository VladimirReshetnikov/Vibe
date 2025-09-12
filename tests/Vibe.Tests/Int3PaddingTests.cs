using System.Linq;
using System.Text.RegularExpressions;
using Vibe.Decompiler;
using Xunit;

namespace Vibe.Tests;

public class Int3PaddingTests
{
    [Fact]
    public void CollapsesInt3BlocksIntoSingleComment()
    {
        // A block of five INT3 instructions (0xCC) should collapse into one line
        byte[] code = Enumerable.Repeat((byte)0xCC, 5).ToArray();
        var engine = new Engine();
        var opts = new Engine.Options { EmitLabels = false, DetectPrologue = false };
        string text = engine.ToPseudoCode(code, opts);

        Assert.Contains("int3 padding", text);
        Assert.DoesNotContain("no semantic translation", text);
        Assert.Equal(1, Regex.Matches(text, "int3").Count);
    }
}
