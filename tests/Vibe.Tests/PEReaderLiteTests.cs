using System;
using System.Reflection;
using Vibe.Decompiler;
using Xunit;

namespace Vibe.Tests;

public sealed class PEReaderLiteTests
{
    [Fact]
    public void GetSummaryIncludesMvidForManagedAssemblies()
    {
        string path = typeof(PEReaderLiteTests).Assembly.Location;
        var pe = new PEReaderLite(path);
        Assert.True(pe.HasDotNetMetadata);
        string summary = pe.GetSummary();
        Guid expected = typeof(PEReaderLiteTests).Module.ModuleVersionId;
        Assert.Contains($"MVID: {expected}", summary);
    }
}
