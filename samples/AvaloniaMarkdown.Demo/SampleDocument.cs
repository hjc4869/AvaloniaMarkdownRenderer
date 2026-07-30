namespace AvaloniaMarkdown.Demo;

/// <summary>A feature-complete sample used by the demo shell.</summary>
internal static class SampleDocument
{
    public const string Text = """
        # Streaming Markdown Renderer

        A **high-performance**, *incremental* GitHub-Flavored Markdown renderer for AvaloniaUI,
        designed for real-time LLM output. It is `token-oriented` rather than document-oriented.

        ## Text formatting

        Bold: **bold text**. Italic: *italic text*. Both: ***bold italic***.
        Strikethrough: ~~removed~~. Inline code: `var x = renderer.Append(chunk);`.
        Escaped markup: \*not emphasis\* and \`not code\`.
        Inline HTML: <b>bold</b>, <em>emphasis</em>, <code>code</code>, <mark>highlight</mark>,
        H<sub>2</sub>O and x<sup>2</sup>.

        ## Headings

        ### Level three
        #### Level four
        ##### Level five
        ###### Level six

        Setext heading
        ==============

        ## Lists

        - Unordered with a dash
        * Unordered with an asterisk
        + Unordered with a plus
          - Nested one level
            - Nested two levels
              - Nested three levels

        1. Ordered first
        2. Ordered second
           1. Nested ordered
           2. Another
        3. Ordered third

        ## Task lists

        - [x] Incremental block parser
        - [x] Speculative tail parsing with rollback
        - [x] Viewport virtualization
        - [ ] Syntax highlighting (deliberately out of scope)

        ## Block quotes

        > A single-level quote.
        >
        > > A nested quote, which keeps its own bar.
        > >
        > > - even lists work inside quotes
        > > - like this

        ## Horizontal rules

        ---

        ***

        ___

        ## Code blocks

        ```csharp
        public sealed class StreamingRenderer
        {
            private readonly MarkdownDocument _document = new();

            public void OnToken(string chunk)
            {
                // Safe from any thread: parsing happens on a pooled worker.
                _document.Append(chunk);
            }

            public void OnStreamEnd() => _document.Complete();
        }
        ```

        ```python
        def fibonacci(n: int) -> int:
            a, b = 0, 1
            for _ in range(n):
                a, b = b, a + b
            return a
        ```

        A code block with a very long line that must scroll horizontally rather than wrap:

        ```text
        this is a deliberately long single line ------------------------------------------------------------------------------------------------------------- end
        ```

        Indented code also works:

            def indented():
                return True

        ## Tables

        | Feature              | Status | Notes                                  |
        |:---------------------|:------:|---------------------------------------:|
        | Incremental parsing  |   OK   | O(new text) per append                 |
        | Virtualization       |   OK   | Fenwick-tree height cache              |
        | Streaming code fence |   OK   | Segment level reuse                    |
        | Syntax highlighting  |   --   | Extension point                        |

        ## Links and images

        An inline [link with a title](https://avaloniaui.net "Avalonia"), a bare URL
        https://github.com/AvaloniaUI/Avalonia, a www link www.avaloniaui.net and an
        email <hello@example.com>.

        ![Avalonia logo](https://avatars.githubusercontent.com/u/14075148?s=200&v=4)

        ## HTML degradation

        <div class="callout">
        Unsupported block HTML degrades to plain text instead of being dropped.
        </div>

        <script>alert('this is never executed')</script>

        ## Long paragraph

        Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor incididunt ut
        labore et dolore magna aliqua. Ut enim ad minim veniam, quis nostrud exercitation ullamco
        laboris nisi ut aliquip ex ea commodo consequat. Duis aute irure dolor in reprehenderit in
        voluptate velit esse cillum dolore eu fugiat nulla pariatur. Excepteur sint occaecat
        cupidatat non proident, sunt in culpa qui officia deserunt mollit anim id est laborum.

        """;
}
