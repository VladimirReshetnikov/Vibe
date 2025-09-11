using System;
using System.Collections.Generic;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace Vibe.Gui;

internal sealed class PseudoCodeColorizer : DocumentColorizingTransformer
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        // C keywords
        "if","else","while","for","return","break","continue","int","float","double","char","void",
        "auto","extern","register","restrict","volatile","unsigned","signed","long","short","sizeof",
        "goto","switch","case","default","do","enum","static","const","struct","union","typedef",
        "inline","_Alignas","_Alignof","_Atomic","_Bool","_Complex","_Generic","_Imaginary","_Noreturn",
        "_Static_assert","_Thread_local","bool","true","false","null","class","public","private","protected"
    };

    private static readonly HashSet<string> PreprocessorKeywords = new(StringComparer.Ordinal)
    {
        "define","undef","include","if","ifdef","ifndef","else","elif","endif","error","pragma","line"
    };

    private bool _inMultiLineComment;

    protected override void ColorizeLine(DocumentLine line)
    {
        string text = CurrentContext.Document.GetText(line);
        int lineOffset = line.Offset;
        int start = 0;

        if (_inMultiLineComment)
        {
            int endComment = text.IndexOf("*/", start, StringComparison.Ordinal);
            if (endComment >= 0)
            {
                ChangeLinePart(lineOffset, lineOffset + endComment + 2,
                    part => part.TextRunProperties.SetForegroundBrush(Brushes.Green));
                _inMultiLineComment = false;
                start = endComment + 2;
            }
            else
            {
                ChangeLinePart(lineOffset, line.EndOffset,
                    part => part.TextRunProperties.SetForegroundBrush(Brushes.Green));
                return;
            }
        }

        while (start < text.Length)
        {
            char c = text[start];

            // Preprocessor directives like #include and #define
            if (c == '#' && (start == 0 || char.IsWhiteSpace(text[start - 1])))
            {
                int i = start + 1;
                while (i < text.Length && char.IsLetter(text[i])) i++;
                string directive = text.Substring(start + 1, i - start - 1);
                if (PreprocessorKeywords.Contains(directive))
                {
                    ChangeLinePart(lineOffset + start, lineOffset + i,
                        part => part.TextRunProperties.SetForegroundBrush(Brushes.DarkCyan));

                    // Highlight included file names surrounded by <> or ""
                    if (string.Equals(directive, "include", StringComparison.Ordinal))
                    {
                        int j = i;
                        while (j < text.Length && char.IsWhiteSpace(text[j])) j++;
                        if (j < text.Length && (text[j] == '<' || text[j] == '"'))
                        {
                            char endChar = text[j] == '<' ? '>' : '"';
                            int k = j + 1;
                            while (k < text.Length && text[k] != endChar) k++;
                            if (k < text.Length) k++;
                            ChangeLinePart(lineOffset + j, lineOffset + k,
                                part => part.TextRunProperties.SetForegroundBrush(Brushes.Brown));
                            start = k;
                            continue;
                        }
                    }

                    start = i;
                    continue;
                }
            }
            if (c == '/' && start + 1 < text.Length)
            {
                if (text[start + 1] == '/')
                {
                    ChangeLinePart(lineOffset + start, line.EndOffset,
                        part => part.TextRunProperties.SetForegroundBrush(Brushes.Green));
                    return;
                }
                if (text[start + 1] == '*')
                {
                    int endComment = text.IndexOf("*/", start + 2, StringComparison.Ordinal);
                    if (endComment >= 0)
                    {
                        ChangeLinePart(lineOffset + start, lineOffset + endComment + 2,
                            part => part.TextRunProperties.SetForegroundBrush(Brushes.Green));
                        start = endComment + 2;
                        continue;
                    }
                    else
                    {
                        ChangeLinePart(lineOffset + start, line.EndOffset,
                            part => part.TextRunProperties.SetForegroundBrush(Brushes.Green));
                        _inMultiLineComment = true;
                        return;
                    }
                }
            }

            if (c == '"')
            {
                int i = start + 1;
                while (i < text.Length)
                {
                if (text[i] == '\\' && i + 1 < text.Length) i += 2;
                    else if (text[i] == '"') { i++; break; }
                    else i++;
                }
                ChangeLinePart(lineOffset + start, lineOffset + i,
                    part => part.TextRunProperties.SetForegroundBrush(Brushes.Brown));
                start = i;
                continue;
            }

            if (c == '\'')
            {
                int i = start + 1;
                while (i < text.Length)
                {
                    if (text[i] == '\\' && i + 1 < text.Length) i += 2;
                    else if (text[i] == '\'') { i++; break; }
                    else i++;
                }
                ChangeLinePart(lineOffset + start, lineOffset + i,
                    part => part.TextRunProperties.SetForegroundBrush(Brushes.Brown));
                start = i;
                continue;
            }

            if (char.IsDigit(c))
            {
                int i = start + 1;
                if (c == '0' && i < text.Length && (text[i] == 'x' || text[i] == 'X'))
                {
                    i++;
                    while (i < text.Length && Uri.IsHexDigit(text[i])) i++;
                }
                else
                {
                    bool hasDot = false;
                    while (i < text.Length && (char.IsDigit(text[i]) || (!hasDot && text[i] == '.')))
                    {
                        if (text[i] == '.') hasDot = true;
                        i++;
                    }
                }
                while (i < text.Length && char.IsLetter(text[i])) i++; // numeric suffixes
                ChangeLinePart(lineOffset + start, lineOffset + i,
                    part => part.TextRunProperties.SetForegroundBrush(Brushes.Magenta));
                start = i;
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                int i = start + 1;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                string word = text.Substring(start, i - start);
                if (Keywords.Contains(word))
                {
                    ChangeLinePart(lineOffset + start, lineOffset + i,
                        part => part.TextRunProperties.SetForegroundBrush(Brushes.Blue));
                }
                start = i;
                continue;
            }

            start++;
        }
    }
}
