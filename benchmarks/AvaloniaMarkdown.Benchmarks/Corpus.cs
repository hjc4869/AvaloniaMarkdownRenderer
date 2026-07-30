using System.Text;

namespace AvaloniaMarkdown.Benchmarks;

/// <summary>Synthetic corpora used by the benchmarks.</summary>
internal static class Corpus
{
    /// <summary>Builds a document with <paramref name="sections"/> mixed-content sections.</summary>
    public static string Build(int sections)
    {
        var builder = new StringBuilder(sections * 320);

        for (int i = 0; i < sections; i++)
        {
            builder.Append("## Section ").Append(i).Append("\n\n");
            builder.Append("Paragraph ").Append(i)
                   .Append(" with **bold**, *italic*, ~~struck~~, `code` and a [link](https://example.com/")
                   .Append(i).Append(").\n\n");
            builder.Append("- alpha\n- beta\n  - nested gamma\n\n");

            if (i % 5 == 0)
            {
                builder.Append("> quoted text for section ").Append(i).Append("\n\n");
            }

            if (i % 10 == 0)
            {
                builder.Append("```csharp\npublic int Value").Append(i).Append(" => ").Append(i).Append(";\n```\n\n");
            }

            if (i % 25 == 0)
            {
                builder.Append("| key | value |\n|:----|------:|\n| a | ").Append(i).Append(" |\n\n");
            }
        }

        return builder.ToString();
    }

    /// <summary>Splits <paramref name="text"/> into chunks that look like LLM tokens.</summary>
    public static string[] Tokenize(string text, int averageLength = 4)
    {
        var chunks = new List<string>(text.Length / averageLength);
        var random = new Random(1234);

        int index = 0;
        while (index < text.Length)
        {
            int length = Math.Min(random.Next(1, (averageLength * 2) + 1), text.Length - index);
            chunks.Add(text.Substring(index, length));
            index += length;
        }

        return chunks.ToArray();
    }

    public static string CodeBlock(int lines)
    {
        var builder = new StringBuilder(lines * 24);
        builder.Append("```csharp\n");
        for (int i = 0; i < lines; i++)
        {
            builder.Append("    Console.WriteLine(\"line ").Append(i).Append("\");\n");
        }

        builder.Append("```\n");
        return builder.ToString();
    }
}
