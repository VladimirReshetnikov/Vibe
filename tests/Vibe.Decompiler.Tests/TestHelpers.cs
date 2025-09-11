namespace Vibe.Decompiler.Tests;

/// <summary>
/// Provides helper extensions used by test code.
/// </summary>
internal static class TestHelpers
{
    /// <summary>
    /// Replaces Windows line endings with Unix line endings to ensure
    /// cross-platform string comparison in tests.
    /// </summary>
    public static string NormalizeLineEndings(this string text) =>
        text.Replace("\r\n", "\n");
}
