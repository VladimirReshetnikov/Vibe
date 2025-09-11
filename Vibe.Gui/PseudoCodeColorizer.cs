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
        "if","else","while","for","return","break","continue","int","float","double","char","void",
        "class","struct","public","private","protected","switch","case","default","do","enum",
        "static","const","unsigned","signed","long","short","bool","true","false","null"
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
                    if (text[i] == '\\') i += 2;
                    else if (text[i] == '"') { i++; break; }
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
                while (i < text.Length && char.IsDigit(text[i])) i++;
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
