namespace AvaloniaMarkdown.Parsing;

/// <summary>
/// Central allow-list for URLs that reach the visual tree.
/// </summary>
/// <remarks>
/// Markdown is untrusted input in a chat application. Only schemes that cannot execute code are
/// accepted; <c>javascript:</c>, <c>vbscript:</c> and similar are rejected so a crafted document
/// cannot turn a link click into script execution or a shell invocation.
/// </remarks>
public static class UriSafety
{
    private static readonly string[] AllowedSchemes =
    {
        "http", "https", "mailto", "tel", "ftp", "ftps", "file", "data", "irc", "ircs", "news",
        "nntp", "sftp", "ssh", "xmpp", "matrix", "sms", "geo", "magnet",
    };

    private static readonly string[] AllowedImageSchemes = { "http", "https", "file", "data", "avares" };

    public static bool IsAllowedScheme(ReadOnlySpan<char> scheme)
    {
        foreach (string allowed in AllowedSchemes)
        {
            if (scheme.Equals(allowed, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return scheme.Equals("avares", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when the URL is acceptable as an image source.</summary>
    public static bool IsAllowedImageUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        int colon = url.IndexOf(':');
        if (colon <= 0)
        {
            // Relative path: allowed, resolved against the configured base address.
            return !url.StartsWith("//", StringComparison.Ordinal);
        }

        ReadOnlySpan<char> scheme = url.AsSpan(0, colon);
        foreach (string allowed in AllowedImageSchemes)
        {
            if (scheme.Equals(allowed, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Normalises a raw destination, unescaping backslash escapes and rejecting unsafe schemes.
    /// Returns <c>null</c> when the destination must not be used.
    /// </summary>
    public static string? Sanitize(ReadOnlySpan<char> raw)
    {
        Span<char> buffer = raw.Length <= 512 ? stackalloc char[raw.Length] : new char[raw.Length];
        int length = 0;

        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (c == '\\' && i + 1 < raw.Length)
            {
                buffer[length++] = raw[++i];
                continue;
            }

            if (char.IsControl(c))
            {
                continue;
            }

            buffer[length++] = c;
        }

        ReadOnlySpan<char> cleaned = buffer[..length].Trim();
        if (cleaned.IsEmpty)
        {
            return string.Empty;
        }

        int colon = cleaned.IndexOf(':');
        int slash = cleaned.IndexOf('/');
        int hash = cleaned.IndexOf('#');
        int question = cleaned.IndexOf('?');

        bool hasScheme = colon > 0 &&
                         (slash < 0 || colon < slash) &&
                         (hash < 0 || colon < hash) &&
                         (question < 0 || colon < question);

        if (hasScheme && !IsAllowedScheme(cleaned[..colon]))
        {
            return null;
        }

        return cleaned.ToString();
    }
}
