using System;
namespace Vibe.Utils;

/// <summary>
/// Helper methods for manipulating decompiled code strings.
/// </summary>
public static class CodeUtils
{
    /// <summary>
    /// Prefixes the provided code with a comment indicating whether it
    /// represents a preliminary or final version of the decompilation.
    /// </summary>
    /// <param name="code">The code to annotate.</param>
    /// <param name="isFinal">True if the code is the final decompiled output; otherwise false.</param>
    /// <returns>The annotated code string.</returns>
    public static string PrependVersionComment(string code, bool isFinal) =>
        string.IsNullOrEmpty(code)
            ? code
            : $"// {(isFinal ? "Final" : "Preliminary")} version{Environment.NewLine}{code}";
}
