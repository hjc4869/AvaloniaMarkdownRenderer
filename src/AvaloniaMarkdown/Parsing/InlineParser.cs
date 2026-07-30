using System.Text;
using AvaloniaMarkdown.Ast;

namespace AvaloniaMarkdown.Parsing;

/// <summary>
/// Converts the raw text of a single block into a flat <see cref="InlineContent"/>.
/// </summary>
/// <remarks>
/// <para>The parser runs in five stages:</para>
/// <list type="number">
///   <item><description>Tokenise: escapes, entities, code spans, autolinks, raw HTML, brackets, delimiter runs.</description></item>
///   <item><description>Resolve links and images from the bracket stack.</description></item>
///   <item><description>Resolve emphasis with the CommonMark delimiter-stack algorithm.</description></item>
///   <item><description>Materialise the display text, recording each token's output range.</description></item>
///   <item><description>Flatten the overlapping style spans into a sorted, non-overlapping run table.</description></item>
/// </list>
/// <para>
/// A single instance is reused for the lifetime of the parser thread; all working buffers are
/// pooled so a steady-state token append allocates only the two result arrays.
/// </para>
/// </remarks>
public sealed class InlineParser
{
    private readonly List<Token> _tokens = new();
    private readonly List<StyleSpan> _spans = new();
    private readonly List<InlineTarget> _targets = new();
    private readonly List<int> _brackets = new();
    private readonly List<int> _delimiters = new();
    private readonly List<int> _htmlStack = new();
    private readonly List<InlineRun> _runs = new();
    private readonly List<CharSpan> _charSpans = new();
    private readonly List<BoundaryEvent> _events = new();
    private readonly List<int> _active = new();
    private readonly StringBuilder _output = new();

    private string _source = string.Empty;
    private MarkdownOptions _options = MarkdownOptions.Default;

    /// <summary>Parses <paramref name="source"/> into styled runs.</summary>
    /// <param name="source">Raw block text with soft breaks already normalised to <c>\n</c>.</param>
    /// <param name="options">Feature switches.</param>
    /// <param name="autoCloseTrailing">
    /// True while the owning block is still being streamed; dangling emphasis openers are then
    /// closed at the end of the text so partial output does not flicker.
    /// </param>
    public InlineContent Parse(string source, MarkdownOptions options, bool autoCloseTrailing = false)
    {
        if (string.IsNullOrEmpty(source))
        {
            return InlineContent.Empty;
        }

        _source = source;
        _options = options;

        _tokens.Clear();
        _spans.Clear();
        _targets.Clear();
        _brackets.Clear();
        _delimiters.Clear();
        _htmlStack.Clear();
        _runs.Clear();
        _output.Clear();

        Tokenize();
        ResolveEmphasis(autoCloseTrailing && _options.AutoCloseStreamingEmphasis);
        ResolveHtmlTags();
        string text = Materialize();
        InlineRun[] runs = BuildRuns(text.Length);

        return new InlineContent(text, runs, _targets.Count == 0 ? Array.Empty<InlineTarget>() : _targets.ToArray());
    }

    // ------------------------------------------------------------------
    // Stage 1 - tokenisation
    // ------------------------------------------------------------------

    private void Tokenize()
    {
        ReadOnlySpan<char> src = _source;
        int i = 0;
        int textStart = 0;

        while (i < src.Length)
        {
            char c = src[i];

            switch (c)
            {
                case '\\' when i + 1 < src.Length && IsEscapable(src[i + 1]):
                    FlushText(textStart, i);
                    AddLiteral(src[i + 1].ToString());
                    i += 2;
                    textStart = i;
                    continue;

                case '\n':
                    FlushText(textStart, i);
                    AddToken(TokenKind.HardBreak, i, 1);
                    i++;
                    textStart = i;
                    continue;

                case '`':
                {
                    int runLength = CountRun(src, i, '`');
                    int contentStart = i + runLength;
                    int closer = FindClosingBacktickRun(src, contentStart, runLength);
                    if (closer < 0)
                    {
                        i += runLength;
                        continue;
                    }

                    FlushText(textStart, i);
                    AddCodeSpan(contentStart, closer - contentStart);
                    i = closer + runLength;
                    textStart = i;
                    continue;
                }

                case '&':
                {
                    if (HtmlEntities.TryDecode(src[i..], out string? decoded, out int consumed))
                    {
                        FlushText(textStart, i);
                        AddLiteral(decoded!);
                        i += consumed;
                        textStart = i;
                        continue;
                    }

                    i++;
                    continue;
                }

                case '<':
                {
                    if (TryReadAutolink(src, i, out int autoLength, out string? url))
                    {
                        FlushText(textStart, i);
                        AddAutolink(i + 1, autoLength - 2, url!);
                        i += autoLength;
                        textStart = i;
                        continue;
                    }

                    if (TryReadHtmlTag(src, i, out int tagLength, out InlineStyle style, out bool closing, out string? href))
                    {
                        FlushText(textStart, i);
                        AddHtmlTag(i, tagLength, style, closing, href);
                        i += tagLength;
                        textStart = i;
                        continue;
                    }

                    i++;
                    continue;
                }

                case '!' when i + 1 < src.Length && src[i + 1] == '[':
                    FlushText(textStart, i);
                    AddBracketOpen(i, 2, isImage: true);
                    i += 2;
                    textStart = i;
                    continue;

                case '[':
                    FlushText(textStart, i);
                    AddBracketOpen(i, 1, isImage: false);
                    i++;
                    textStart = i;
                    continue;

                case ']':
                {
                    FlushText(textStart, i);
                    int consumed = CloseBracket(i);
                    i += consumed;
                    textStart = i;
                    continue;
                }

                case '*':
                case '_':
                case '~':
                {
                    if (c == '~' && !_options.EnableStrikethrough)
                    {
                        i++;
                        continue;
                    }

                    int runLength = CountRun(src, i, c);
                    FlushText(textStart, i);
                    AddDelimiter(i, runLength, c);
                    i += runLength;
                    textStart = i;
                    continue;
                }

                case 'h' or 'H' or 'w' or 'W' or 'f' or 'F' when _options.EnableAutoLinks:
                {
                    if (IsWordBoundary(src, i) && AutoLinkScanner.TryScan(src, i, out int linkLength, out string? autoUrl))
                    {
                        FlushText(textStart, i);
                        AddAutolink(i, linkLength, autoUrl!);
                        i += linkLength;
                        textStart = i;
                        continue;
                    }

                    i++;
                    continue;
                }

                default:
                    i++;
                    continue;
            }
        }

        FlushText(textStart, src.Length);
    }

    private void FlushText(int start, int end)
    {
        if (end > start)
        {
            AddToken(TokenKind.Text, start, end - start);
        }
    }

    private void AddToken(TokenKind kind, int start, int length)
    {
        _tokens.Add(new Token { Kind = kind, Start = start, Length = length });
    }

    private void AddLiteral(string literal)
    {
        _tokens.Add(new Token { Kind = TokenKind.Literal, Literal = literal });
    }

    private void AddCodeSpan(int start, int length)
    {
        _tokens.Add(new Token { Kind = TokenKind.CodeSpan, Start = start, Length = length });
    }

    private void AddAutolink(int start, int length, string url)
    {
        _targets.Add(new InlineTarget(url, null, isImage: false));
        _tokens.Add(new Token { Kind = TokenKind.Autolink, Start = start, Length = length, TargetId = _targets.Count - 1 });
    }

    private void AddHtmlTag(int start, int length, InlineStyle style, bool closing, string? href)
    {
        _tokens.Add(new Token
        {
            Kind = closing ? TokenKind.HtmlClose : TokenKind.HtmlOpen,
            Start = start,
            Length = length,
            Style = style,
            Literal = href,
        });
    }

    private void AddBracketOpen(int start, int length, bool isImage)
    {
        _tokens.Add(new Token { Kind = TokenKind.BracketOpen, Start = start, Length = length, IsImage = isImage });
        _brackets.Add(_tokens.Count - 1);
    }

    private void AddDelimiter(int start, int count, char delimiter)
    {
        (bool canOpen, bool canClose) = ClassifyDelimiterRun(_source, start, count, delimiter);
        _tokens.Add(new Token
        {
            Kind = TokenKind.Delimiter,
            Start = start,
            Length = count,
            DelimChar = delimiter,
            DelimCount = count,
            CanOpen = canOpen,
            CanClose = canClose,
        });

        _delimiters.Add(_tokens.Count - 1);
    }

    /// <summary>Handles a <c>]</c>, resolving it into a link/image when a destination follows.</summary>
    private int CloseBracket(int index)
    {
        if (_brackets.Count == 0)
        {
            AddToken(TokenKind.Text, index, 1);
            return 1;
        }

        int openerIndex = _brackets[^1];
        _brackets.RemoveAt(_brackets.Count - 1);

        int after = index + 1;
        if (!TryReadLinkDestination(_source, after, out int destLength, out string? url, out string? title))
        {
            AddToken(TokenKind.Text, index, 1);
            return 1;
        }

        Token opener = _tokens[openerIndex];
        bool isImage = opener.IsImage;
        _targets.Add(new InlineTarget(url!, title, isImage));
        int targetId = _targets.Count - 1;

        opener.Kind = TokenKind.Removed;
        _tokens[openerIndex] = opener;

        _tokens.Add(new Token { Kind = TokenKind.Removed });
        int closerIndex = _tokens.Count - 1;

        _spans.Add(new StyleSpan(openerIndex, closerIndex, isImage ? InlineStyle.Image : InlineStyle.Link, targetId));
        return 1 + destLength;
    }

    // ------------------------------------------------------------------
    // Stage 3 - emphasis
    // ------------------------------------------------------------------

    private void ResolveEmphasis(bool autoCloseTrailing)
    {
        for (int ci = 0; ci < _delimiters.Count; ci++)
        {
            int closerIndex = _delimiters[ci];
            Token closer = _tokens[closerIndex];
            if (closer.Kind != TokenKind.Delimiter || !closer.CanClose || closer.DelimCount == 0)
            {
                continue;
            }

            while (closer.DelimCount > 0)
            {
                int openerListIndex = FindOpener(ci, closer);
                if (openerListIndex < 0)
                {
                    break;
                }

                int openerIndex = _delimiters[openerListIndex];
                Token opener = _tokens[openerIndex];

                int use = closer.DelimChar == '~'
                    ? Math.Min(opener.DelimCount, Math.Min(closer.DelimCount, 2))
                    : (opener.DelimCount >= 2 && closer.DelimCount >= 2 ? 2 : 1);

                InlineStyle style = closer.DelimChar switch
                {
                    '~' => InlineStyle.Strikethrough,
                    _ => use >= 2 ? InlineStyle.Bold : InlineStyle.Italic,
                };

                opener.DelimCount -= use;
                closer.DelimCount -= use;
                _tokens[openerIndex] = opener;
                _tokens[closerIndex] = closer;

                _spans.Add(new StyleSpan(openerIndex, closerIndex, style, -1));

                // Delimiters trapped between the pair can no longer match anything.
                for (int k = openerListIndex + 1; k < ci; k++)
                {
                    Token trapped = _tokens[_delimiters[k]];
                    if (trapped.Kind != TokenKind.Delimiter)
                    {
                        continue;
                    }

                    trapped.CanOpen = false;
                    trapped.CanClose = false;
                    _tokens[_delimiters[k]] = trapped;
                }
            }
        }

        if (!autoCloseTrailing)
        {
            return;
        }

        // Streaming tail: close any opener that never found a partner.
        for (int i = _delimiters.Count - 1; i >= 0; i--)
        {
            int index = _delimiters[i];
            Token token = _tokens[index];
            if (token.Kind != TokenKind.Delimiter || !token.CanOpen || token.DelimCount == 0)
            {
                continue;
            }

            int use = token.DelimChar == '~' ? Math.Min(token.DelimCount, 2) : (token.DelimCount >= 2 ? 2 : 1);
            InlineStyle style = token.DelimChar switch
            {
                '~' => InlineStyle.Strikethrough,
                _ => use >= 2 ? InlineStyle.Bold : InlineStyle.Italic,
            };

            token.DelimCount -= use;
            _tokens[index] = token;
            _spans.Add(new StyleSpan(index, _tokens.Count, style | InlineStyle.Provisional, -1));
        }
    }

    private int FindOpener(int closerListIndex, in Token closer)
    {
        for (int i = closerListIndex - 1; i >= 0; i--)
        {
            Token candidate = _tokens[_delimiters[i]];
            if (candidate.Kind != TokenKind.Delimiter ||
                !candidate.CanOpen ||
                candidate.DelimCount == 0 ||
                candidate.DelimChar != closer.DelimChar)
            {
                continue;
            }

            // CommonMark "rule of three".
            if (closer.DelimChar != '~' && (candidate.CanClose || closer.CanOpen))
            {
                int sum = candidate.Length + closer.Length;
                if (sum % 3 == 0 && (candidate.Length % 3 != 0 || closer.Length % 3 != 0))
                {
                    continue;
                }
            }

            return i;
        }

        return -1;
    }

    // ------------------------------------------------------------------
    // Stage 3b - inline HTML
    // ------------------------------------------------------------------

    private void ResolveHtmlTags()
    {
        _htmlStack.Clear();
        for (int i = 0; i < _tokens.Count; i++)
        {
            Token token = _tokens[i];
            if (token.Kind == TokenKind.HtmlOpen)
            {
                if (token.Style == InlineStyle.None)
                {
                    // <br> and friends: no styling, no pairing required.
                    continue;
                }

                _htmlStack.Add(i);
            }
            else if (token.Kind == TokenKind.HtmlClose)
            {
                for (int s = _htmlStack.Count - 1; s >= 0; s--)
                {
                    Token opener = _tokens[_htmlStack[s]];
                    if (opener.Style != token.Style)
                    {
                        continue;
                    }

                    int targetId = -1;
                    if (opener.Literal is { } href)
                    {
                        _targets.Add(new InlineTarget(href, null, isImage: false));
                        targetId = _targets.Count - 1;
                    }

                    _spans.Add(new StyleSpan(_htmlStack[s], i, opener.Style, targetId));
                    _htmlStack.RemoveRange(s, _htmlStack.Count - s);
                    break;
                }
            }
        }
    }

    // ------------------------------------------------------------------
    // Stage 4 - materialisation
    // ------------------------------------------------------------------

    private string Materialize()
    {
        ReadOnlySpan<char> src = _source;

        for (int i = 0; i < _tokens.Count; i++)
        {
            Token token = _tokens[i];
            token.OutStart = _output.Length;

            switch (token.Kind)
            {
                case TokenKind.Text:
                    _output.Append(src.Slice(token.Start, token.Length));
                    break;

                case TokenKind.Literal:
                    _output.Append(token.Literal);
                    break;

                case TokenKind.CodeSpan:
                    AppendCodeSpan(src.Slice(token.Start, token.Length));
                    break;

                case TokenKind.Autolink:
                    _output.Append(src.Slice(token.Start, token.Length));
                    break;

                case TokenKind.HardBreak:
                    _output.Append(_options.SoftLineBreaksAsHardBreaks ? '\n' : ' ');
                    break;

                case TokenKind.Delimiter:
                    _output.Append(token.DelimChar, token.DelimCount);
                    break;

                case TokenKind.BracketOpen:
                    _output.Append(src.Slice(token.Start, token.Length));
                    break;

                case TokenKind.HtmlOpen:
                case TokenKind.HtmlClose:
                case TokenKind.Removed:
                    break;
            }

            token.OutEnd = _output.Length;
            _tokens[i] = token;
        }

        return _output.ToString();
    }

    private void AppendCodeSpan(ReadOnlySpan<char> content)
    {
        int start = 0;
        int end = content.Length;

        if (end - start >= 2 && content[start] == ' ' && content[end - 1] == ' ')
        {
            bool allSpaces = true;
            for (int i = start; i < end; i++)
            {
                if (content[i] != ' ')
                {
                    allSpaces = false;
                    break;
                }
            }

            if (!allSpaces)
            {
                start++;
                end--;
            }
        }

        for (int i = start; i < end; i++)
        {
            _output.Append(content[i] == '\n' ? ' ' : content[i]);
        }
    }

    // ------------------------------------------------------------------
    // Stage 5 - run flattening
    // ------------------------------------------------------------------

    private InlineRun[] BuildRuns(int textLength)
    {
        if (textLength == 0)
        {
            return Array.Empty<InlineRun>();
        }

        _charSpans.Clear();

        // Convert token-index spans to character spans, dropping empty ones.
        for (int i = 0; i < _spans.Count; i++)
        {
            StyleSpan span = _spans[i];
            int start = span.OpenToken < _tokens.Count ? _tokens[span.OpenToken].OutEnd : textLength;
            int end = span.CloseToken < _tokens.Count ? _tokens[span.CloseToken].OutStart : textLength;
            if (end > start)
            {
                _charSpans.Add(new CharSpan(start, end, span.Style, span.TargetId));
            }
        }

        // Code spans and autolinks are single-token styles.
        for (int i = 0; i < _tokens.Count; i++)
        {
            Token token = _tokens[i];
            if (token.OutEnd <= token.OutStart)
            {
                continue;
            }

            if (token.Kind == TokenKind.CodeSpan)
            {
                _charSpans.Add(new CharSpan(token.OutStart, token.OutEnd, InlineStyle.Code, -1));
            }
            else if (token.Kind == TokenKind.Autolink)
            {
                _charSpans.Add(new CharSpan(token.OutStart, token.OutEnd, InlineStyle.Link, token.TargetId));
            }
        }

        if (_charSpans.Count == 0)
        {
            return new[] { new InlineRun(0, textLength, InlineStyle.None) };
        }

        // Sweep line: O(S log S + segments * nesting depth) instead of O(segments * S).
        _events.Clear();
        for (int i = 0; i < _charSpans.Count; i++)
        {
            CharSpan span = _charSpans[i];
            _events.Add(new BoundaryEvent(span.Start, 1, i));
            _events.Add(new BoundaryEvent(span.End, -1, i));
        }

        _events.Sort(static (a, b) => a.Position != b.Position ? a.Position.CompareTo(b.Position) : a.Delta.CompareTo(b.Delta));

        _runs.Clear();
        _active.Clear();
        int position = 0;
        int index = 0;

        while (index < _events.Count)
        {
            int boundary = _events[index].Position;
            if (boundary > position)
            {
                EmitSegment(position, boundary);
                position = boundary;
            }

            while (index < _events.Count && _events[index].Position == boundary)
            {
                BoundaryEvent evt = _events[index++];
                if (evt.Delta > 0)
                {
                    _active.Add(evt.SpanIndex);
                }
                else
                {
                    _active.Remove(evt.SpanIndex);
                }
            }
        }

        if (position < textLength)
        {
            EmitSegment(position, textLength);
        }

        return _runs.ToArray();
    }

    private void EmitSegment(int start, int end)
    {
        InlineStyle style = InlineStyle.None;
        int targetId = -1;

        for (int i = 0; i < _active.Count; i++)
        {
            CharSpan span = _charSpans[_active[i]];
            style |= span.Style;
            if (span.TargetId >= 0)
            {
                targetId = span.TargetId;
            }
        }

        if (_runs.Count > 0)
        {
            InlineRun last = _runs[^1];
            if (last.End == start && last.Style == style && last.TargetId == targetId)
            {
                _runs[^1] = new InlineRun(last.Start, last.Length + (end - start), style, targetId);
                return;
            }
        }

        _runs.Add(new InlineRun(start, end - start, style, targetId));
    }

    // ------------------------------------------------------------------
    // Scanners
    // ------------------------------------------------------------------

    private static int CountRun(ReadOnlySpan<char> text, int start, char c)
    {
        int i = start;
        while (i < text.Length && text[i] == c)
        {
            i++;
        }

        return i - start;
    }

    private static int FindClosingBacktickRun(ReadOnlySpan<char> text, int from, int runLength)
    {
        int i = from;
        while (i < text.Length)
        {
            if (text[i] != '`')
            {
                i++;
                continue;
            }

            int length = CountRun(text, i, '`');
            if (length == runLength)
            {
                return i;
            }

            i += length;
        }

        return -1;
    }

    private static bool IsEscapable(char c) =>
        c is '\\' or '`' or '*' or '_' or '{' or '}' or '[' or ']' or '(' or ')' or '#' or '+' or '-'
            or '.' or '!' or '|' or '<' or '>' or '~' or '"' or '\'' or '$' or '%' or '&' or ',' or '/'
            or ':' or ';' or '=' or '?' or '@' or '^';

    private static bool IsWordBoundary(ReadOnlySpan<char> text, int index) =>
        index == 0 || !char.IsLetterOrDigit(text[index - 1]);

    private static (bool CanOpen, bool CanClose) ClassifyDelimiterRun(ReadOnlySpan<char> text, int start, int count, char delimiter)
    {
        char before = start > 0 ? text[start - 1] : '\n';
        int afterIndex = start + count;
        char after = afterIndex < text.Length ? text[afterIndex] : '\n';

        bool beforeWhitespace = char.IsWhiteSpace(before);
        bool afterWhitespace = char.IsWhiteSpace(after);
        bool beforePunctuation = IsPunctuation(before);
        bool afterPunctuation = IsPunctuation(after);

        bool leftFlanking = !afterWhitespace && (!afterPunctuation || beforeWhitespace || beforePunctuation);
        bool rightFlanking = !beforeWhitespace && (!beforePunctuation || afterWhitespace || afterPunctuation);

        if (delimiter == '_')
        {
            return (leftFlanking && (!rightFlanking || beforePunctuation),
                    rightFlanking && (!leftFlanking || afterPunctuation));
        }

        return (leftFlanking, rightFlanking);
    }

    private static bool IsPunctuation(char c) => !char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c);

    /// <summary>Reads <c>&lt;https://example.com&gt;</c> style autolinks.</summary>
    private static bool TryReadAutolink(ReadOnlySpan<char> text, int index, out int length, out string? url)
    {
        length = 0;
        url = null;

        int close = text[index..].IndexOf('>');
        if (close < 0)
        {
            return false;
        }

        ReadOnlySpan<char> inner = text.Slice(index + 1, close - 1);
        if (inner.IsEmpty || inner.IndexOf(' ') >= 0)
        {
            return false;
        }

        int colon = inner.IndexOf(':');
        bool isEmail = colon < 0 && inner.IndexOf('@') > 0;
        if (colon <= 0 && !isEmail)
        {
            return false;
        }

        if (!isEmail && !UriSafety.IsAllowedScheme(inner[..colon]))
        {
            return false;
        }

        url = isEmail ? string.Concat("mailto:", inner) : inner.ToString();
        length = close + 1;
        return true;
    }

    /// <summary>Reads a safe inline HTML tag, mapping it to a style.</summary>
    private static bool TryReadHtmlTag(
        ReadOnlySpan<char> text,
        int index,
        out int length,
        out InlineStyle style,
        out bool closing,
        out string? href)
    {
        length = 0;
        style = InlineStyle.None;
        closing = false;
        href = null;

        int i = index + 1;
        if (i < text.Length && text[i] == '/')
        {
            closing = true;
            i++;
        }

        int nameStart = i;
        while (i < text.Length && char.IsAsciiLetterOrDigit(text[i]))
        {
            i++;
        }

        if (i == nameStart)
        {
            return false;
        }

        ReadOnlySpan<char> name = text[nameStart..i];
        if (!HtmlBlockScanner.IsSafeInlineTag(name))
        {
            return false;
        }

        int close = text[i..].IndexOf('>');
        if (close < 0)
        {
            return false;
        }

        ReadOnlySpan<char> attributes = text.Slice(i, close);
        length = (i + close + 1) - index;

        style = name switch
        {
            var n when n.Equals("b", StringComparison.OrdinalIgnoreCase) => InlineStyle.Bold,
            var n when n.Equals("strong", StringComparison.OrdinalIgnoreCase) => InlineStyle.Bold,
            var n when n.Equals("i", StringComparison.OrdinalIgnoreCase) => InlineStyle.Italic,
            var n when n.Equals("em", StringComparison.OrdinalIgnoreCase) => InlineStyle.Italic,
            var n when n.Equals("u", StringComparison.OrdinalIgnoreCase) => InlineStyle.Underline,
            var n when n.Equals("ins", StringComparison.OrdinalIgnoreCase) => InlineStyle.Underline,
            var n when n.Equals("s", StringComparison.OrdinalIgnoreCase) => InlineStyle.Strikethrough,
            var n when n.Equals("del", StringComparison.OrdinalIgnoreCase) => InlineStyle.Strikethrough,
            var n when n.Equals("code", StringComparison.OrdinalIgnoreCase) => InlineStyle.Code,
            var n when n.Equals("kbd", StringComparison.OrdinalIgnoreCase) => InlineStyle.Code,
            var n when n.Equals("mark", StringComparison.OrdinalIgnoreCase) => InlineStyle.Highlight,
            var n when n.Equals("sub", StringComparison.OrdinalIgnoreCase) => InlineStyle.Subscript,
            var n when n.Equals("sup", StringComparison.OrdinalIgnoreCase) => InlineStyle.Superscript,
            var n when n.Equals("a", StringComparison.OrdinalIgnoreCase) => InlineStyle.Link,
            _ => InlineStyle.None,
        };

        if (style == InlineStyle.Link && !closing)
        {
            href = ReadHrefAttribute(attributes);
            if (href is null)
            {
                style = InlineStyle.None;
            }
        }

        return true;
    }

    private static string? ReadHrefAttribute(ReadOnlySpan<char> attributes)
    {
        int index = attributes.IndexOf("href", StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        int i = index + 4;
        while (i < attributes.Length && (attributes[i] == ' ' || attributes[i] == '='))
        {
            i++;
        }

        if (i >= attributes.Length)
        {
            return null;
        }

        char quote = attributes[i];
        if (quote is '"' or '\'')
        {
            i++;
            int end = attributes[i..].IndexOf(quote);
            if (end < 0)
            {
                return null;
            }

            return UriSafety.Sanitize(attributes.Slice(i, end));
        }

        int stop = i;
        while (stop < attributes.Length && attributes[stop] != ' ' && attributes[stop] != '>')
        {
            stop++;
        }

        return UriSafety.Sanitize(attributes[i..stop]);
    }

    /// <summary>Reads <c>(url "title")</c> immediately after a link label.</summary>
    private static bool TryReadLinkDestination(ReadOnlySpan<char> text, int index, out int length, out string? url, out string? title)
    {
        length = 0;
        url = null;
        title = null;

        if (index >= text.Length || text[index] != '(')
        {
            return false;
        }

        int i = index + 1;
        while (i < text.Length && char.IsWhiteSpace(text[i]))
        {
            i++;
        }

        int urlStart = i;
        int urlEnd;

        if (i < text.Length && text[i] == '<')
        {
            i++;
            urlStart = i;
            while (i < text.Length && text[i] != '>' && text[i] != '\n')
            {
                i++;
            }

            if (i >= text.Length)
            {
                return false;
            }

            urlEnd = i;
            i++;
        }
        else
        {
            int depth = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (c == '\\' && i + 1 < text.Length)
                {
                    i += 2;
                    continue;
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
                else if (char.IsWhiteSpace(c))
                {
                    break;
                }

                i++;
            }

            urlEnd = i;
        }

        while (i < text.Length && char.IsWhiteSpace(text[i]))
        {
            i++;
        }

        if (i < text.Length && (text[i] == '"' || text[i] == '\''))
        {
            char quote = text[i];
            i++;
            int titleStart = i;
            while (i < text.Length && text[i] != quote)
            {
                i++;
            }

            if (i >= text.Length)
            {
                return false;
            }

            title = text[titleStart..i].ToString();
            i++;

            while (i < text.Length && char.IsWhiteSpace(text[i]))
            {
                i++;
            }
        }

        if (i >= text.Length || text[i] != ')')
        {
            return false;
        }

        url = UriSafety.Sanitize(text[urlStart..urlEnd]);
        if (url is null)
        {
            return false;
        }

        length = i + 1 - index;
        return true;
    }

    // ------------------------------------------------------------------
    // Internal data
    // ------------------------------------------------------------------

    private enum TokenKind : byte
    {
        Text,
        Literal,
        CodeSpan,
        Autolink,
        HardBreak,
        Delimiter,
        BracketOpen,
        HtmlOpen,
        HtmlClose,
        Removed,
    }

    private struct Token
    {
        public TokenKind Kind;
        public int Start;
        public int Length;
        public string? Literal;
        public char DelimChar;
        public int DelimCount;
        public bool CanOpen;
        public bool CanClose;
        public bool IsImage;
        public int TargetId;
        public InlineStyle Style;
        public int OutStart;
        public int OutEnd;
    }

    private readonly record struct StyleSpan(int OpenToken, int CloseToken, InlineStyle Style, int TargetId);

    private readonly record struct CharSpan(int Start, int End, InlineStyle Style, int TargetId);

    private readonly record struct BoundaryEvent(int Position, int Delta, int SpanIndex);
}
