namespace Vibe.Decompiler.Tests;

internal static class TestHelpers
{
    public static string NormalizeLineEndings(this string text) =>
        text.Replace("\r\n", "\n");
}
