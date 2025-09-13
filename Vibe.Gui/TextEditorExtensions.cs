using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Rendering;

namespace Vibe.Gui;

/// <summary>
/// Modes of syntax highlighting supported by the application.
/// </summary>
internal enum SyntaxHighlightingMode
{
    /// <summary>No syntax highlighting.</summary>
    Plain,
    /// <summary>Highlighting for C or C++ code.</summary>
    Cpp,
    /// <summary>Highlighting for C# code.</summary>
    CSharp,
}

/// <summary>
/// Extension helpers for configuring <see cref="TextEditor"/> instances.
/// </summary>
internal static class TextEditorExtensions
{
    private static readonly IHighlightingDefinition? CSharpDefinition =
        HighlightingManager.Instance.GetDefinition("C#");

    /// <summary>
    /// Applies the requested syntax highlighting mode to the editor, removing any
    /// previously applied <see cref="PseudoCodeColorizer"/>.
    /// </summary>
    public static void SetSyntaxHighlighting(this TextEditor editor, SyntaxHighlightingMode mode)
    {
        var transformers = editor.TextArea.TextView.LineTransformers;
        for (int i = transformers.Count - 1; i >= 0; i--)
        {
            if (transformers[i] is PseudoCodeColorizer)
                transformers.RemoveAt(i);
        }

        editor.SyntaxHighlighting = null;

        switch (mode)
        {
            case SyntaxHighlightingMode.Cpp:
                transformers.Add(new PseudoCodeColorizer());
                break;
            case SyntaxHighlightingMode.CSharp:
                if (CSharpDefinition != null)
                    editor.SyntaxHighlighting = CSharpDefinition;
                break;
            case SyntaxHighlightingMode.Plain:
            default:
                break;
        }
    }
}

