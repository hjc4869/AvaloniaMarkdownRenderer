namespace AvaloniaMarkdown.Parsing;

/// <summary>Detects bare URLs (GFM "extended www/url autolinks") inside plain text.</summary>
internal static class AutoLinkScanner
{
    private static readonly string[] Prefixes = { "https://", "http://", "ftp://", "www." };

    public static bool TryScan(ReadOnlySpan<char> text, int index, out int length, out string? url)
    {
        length = 0;
        url = null;

        ReadOnlySpan<char> rest = text[index..];
        string? matched = null;
        foreach (string prefix in Prefixes)
        {
            if (rest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                matched = prefix;
                break;
            }
        }

        if (matched is null)
        {
            return false;
        }

        int i = matched.Length;
        if (i >= rest.Length)
        {
            return false;
        }

        int depth = 0;
        while (i < rest.Length)
        {
            char c = rest[i];
            if (char.IsWhiteSpace(c) || c == '<' || c == '>' || c == '"' || c == '`')
            {
                break;
            }

            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                if (depth == 0)
                {
                    break;
                }

                depth--;
            }

            i++;
        }

        // Trailing punctuation is almost always sentence punctuation rather than part of the URL.
        while (i > matched.Length && IsTrailingPunctuation(rest[i - 1]))
        {
            i--;
        }

        if (i <= matched.Length)
        {
            return false;
        }

        ReadOnlySpan<char> candidate = rest[..i];
        if (candidate.IndexOf('.') < 0)
        {
            return false;
        }

        length = i;
        url = matched == "www."
            ? string.Concat("https://", candidate)
            : candidate.ToString();

        return true;
    }

    private static bool IsTrailingPunctuation(char c) =>
        c is '.' or ',' or ':' or ';' or '!' or '?' or '\'' or '*' or '_' or '~' or ']' or ')';
}
