using System.Globalization;

namespace AvaloniaMarkdown.Parsing;

/// <summary>Decodes the HTML entities that appear in practice, plus all numeric references.</summary>
internal static class HtmlEntities
{
    private static readonly Dictionary<string, string> Named = new(StringComparer.Ordinal)
    {
        ["amp"] = "&",
        ["lt"] = "<",
        ["gt"] = ">",
        ["quot"] = "\"",
        ["apos"] = "'",
        ["nbsp"] = "\u00a0",
        ["copy"] = "\u00a9",
        ["reg"] = "\u00ae",
        ["trade"] = "\u2122",
        ["hellip"] = "\u2026",
        ["mdash"] = "\u2014",
        ["ndash"] = "\u2013",
        ["lsquo"] = "\u2018",
        ["rsquo"] = "\u2019",
        ["ldquo"] = "\u201c",
        ["rdquo"] = "\u201d",
        ["bull"] = "\u2022",
        ["middot"] = "\u00b7",
        ["deg"] = "\u00b0",
        ["plusmn"] = "\u00b1",
        ["times"] = "\u00d7",
        ["divide"] = "\u00f7",
        ["laquo"] = "\u00ab",
        ["raquo"] = "\u00bb",
        ["euro"] = "\u20ac",
        ["pound"] = "\u00a3",
        ["yen"] = "\u00a5",
        ["cent"] = "\u00a2",
        ["sect"] = "\u00a7",
        ["para"] = "\u00b6",
        ["dagger"] = "\u2020",
        ["larr"] = "\u2190",
        ["rarr"] = "\u2192",
        ["uarr"] = "\u2191",
        ["darr"] = "\u2193",
        ["harr"] = "\u2194",
        ["check"] = "\u2713",
    };

    /// <summary>
    /// Attempts to decode an entity starting at the <c>&amp;</c> in <paramref name="text"/>.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<char> text, out string? decoded, out int consumed)
    {
        decoded = null;
        consumed = 0;

        if (text.Length < 3 || text[0] != '&')
        {
            return false;
        }

        int semicolon = text[..Math.Min(text.Length, 34)].IndexOf(';');
        if (semicolon <= 1)
        {
            return false;
        }

        ReadOnlySpan<char> body = text[1..semicolon];

        if (body[0] == '#')
        {
            if (body.Length < 2)
            {
                return false;
            }

            bool hex = body[1] is 'x' or 'X';
            ReadOnlySpan<char> digits = hex ? body[2..] : body[1..];
            if (digits.IsEmpty)
            {
                return false;
            }

            if (!int.TryParse(digits, hex ? NumberStyles.HexNumber : NumberStyles.Integer, CultureInfo.InvariantCulture, out int codePoint))
            {
                return false;
            }

            if (codePoint <= 0 || codePoint > 0x10FFFF || (codePoint >= 0xD800 && codePoint <= 0xDFFF))
            {
                decoded = "\ufffd";
            }
            else
            {
                decoded = char.ConvertFromUtf32(codePoint);
            }

            consumed = semicolon + 1;
            return true;
        }

        if (!Named.TryGetValue(body.ToString(), out string? value))
        {
            return false;
        }

        decoded = value;
        consumed = semicolon + 1;
        return true;
    }
}
