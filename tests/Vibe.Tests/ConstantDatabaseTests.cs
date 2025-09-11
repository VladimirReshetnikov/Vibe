using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FsCheck.Xunit;
using Vibe.Decompiler;
using Xunit;

namespace Vibe.Tests;

[Flags]
public enum TestAccess
{
    Read = 1,
    Write = 2,
    Execute = 4
}

// Enum without [Flags] that still looks like a flags enum.
public enum ImplicitFlags
{
    None = 0,
    A = 1,
    B = 2,
    C = 4
}

// Static class with const fields, treated like an enum by ConstantDatabase.
public static class StatusCodes
{
    public const int Success = 0;
    public const int Failure = 1;
}

/// <summary>
/// Tests functionality of <see cref="ConstantDatabase"/> for mapping and
/// formatting constant values.
/// </summary>
public class ConstantDatabaseTests
{
    /// <summary>
    /// Maps an argument enum value and retrieves its expected enum type.
    /// </summary>
    [Fact]
    public void MapAndLookupArgEnum()
    {
        var db = new ConstantDatabase();
        db.LoadFromAssembly(typeof(TestAccess).Assembly);
        db.MapArgEnum("Foo", 1, typeof(TestAccess).FullName!);

        Assert.True(db.TryGetArgExpectedEnumType("Foo", 1, out var name));
        Assert.Equal(typeof(TestAccess).FullName, name);
    }

    /// <summary>
    /// Formats an unknown constant value as hexadecimal.
    /// </summary>
    [Fact]
    public void UnknownValueFallsBackToHex()
    {
        var db = new ConstantDatabase();
        db.LoadFromAssembly(typeof(TestAccess).Assembly);
        var ok = db.TryFormatValue(typeof(TestAccess).FullName!, 0x80, out var formatted);
        Assert.False(ok);
        Assert.Equal("0x80", formatted);
    }

    /// <summary>
    /// Formats combinations of flags as pipe-separated enum names.
    /// </summary>
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

    /// <summary>
    /// Call target lookups should ignore case and module prefixes when mapping enums.
    /// </summary>
    [Fact]
    public void ArgEnumLookupIsCaseInsensitiveAndHandlesModule()
    {
        var db = new ConstantDatabase();
        db.LoadFromAssembly(typeof(TestAccess).Assembly);
        db.MapArgEnum("FooBar", 2, typeof(TestAccess).FullName!);

        Assert.True(db.TryGetArgExpectedEnumType("foobar", 2, out var name));
        Assert.Equal(typeof(TestAccess).FullName, name);

        Assert.True(db.TryGetArgExpectedEnumType("mod!FOOBAR", 2, out name));
        Assert.Equal(typeof(TestAccess).FullName, name);
    }

    /// <summary>
    /// Mixing known flags with unknown bits should fall back to hexadecimal formatting.
    /// </summary>
    [Fact]
    public void CombinationWithUnknownBitsFallsBackToHex()
    {
        var db = new ConstantDatabase();
        db.LoadFromAssembly(typeof(TestAccess).Assembly);
        ulong value = (ulong)(TestAccess.Read | (TestAccess)0x8);

        var ok = db.TryFormatValue(typeof(TestAccess).FullName!, value, out var formatted);
        Assert.False(ok);
        Assert.Equal("0x9", formatted);
    }

    /// <summary>
    /// Formats combinations of enums that look like flags even without a [Flags] attribute.
    /// </summary>
    [Fact]
    public void FormatsImplicitFlagCombinations()
    {
        var db = new ConstantDatabase();
        db.LoadFromAssembly(typeof(ImplicitFlags).Assembly);
        ulong value = (ulong)(ImplicitFlags.A | ImplicitFlags.C);

        var ok = db.TryFormatValue(typeof(ImplicitFlags).FullName!, value, out var formatted);
        Assert.True(ok);

        var expected = new[]
        {
            $"{typeof(ImplicitFlags).FullName}.A",
            $"{typeof(ImplicitFlags).FullName}.C"
        }.OrderBy(x => x);
        var parts = formatted.Split(" | ", StringSplitOptions.RemoveEmptyEntries).OrderBy(x => x);
        Assert.Equal(expected, parts);
    }

    /// <summary>
    /// Value zero is formatted when the enum defines it explicitly.
    /// </summary>
    [Fact]
    public void FormatsZeroValueWhenDefined()
    {
        var db = new ConstantDatabase();
        db.LoadFromAssembly(typeof(ImplicitFlags).Assembly);

        var ok = db.TryFormatValue(typeof(ImplicitFlags).FullName!, 0, out var formatted);
        Assert.True(ok);
        Assert.Equal($"{typeof(ImplicitFlags).FullName}.None", formatted);
    }

    /// <summary>
    /// Static classes with constants are treated like enums for formatting.
    /// </summary>
    [Fact]
    public void FormatsValuesFromStaticConstantClasses()
    {
        var db = new ConstantDatabase();
        db.LoadFromAssembly(typeof(StatusCodes).Assembly);

        var ok = db.TryFormatValue(typeof(StatusCodes).FullName!, 1, out var formatted);
        Assert.True(ok);
        Assert.Equal($"{typeof(StatusCodes).FullName}.Failure", formatted);
    }

    /// <summary>
    /// Formatting with an unknown enum type returns false and an empty string.
    /// </summary>
    [Fact]
    public void UnknownEnumTypeReturnsFalse()
    {
        var db = new ConstantDatabase();
        var ok = db.TryFormatValue("Does.Not.Exist", 1, out var formatted);
        Assert.False(ok);
        Assert.Equal("", formatted);
    }

    /// <summary>
    /// Loads Windows metadata and looks up well-known WinAPI constants by their hexadecimal values.
    /// If the metadata file is not available, the test exits early.
    /// </summary>
    [Fact]
    public void LooksUpWinApiConstantsByHexValue()
    {
        string? winmd = FindWindowsWin32Metadata();
        if (winmd is null)
            return; // metadata not available; skip test silently

        var db = new ConstantDatabase();
        db.LoadWin32MetadataFromWinmd(winmd);

        var cases = new (string EnumName, ulong Value, string Expected)[]
        {
            (
                "Windows.Win32.System.Memory.PAGE_PROTECTION_FLAGS",
                0x04,
                "Windows.Win32.System.Memory.PAGE_PROTECTION_FLAGS.PAGE_READWRITE"
            ),
            (
                "Windows.Win32.System.Memory.MEMORY_ALLOCATION_TYPE",
                0x1000,
                "Windows.Win32.System.Memory.MEMORY_ALLOCATION_TYPE.MEM_COMMIT"
            ),
            (
                "Windows.Win32.System.Threading.PROCESS_ACCESS_RIGHTS",
                0x001F0FFF,
                "Windows.Win32.System.Threading.PROCESS_ACCESS_RIGHTS.PROCESS_ALL_ACCESS"
            )
        };

        foreach (var (enumName, value, expected) in cases)
        {
            Assert.True(db.TryFormatValue(enumName, value, out var formatted));
            Assert.Equal(expected, formatted);
        }
    }

    private static string? FindWindowsWin32Metadata()
    {
        try
        {
            string? dir = AppContext.BaseDirectory;
            while (!string.IsNullOrEmpty(dir))
            {
                var file = Directory.EnumerateFiles(dir, "Windows.Win32.winmd", SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (file is not null)
                    return file;
                var parent = Path.GetDirectoryName(dir);
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        catch { }

        var roots = new List<string>();
        string? env = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (!string.IsNullOrEmpty(env))
            roots.Add(env);

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile))
            roots.Add(Path.Combine(profile, ".nuget", "packages"));

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(local))
        {
            roots.Add(Path.Combine(local, "NuGet", "Cache"));
            roots.Add(Path.Combine(local, "NuGet", "v3-cache"));
        }

        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
                continue;
            foreach (var pkgDir in Directory.EnumerateDirectories(root, "microsoft.windows.sdk.win32metadata*", SearchOption.TopDirectoryOnly))
            {
                var file = Directory.EnumerateFiles(pkgDir, "Windows.Win32.winmd", SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (file is not null)
                    return file;
            }
        }

        return null;
    }
}
