using System;
using System.Reflection;
using Vibe.Decompiler;
using Vibe.Decompiler.PE;
using Xunit;

namespace Vibe.Tests;

/// <summary>
/// Tests for <see cref="PeImage"/> verifying that it can summarise managed
/// assemblies and expose the module version identifier (MVID).
/// </summary>
public sealed class PeImageTests
{
    /// <summary>
    /// A managed assembly should report its MVID within the textual summary.
    /// </summary>
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
