using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenIssue.Core;

/// <summary>Identifies a Markdown list style used by the editor.</summary>
public enum MarkdownListKind
{
    Bulleted,
    Numbered,
    Checkbox
}

/// <summary>Describes a text replacement and the caret position after it is applied.</summary>
/// <param name="Start">The start index of the replacement.</param>
/// <param name="Length">The number of characters to replace.</param>
/// <param name="Replacement">The text to insert.</param>
/// <param name="CaretOffset">The selection start relative to the replacement start.</param>
/// <param name="SelectionLength">The length of the resulting selection.</param>
public readonly record struct MarkdownTextEdit(
    int Start,
    int Length,
    string Replacement,
    int CaretOffset,
    int SelectionLength = 0);

/// <summary>Creates Markdown-aware text edits for the issue-body editor.</summary>
public static class MarkdownTextEditor
{
    private static readonly Regex ListItemPattern = new(
        @"^(?<indent>[ \t]*)(?<marker>[-+*]|[0-9]{1,9}[.)])(?<spacing>[ \t]+)(?<task>\[(?<check>[ xX])\](?=[ \t]|$)(?<taskSpacing>[ \t]*))?(?<content>.*)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex HeadingPattern = new(
        @"^(?<indent> {0,3})(?<marker>#{1,6})(?:[ \t]+|$)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Creates an edit that wraps the selected text in an inline Markdown element.</summary>
    /// <param name="text">The current Markdown text.</param>
    /// <param name="selectionStart">The selection start.</param>
    /// <param name="selectionLength">The selection length.</param>
    /// <param name="opening">The opening Markdown marker.</param>
    /// <param name="closing">The closing Markdown marker.</param>
    /// <returns>An edit that leaves the selected content selected, or places the caret between empty markers.</returns>
    public static MarkdownTextEdit CreateInlineFormatEdit(
        string text,
        int selectionStart,
        int selectionLength,
        string opening,
        string closing)
    {
        ValidateSelection(text, selectionStart, selectionLength);
        ArgumentNullException.ThrowIfNull(opening);
        ArgumentNullException.ThrowIfNull(closing);

        var selectedText = text.Substring(selectionStart, selectionLength);
        return new MarkdownTextEdit(
            selectionStart,
            selectionLength,
            opening + selectedText + closing,
            opening.Length,
            selectionLength);
    }

    /// <summary>Creates an edit that inserts a Markdown link around the selected text or a new link placeholder.</summary>
    /// <param name="text">The current Markdown text.</param>
    /// <param name="selectionStart">The selection start.</param>
    /// <param name="selectionLength">The selection length.</param>
    /// <returns>An edit with the link label or URL placeholder selected for immediate replacement.</returns>
    public static MarkdownTextEdit CreateLinkEdit(
        string text,
        int selectionStart,
        int selectionLength)
    {
        ValidateSelection(text, selectionStart, selectionLength);

        if (selectionLength == 0)
        {
            const string placeholder = "[text](url)";
            const int labelStart = 1;
            const int labelLength = 4;
            return new MarkdownTextEdit(
                selectionStart,
                0,
                placeholder,
                labelStart,
                labelLength);
        }

        var selectedText = text.Substring(selectionStart, selectionLength);
        var urlStart = selectedText.Length + 3;
        return new MarkdownTextEdit(
            selectionStart,
            selectionLength,
            $"[{selectedText}](url)",
            urlStart,
            3);
    }

    /// <summary>Creates an edit that toggles a level-one Markdown heading on the selected lines.</summary>
    /// <param name="text">The current Markdown text.</param>
    /// <param name="selectionStart">The selection start.</param>
    /// <param name="selectionLength">The selection length.</param>
    /// <returns>An edit that converts selected lines to level-one headings or removes their heading markers.</returns>
    public static MarkdownTextEdit CreateHeadingEdit(
        string text,
        int selectionStart,
        int selectionLength)
    {
        ValidateSelection(text, selectionStart, selectionLength);
        return CreateLineTransformationEdit(
            text,
            selectionStart,
            selectionLength,
            TransformHeadingLine);
    }

    /// <summary>Creates an edit that applies a list style to the selected lines.</summary>
    /// <param name="text">The current Markdown text.</param>
    /// <param name="selectionStart">The selection start.</param>
    /// <param name="selectionLength">The selection length.</param>
    /// <param name="kind">The list style to apply.</param>
    /// <returns>An edit that replaces existing list markers and applies the requested style.</returns>
    public static MarkdownTextEdit CreateListEdit(
        string text,
        int selectionStart,
        int selectionLength,
        MarkdownListKind kind)
    {
        ValidateSelection(text, selectionStart, selectionLength);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        return CreateLineTransformationEdit(
            text,
            selectionStart,
            selectionLength,
            (line, lineNumber) => TransformListLine(line, lineNumber, kind));
    }

    /// <summary>Creates an edit that indents every selected line, or the current line when nothing is selected.</summary>
    /// <param name="text">The current Markdown text.</param>
    /// <param name="selectionStart">The selection start.</param>
    /// <param name="selectionLength">The selection length.</param>
    /// <returns>An edit that inserts one tab at the start of each affected line.</returns>
    public static MarkdownTextEdit CreateIndentEdit(
        string text,
        int selectionStart,
        int selectionLength)
    {
        ValidateSelection(text, selectionStart, selectionLength);
        return CreateLineTransformationEdit(
            text,
            selectionStart,
            selectionLength,
            static (line, _) => "\t" + line);
    }

    /// <summary>Creates an edit that removes one indentation level from selected lines or the current line.</summary>
    /// <param name="text">The current Markdown text.</param>
    /// <param name="selectionStart">The selection start.</param>
    /// <param name="selectionLength">The selection length.</param>
    /// <param name="edit">Receives the edit when at least one line is indented.</param>
    /// <returns><see langword="true"/> when an indentation level was removed.</returns>
    public static bool TryCreateOutdentEdit(
        string text,
        int selectionStart,
        int selectionLength,
        out MarkdownTextEdit edit)
    {
        ValidateSelection(text, selectionStart, selectionLength);
        edit = CreateLineTransformationEdit(
            text,
            selectionStart,
            selectionLength,
            static (line, _) => RemoveIndentation(line));
        if (string.Equals(
                edit.Replacement,
                text.Substring(edit.Start, edit.Length),
                StringComparison.Ordinal))
        {
            edit = default;
            return false;
        }

        return true;
    }

    /// <summary>Creates a line continuation or empty-list exit edit when the caret has no selection.</summary>
    /// <param name="text">The Markdown text being edited.</param>
    /// <param name="caretIndex">The current caret index.</param>
    /// <param name="selectionLength">The current selection length.</param>
    /// <param name="edit">Receives the edit to apply when one is needed.</param>
    /// <returns><see langword="true"/> when Markdown-aware handling should replace the default Enter behavior.</returns>
    public static bool TryCreateEnterEdit(
        string text,
        int caretIndex,
        int selectionLength,
        out MarkdownTextEdit edit)
    {
        ArgumentNullException.ThrowIfNull(text);
        if ((uint)caretIndex > (uint)text.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(caretIndex));
        }

        if (selectionLength < 0 || selectionLength > text.Length - caretIndex)
        {
            throw new ArgumentOutOfRangeException(nameof(selectionLength));
        }

        edit = default;
        if (selectionLength != 0)
        {
            return false;
        }

        var lineStart = FindLineStart(text, caretIndex);
        var lineEnd = FindLineEnd(text, caretIndex);
        var line = text[lineStart..lineEnd];
        var listItem = ListItemPattern.Match(line);
        if (listItem.Success)
        {
            var indent = listItem.Groups["indent"].Value;
            var content = listItem.Groups["content"];
            if (string.IsNullOrWhiteSpace(content.Value) &&
                caretIndex >= lineStart + content.Index)
            {
                edit = new MarkdownTextEdit(lineStart, line.Length, indent, indent.Length);
                return true;
            }

            var marker = GetNextListMarker(listItem.Groups["marker"].Value);
            var task = listItem.Groups["task"];
            var taskSpacing = listItem.Groups["taskSpacing"].Value;
            var checkboxContinuation = task.Success
                ? $"[ ]{(taskSpacing.Length == 0 ? " " : taskSpacing)}"
                : string.Empty;
            var continuation =
                indent +
                marker +
                listItem.Groups["spacing"].Value +
                checkboxContinuation;
            var replacement = GetLineEnding(text) + continuation;
            edit = new MarkdownTextEdit(caretIndex, 0, replacement, replacement.Length);
            return true;
        }

        var indentLength = 0;
        while (indentLength < line.Length && line[indentLength] is ' ' or '\t')
        {
            indentLength++;
        }

        if (indentLength == 0)
        {
            return false;
        }

        var indentation = line[..indentLength];
        var indentedReplacement = GetLineEnding(text) + indentation;
        edit = new MarkdownTextEdit(
            caretIndex,
            0,
            indentedReplacement,
            indentedReplacement.Length);
        return true;
    }

    private static MarkdownTextEdit CreateLineTransformationEdit(
        string text,
        int selectionStart,
        int selectionLength,
        Func<string, int, string> transformLine)
    {
        var (lineStart, lineEnd) = GetAffectedLineRange(text, selectionStart, selectionLength);
        var originalBlock = text[lineStart..lineEnd];
        var replacement = TransformLineBlock(originalBlock, transformLine);
        if (selectionLength > 0)
        {
            return new MarkdownTextEdit(
                lineStart,
                lineEnd - lineStart,
                replacement,
                0,
                replacement.Length);
        }

        var firstLineEnd = FindLineEnd(originalBlock, 0);
        var firstLine = originalBlock[..firstLineEnd];
        var transformedFirstLine = transformLine(firstLine, 0);
        var caretOffset = Math.Clamp(
            selectionStart - lineStart + transformedFirstLine.Length - firstLine.Length,
            0,
            replacement.Length);
        return new MarkdownTextEdit(
            lineStart,
            lineEnd - lineStart,
            replacement,
            caretOffset);
    }

    private static string TransformLineBlock(
        string text,
        Func<string, int, string> transformLine)
    {
        var result = new StringBuilder(text.Length);
        var lineStart = 0;
        var lineNumber = 0;
        while (true)
        {
            var lineEnd = FindLineEnd(text, lineStart);
            result.Append(transformLine(text[lineStart..lineEnd], lineNumber++));
            if (lineEnd >= text.Length)
            {
                break;
            }

            var lineBreakEnd = lineEnd + (text[lineEnd] == '\r' &&
                                          lineEnd + 1 < text.Length &&
                                          text[lineEnd + 1] == '\n'
                ? 2
                : 1);
            result.Append(text.AsSpan(lineEnd, lineBreakEnd - lineEnd));
            lineStart = lineBreakEnd;
        }

        return result.ToString();
    }

    private static string TransformHeadingLine(string line, int _)
    {
        var heading = HeadingPattern.Match(line);
        if (heading.Success)
        {
            if (heading.Groups["marker"].Length > 1)
            {
                return heading.Groups["indent"].Value + "# " + line[heading.Length..];
            }

            var contentStart = heading.Length;
            return heading.Groups["indent"].Value + line[contentStart..];
        }

        var indentLength = CountLeadingSpaces(line, maximumCount: 3);
        return line[..indentLength] + "# " + line[indentLength..];
    }

    private static string TransformListLine(string line, int lineNumber, MarkdownListKind kind)
    {
        var listItem = ListItemPattern.Match(line);
        var indentLength = listItem.Success
            ? listItem.Groups["indent"].Length
            : CountLeadingWhitespace(line);
        var indent = line[..indentLength];
        var content = listItem.Success
            ? listItem.Groups["content"].Value
            : line[indentLength..];
        var marker = kind switch
        {
            MarkdownListKind.Bulleted => "- ",
            MarkdownListKind.Numbered => $"{lineNumber + 1}. ",
            MarkdownListKind.Checkbox => "- [ ] ",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        return indent + marker + content;
    }

    private static string RemoveIndentation(string line)
    {
        if (line.StartsWith('\t'))
        {
            return line[1..];
        }

        var spaces = CountLeadingSpaces(line, maximumCount: 4);
        return line[spaces..];
    }

    private static int CountLeadingWhitespace(string line)
    {
        var count = 0;
        while (count < line.Length && line[count] is ' ' or '\t')
        {
            count++;
        }

        return count;
    }

    private static int CountLeadingSpaces(string line, int maximumCount)
    {
        var count = 0;
        while (count < line.Length && count < maximumCount && line[count] == ' ')
        {
            count++;
        }

        return count;
    }

    private static (int Start, int End) GetAffectedLineRange(
        string text,
        int selectionStart,
        int selectionLength)
    {
        var rangeStart = FindLineStart(text, selectionStart);
        var lastSelectedIndex = selectionLength == 0
            ? selectionStart
            : selectionStart + selectionLength - 1;
        return (rangeStart, FindLineEnd(text, lastSelectedIndex));
    }

    private static void ValidateSelection(string text, int selectionStart, int selectionLength)
    {
        ArgumentNullException.ThrowIfNull(text);
        if ((uint)selectionStart > (uint)text.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(selectionStart));
        }

        if (selectionLength < 0 || selectionLength > text.Length - selectionStart)
        {
            throw new ArgumentOutOfRangeException(nameof(selectionLength));
        }
    }

    private static string GetNextListMarker(string marker)
    {
        if (!char.IsAsciiDigit(marker[0]))
        {
            return marker;
        }

        var number = int.Parse(marker[..^1], NumberStyles.None, CultureInfo.InvariantCulture);
        return (number + 1).ToString(CultureInfo.InvariantCulture) + marker[^1];
    }

    private static string GetLineEnding(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\r')
            {
                return index + 1 < text.Length && text[index + 1] == '\n'
                    ? "\r\n"
                    : "\r";
            }

            if (text[index] == '\n')
            {
                return "\n";
            }
        }

        return Environment.NewLine;
    }

    private static int FindLineStart(string text, int index)
    {
        while (index > 0 && text[index - 1] is not '\r' and not '\n')
        {
            index--;
        }

        return index;
    }

    private static int FindLineEnd(string text, int index)
    {
        while (index < text.Length && text[index] is not '\r' and not '\n')
        {
            index++;
        }

        return index;
    }
}
