using System;
using System.Collections.Generic;
using System.Windows.Media;
using System.Runtime.CompilerServices;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace Vibe.Gui;

internal sealed class PseudoCodeColorizer : DocumentColorizingTransformer
{
    private static SolidColorBrush CreateBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static readonly SolidColorBrush KeywordBrush = CreateBrush(0x8F, 0x9E, 0xB2); // CoolGray.Slate
    private static readonly SolidColorBrush PreprocessorBrush = CreateBrush(0x5F, 0x70, 0x86); // CoolGray.Stone
    private static readonly SolidColorBrush StringBrush = CreateBrush(0xEA, 0x8F, 0x7E); // Peach.Cantaloupe
    private static readonly SolidColorBrush NumberBrush = CreateBrush(0xE7, 0xD2, 0x7B); // Yellow.Beeswax
    private static readonly SolidColorBrush CommentBrush = CreateBrush(0x7D, 0x96, 0x57); // Olive.Olive

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        // C keywords
        "auto","break","case","char","const","continue","default","do","double","else","enum","extern","float","for","goto","if","inline","int","long","register","restrict","return","short","signed","sizeof","static","struct","switch","typedef","union","unsigned","void","volatile","while",
        "_Alignas","_Alignof","_Atomic","_BitInt","_Bool","_Complex","_Decimal32","_Decimal64","_Decimal128","_Generic","_Imaginary","_Noreturn","_Static_assert","_Thread_local",

        // C++ keywords
        "alignas","alignof","and","and_eq","asm","bitand","bitor","bool","catch","char8_t","char16_t","char32_t","class","compl","concept","consteval","constexpr","constinit","const_cast","co_await","co_return","co_yield","decltype","delete","dynamic_cast","explicit","export","friend","mutable","namespace","new","noexcept","not","not_eq","nullptr","operator","or","or_eq","private","protected","public","reinterpret_cast","requires","static_assert","static_cast","template","this","thread_local","throw","true","try","typeid","typename","using","virtual","wchar_t","xor","xor_eq",

        // Common aliases and additional keywords
        "false","true"
    };

    private static readonly HashSet<string> PreprocessorKeywords = new(StringComparer.Ordinal)
    {
        "define","undef","include","include_next","if","ifdef","ifndef","elif","else","endif","error","pragma","line","warning","import","region","endregion"
    };

    private sealed class LineState
    {
        public bool InMultiLineComment;
        public bool InString;
        public bool RawString;
        public char StringQuote;
        public string? RawStringDelimiter;
    }

    private bool _inMultiLineComment;
    private bool _inString;
    private bool _rawString;
    private char _stringQuote;
    private string? _rawStringDelimiter;

    private readonly ConditionalWeakTable<DocumentLine, LineState> _lineStates = new();

    private void SetState(LineState state)
    {
        _inMultiLineComment = state.InMultiLineComment;
        _inString = state.InString;
        _rawString = state.RawString;
        _stringQuote = state.StringQuote;
        _rawStringDelimiter = state.RawStringDelimiter;
    }

    private LineState CaptureState() => new()
    {
        InMultiLineComment = _inMultiLineComment,
        InString = _inString,
        RawString = _rawString,
        StringQuote = _stringQuote,
        RawStringDelimiter = _rawStringDelimiter
    };

    private void SaveLineState(DocumentLine line)
    {
        _lineStates.Remove(line);
        _lineStates.Add(line, CaptureState());
    }

    private LineState GetStartState(DocumentLine line)
    {
        var prev = line.PreviousLine;
        if (prev == null)
            return new LineState();

        if (_lineStates.TryGetValue(prev, out var state))
            return state;

        state = GetStartState(prev);
        SetState(state);
        ScanLineState(CurrentContext.Document.GetText(prev));
        var captured = CaptureState();
        _lineStates.Add(prev, captured);
        return captured;
    }

    private void ScanLineState(string text)
    {
        int start = 0;

        if (_inMultiLineComment)
        {
            int endComment = text.IndexOf("*/", start, StringComparison.Ordinal);
            if (endComment >= 0)
            {
                _inMultiLineComment = false;
                start = endComment + 2;
            }
            else
            {
                return;
            }
        }

        if (_inString)
        {
            int endString = ContinueString(text, 0);
            if (endString >= 0)
            {
                start = endString;
            }
            else
            {
                return;
            }
        }

        while (start < text.Length)
        {
            char c = text[start];

            if (c == '/' && start + 1 < text.Length)
            {
                if (text[start + 1] == '/')
                    return;
                if (text[start + 1] == '*')
                {
                    int endComment = text.IndexOf("*/", start + 2, StringComparison.Ordinal);
                    if (endComment >= 0)
                    {
                        start = endComment + 2;
                        continue;
                    }
                    else
                    {
                        _inMultiLineComment = true;
                        return;
                    }
                }
            }

            if (IsStringStart(text, start))
            {
                start = ScanString(text, start, 0, false);
                continue;
            }

            start++;
        }
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        SetState(GetStartState(line));

        string text = CurrentContext.Document.GetText(line);
        int lineOffset = line.Offset;
        int start = 0;

        if (_inMultiLineComment)
        {
            int endComment = text.IndexOf("*/", start, StringComparison.Ordinal);
            if (endComment >= 0)
            {
                ChangeLinePart(lineOffset, lineOffset + endComment + 2,
                    part => part.TextRunProperties.SetForegroundBrush(CommentBrush));
                _inMultiLineComment = false;
                start = endComment + 2;
            }
            else
            {
                ChangeLinePart(lineOffset, line.EndOffset,
                    part => part.TextRunProperties.SetForegroundBrush(CommentBrush));
                SaveLineState(line);
                return;
            }
        }

        if (_inString)
        {
            int endString = ContinueString(text, 0);
            if (endString >= 0)
            {
                ChangeLinePart(lineOffset, lineOffset + endString,
                    part => part.TextRunProperties.SetForegroundBrush(StringBrush));
                start = endString;
            }
            else
            {
                ChangeLinePart(lineOffset, line.EndOffset,
                    part => part.TextRunProperties.SetForegroundBrush(StringBrush));
                SaveLineState(line);
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
                        part => part.TextRunProperties.SetForegroundBrush(PreprocessorBrush));

                    // Highlight included file names surrounded by <> or ""
                    if (string.Equals(directive, "include", StringComparison.Ordinal) || string.Equals(directive, "include_next", StringComparison.Ordinal))
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
                                part => part.TextRunProperties.SetForegroundBrush(StringBrush));
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
                        part => part.TextRunProperties.SetForegroundBrush(CommentBrush));
                    SaveLineState(line);
                    return;
                }
                if (text[start + 1] == '*')
                {
                    int endComment = text.IndexOf("*/", start + 2, StringComparison.Ordinal);
                    if (endComment >= 0)
                    {
                        ChangeLinePart(lineOffset + start, lineOffset + endComment + 2,
                            part => part.TextRunProperties.SetForegroundBrush(CommentBrush));
                        start = endComment + 2;
                        continue;
                    }
                    else
                    {
                    ChangeLinePart(lineOffset + start, line.EndOffset,
                        part => part.TextRunProperties.SetForegroundBrush(CommentBrush));
                    _inMultiLineComment = true;
                    SaveLineState(line);
                    return;
                }
            }
            }

            if (IsStringStart(text, start))
            {
                start = ScanString(text, start, lineOffset, true);
                continue;
            }

            if (char.IsDigit(c))
            {
                int i = start;
                if (c == '0' && i + 1 < text.Length && (text[i + 1] == 'x' || text[i + 1] == 'X'))
                {
                    i += 2;
                    while (i < text.Length && Uri.IsHexDigit(text[i])) i++;
                }
                else if (c == '0' && i + 1 < text.Length && (text[i + 1] == 'b' || text[i + 1] == 'B'))
                {
                    i += 2;
                    while (i < text.Length && (text[i] == '0' || text[i] == '1')) i++;
                }
                else
                {
                    bool hasDot = false;
                    while (i < text.Length && (char.IsDigit(text[i]) || (!hasDot && text[i] == '.')))
                    {
                        if (text[i] == '.') hasDot = true;
                        i++;
                    }

                    if (i < text.Length && (text[i] == 'e' || text[i] == 'E' || text[i] == 'p' || text[i] == 'P'))
                    {
                        i++;
                        if (i < text.Length && (text[i] == '+' || text[i] == '-')) i++;
                        while (i < text.Length && char.IsDigit(text[i])) i++;
                    }
                }
                while (i < text.Length && char.IsLetter(text[i])) i++; // numeric suffixes
                ChangeLinePart(lineOffset + start, lineOffset + i,
                    part => part.TextRunProperties.SetForegroundBrush(NumberBrush));
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
                        part => part.TextRunProperties.SetForegroundBrush(KeywordBrush));
                }
                start = i;
                continue;
            }

            start++;
        }

        SaveLineState(line);
    }

    private bool IsStringStart(string text, int index)
    {
        char c = text[index];
        if (c == '\"' || c == '\'') return true;

        int i = index;
        if (c == 'u')
        {
            i++;
            if (i < text.Length && text[i] == '8') i++;
        }
        else if (c == 'U' || c == 'L')
        {
            i++;
        }
        else if (c != 'R')
        {
            return false;
        }

        if (i < text.Length && text[i] == 'R')
        {
            i++;
        }
        return i < text.Length && (text[i] == '\"' || text[i] == '\'');
    }

    private int ScanString(string text, int start, int lineOffset, bool colorize)
    {
        int i = start;

        // Parse prefixes
        if (text[i] == 'u')
        {
            i++;
            if (i < text.Length && text[i] == '8') i++;
        }
        else if (text[i] == 'U' || text[i] == 'L')
        {
            i++;
        }

        bool raw = false;
        if (i < text.Length && text[i] == 'R')
        {
            raw = true;
            i++;
        }

        if (i >= text.Length) return start + 1;

        char quote = text[i];
        if (quote != '\"' && quote != '\'') return start + 1;

        if (raw && quote == '\"')
        {
            int delimStart = i + 1;
            int parenIndex = text.IndexOf('(', delimStart);
            string delimiter = parenIndex >= 0 ? text.Substring(delimStart, parenIndex - delimStart) : string.Empty;
            string terminator = ")" + delimiter + "\"";
            int end = parenIndex >= 0 ? text.IndexOf(terminator, parenIndex + 1, StringComparison.Ordinal) : -1;

            if (end >= 0)
            {
                end += terminator.Length;
                if (colorize)
                    ChangeLinePart(lineOffset + start, lineOffset + end,
                        part => part.TextRunProperties.SetForegroundBrush(StringBrush));
                return end;
            }
            else
            {
                if (colorize)
                    ChangeLinePart(lineOffset + start, lineOffset + text.Length,
                        part => part.TextRunProperties.SetForegroundBrush(StringBrush));
                _inString = true;
                _rawString = true;
                _rawStringDelimiter = terminator;
                return text.Length;
            }
        }
        else
        {
            i++;
            int end = i;
            while (end < text.Length)
            {
                if (text[end] == '\\' && end + 1 < text.Length) end += 2;
                else if (text[end] == quote)
                {
                    end++;
                    if (colorize)
                        ChangeLinePart(lineOffset + start, lineOffset + end,
                            part => part.TextRunProperties.SetForegroundBrush(StringBrush));
                    return end;
                }
                else end++;
            }

            if (colorize)
                ChangeLinePart(lineOffset + start, lineOffset + text.Length,
                    part => part.TextRunProperties.SetForegroundBrush(StringBrush));
            _inString = true;
            _rawString = false;
            _stringQuote = quote;
            return text.Length;
        }
    }

    private int ContinueString(string text, int start)
    {
        if (_rawString && _rawStringDelimiter != null)
        {
            int end = text.IndexOf(_rawStringDelimiter, start, StringComparison.Ordinal);
            if (end >= 0)
            {
                _inString = false;
                return end + _rawStringDelimiter.Length;
            }
            return -1;
        }

        int i = start;
        while (i < text.Length)
        {
            if (text[i] == '\\' && i + 1 < text.Length) i += 2;
            else if (text[i] == _stringQuote)
            {
                _inString = false;
                return i + 1;
            }
            else i++;
        }
        return -1;
    }
}

