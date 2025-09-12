using System;
using System.Reflection;
using Vibe.Decompiler;
using Xunit;

namespace Vibe.Tests;

public sealed class PeImageTests
{
    [Fact]
    public void GetSummaryIncludesMvidForManagedAssemblies()
    {
        string path = typeof(PeImageTests).Assembly.Location;
        var pe = new PeImage(path);
        Assert.True(pe.HasDotNetMetadata);
        string summary = pe.GetSummary();
        Guid expected = typeof(PeImageTests).Module.ModuleVersionId;
        Assert.Contains($"MVID: {expected}", summary);
    }
}
