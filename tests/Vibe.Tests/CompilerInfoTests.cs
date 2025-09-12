using Vibe.Decompiler;
using Xunit;

namespace Vibe.Tests;

public sealed class CompilerInfoTests
{
    [Fact]
    public void DetectsManagedAssembly()
    {
        string path = typeof(CompilerInfoTests).Assembly.Location;
        var info = CompilerInfo.Analyze(path);
        Assert.Equal(".NET", info.Compiler);
        Assert.Contains("Version=v8.0", info.Toolset);
        Assert.Equal("System.Private.CoreLib", info.StandardLibrary);
    }
}
