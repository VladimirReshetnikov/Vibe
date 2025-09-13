using System;
using System.IO;
using Vibe.Decompiler;
using Vibe.Decompiler.PE;
using Xunit;

namespace Vibe.Tests;

/// <summary>
/// Tests for <see cref="CompilerInfo"/> which analyses PE files to determine
/// the toolchain used to produce them.
/// </summary>
public sealed class CompilerInfoTests
{
    /// <summary>
    /// The analyzer should correctly identify managed assemblies produced by
    /// the .NET toolchain.
    /// </summary>
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
