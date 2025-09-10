using System;
using System.Linq;
using FsCheck.Xunit;
using Vibe.Decompiler;
using Xunit;

namespace Vibe.Decompiler.Tests;

[Flags]
public enum TestAccess
{
    Read = 1,
    Write = 2,
    Execute = 4
}

public class ConstantDatabaseTests
{
    [Fact]
    public void MapAndLookupArgEnum()
    {
        var db = new ConstantDatabase();
        db.LoadFromAssembly(typeof(TestAccess).Assembly);
        db.MapArgEnum("Foo", 1, typeof(TestAccess).FullName!);

        Assert.True(db.TryGetArgExpectedEnumType("Foo", 1, out var name));
        Assert.Equal(typeof(TestAccess).FullName, name);
    }

    [Fact]
    public void UnknownValueFallsBackToHex()
    {
        var db = new ConstantDatabase();
        db.LoadFromAssembly(typeof(TestAccess).Assembly);
        var ok = db.TryFormatValue(typeof(TestAccess).FullName!, 0x80, out var formatted);
        Assert.False(ok);
        Assert.Equal("0x80", formatted);
    }

    [Property]
    public void FormatsFlagCombinationsCorrectly(TestAccess[] flags)
    {
        var distinct = flags
            .Where(f => f != 0 && ((ulong)f & ((ulong)f - 1)) == 0)
            .Distinct()
            .ToArray();
        if (distinct.Length == 0)
            return;

        var db = new ConstantDatabase();
        db.LoadFromAssembly(typeof(TestAccess).Assembly);
        ulong value = distinct.Aggregate(0UL, (acc, f) => acc | (ulong)f);

        var success = db.TryFormatValue(typeof(TestAccess).FullName!, value, out var formatted);
        Assert.True(success);

        var expected = distinct.Select(f => $"{typeof(TestAccess).FullName}.{f}").OrderBy(x => x);
        var parts = formatted.Split(" | ", StringSplitOptions.RemoveEmptyEntries).OrderBy(x => x);

        Assert.Equal(expected, parts);
    }
}
