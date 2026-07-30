namespace AvaloniaMarkdown.Parsing;

/// <summary>
/// Recognises the small, safe subset of raw HTML that the renderer understands.
/// </summary>
/// <remarks>
/// Anything outside the allow list degrades gracefully: block-level HTML is shown as
/// preformatted text and unknown inline tags are emitted verbatim as literal text, so a
/// malformed or hostile document can never inject behaviour into the visual tree.
/// </remarks>
internal static class HtmlBlockScanner
{
    private static readonly string[] BlockTags =
    {
        "address", "article", "aside", "blockquote", "details", "dialog", "dd", "div", "dl", "dt",
        "fieldset", "figcaption", "figure", "footer", "form", "h1", "h2", "h3", "h4", "h5", "h6",
        "header", "hr", "li", "main", "nav", "ol", "p", "pre", "section", "summary", "table",
        "tbody", "td", "tfoot", "th", "thead", "tr", "ul",
    };

    /// <summary>Inline tags that are translated into inline styling instead of being shown literally.</summary>
    private static readonly string[] InlineTags =
    {
        "a", "b", "br", "code", "del", "em", "i", "ins", "kbd", "mark", "s", "strong", "sub",
        "sup", "u", "span",
    };

    public static bool IsBlockStart(ReadOnlySpan<char> line)
    {
        if (line.Length < 2 || line[0] != '<')
        {
            return false;
        }

        if (line.StartsWith("<!--", StringComparison.Ordinal) ||
            line.StartsWith("<?", StringComparison.Ordinal) ||
            line.StartsWith("<!", StringComparison.Ordinal))
        {
            return true;
        }

        int i = 1;
        if (line[i] == '/')
        {
            i++;
        }

        int nameStart = i;
        while (i < line.Length && (char.IsAsciiLetterOrDigit(line[i]) || line[i] == '-'))
        {
            i++;
        }

        if (i == nameStart)
        {
            return false;
        }

        if (i < line.Length && line[i] != '>' && line[i] != ' ' && line[i] != '\t' && line[i] != '/')
        {
            return false;
        }

        return IsKnownTag(BlockTags, line[nameStart..i]);
    }

    public static bool IsSafeInlineTag(ReadOnlySpan<char> name) => IsKnownTag(InlineTags, name);

    private static bool IsKnownTag(string[] tags, ReadOnlySpan<char> name)
    {
        foreach (string tag in tags)
        {
            if (name.Equals(tag, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
