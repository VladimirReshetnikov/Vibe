using System;
using System.Collections.Generic;
using System.Windows;
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

    private readonly Brush _commentBrush;
    private readonly Brush _stringBrush;
    private readonly Brush _numberBrush;
    private readonly Brush _keywordBrush;
    private readonly Brush _identifierBrush;

    public PseudoCodeColorizer(
        Brush? commentBrush = null,
        Brush? stringBrush = null,
        Brush? numberBrush = null,
        Brush? keywordBrush = null,
        Brush? identifierBrush = null)
    {
        // Pull from theme if not provided
        var res = Application.Current?.Resources;
        _commentBrush    = commentBrush    ?? (Brush?)res?["Brush.Syntax.Comment"]    ?? Brushes.ForestGreen;
        _stringBrush     = stringBrush     ?? (Brush?)res?["Brush.Syntax.String"]     ?? Brushes.SaddleBrown;
        _numberBrush     = numberBrush     ?? (Brush?)res?["Brush.Syntax.Number"]     ?? Brushes.MediumVioletRed;
        _keywordBrush    = keywordBrush    ?? (Brush?)res?["Brush.Syntax.Keyword"]    ?? Brushes.RoyalBlue;
        _identifierBrush = identifierBrush ?? (Brush?)res?["Brush.Syntax.Identifier"] ?? Brushes.Black;
    }

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
                    part => part.TextRunProperties.SetForegroundBrush(_commentBrush));
                _inMultiLineComment = false;
                start = endComment + 2;
            }
            else
            {
                ChangeLinePart(lineOffset, line.EndOffset,
                    part => part.TextRunProperties.SetForegroundBrush(_commentBrush));
                return;
            }
        }

        while (start < text.Length)
        {
            char c = text[start];

            // // and /* */ comments
            if (c == '/' && start + 1 < text.Length)
            {
                if (text[start + 1] == '/')
                {
                    ChangeLinePart(lineOffset + start, line.EndOffset,
                        part => part.TextRunProperties.SetForegroundBrush(_commentBrush));
                    return;
                }
                if (text[start + 1] == '*')
                {
                    int endComment = text.IndexOf("*/", start + 2, StringComparison.Ordinal);
                    if (endComment >= 0)
                    {
                        ChangeLinePart(lineOffset + start, lineOffset + endComment + 2,
                            part => part.TextRunProperties.SetForegroundBrush(_commentBrush));
                        start = endComment + 2;
                        continue;
                    }
                    else
                    {
                        ChangeLinePart(lineOffset + start, line.EndOffset,
                            part => part.TextRunProperties.SetForegroundBrush(_commentBrush));
                        _inMultiLineComment = true;
                        return;
                    }
                }
            }

            // Strings
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
                    part => part.TextRunProperties.SetForegroundBrush(_stringBrush));
                start = i;
                continue;
            }

            // Numbers (simple)
            if (char.IsDigit(c))
            {
                int i = start + 1;
                while (i < text.Length && char.IsDigit(text[i])) i++;
                ChangeLinePart(lineOffset + start, lineOffset + i,
                    part => part.TextRunProperties.SetForegroundBrush(_numberBrush));
                start = i;
                continue;
            }

            // Identifiers + keywords
            if (char.IsLetter(c) || c == '_')
            {
                int i = start + 1;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                string word = text.Substring(start, i - start);
                if (Keywords.Contains(word))
                {
                    ChangeLinePart(lineOffset + start, lineOffset + i,
                        part => part.TextRunProperties.SetForegroundBrush(_keywordBrush));
                }
                else
                {
                    // You can tint identifiers slightly if desired:
                    // part => part.TextRunProperties.SetForegroundBrush(_identifierBrush));
                }
                start = i;
                continue;
            }

            start++;
        }
    }
}
