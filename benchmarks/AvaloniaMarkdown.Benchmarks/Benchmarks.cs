using BenchmarkDotNet.Attributes;
using AvaloniaMarkdown.Diffing;
using AvaloniaMarkdown.Flattening;

namespace AvaloniaMarkdown.Benchmarks;

/// <summary>
/// Latency of a single streamed token, which is the number that decides whether a UI stutters.
/// </summary>
/// <remarks>
/// Measured against documents of very different sizes: if the design is truly incremental, append
/// latency must be flat rather than growing with the document.
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 8)]
public class AppendLatencyBenchmarks
{
    private MarkdownDocument _document = null!;
    private string[] _tokens = null!;
    private int _cursor;

    /// <summary>Number of sections already parsed before the measured append.</summary>
    [Params(0, 100, 1_000, 10_000)]
    public int PreloadedSections { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _tokens = Corpus.Tokenize(Corpus.Build(2_000));
    }

    [IterationSetup]
    public void IterationSetup()
    {
        _document = new MarkdownDocument(MarkdownOptions.Default, MarkdownProcessingMode.Inline);
        if (PreloadedSections > 0)
        {
            _document.Append(Corpus.Build(PreloadedSections));
        }

        _cursor = 0;
    }

    /// <summary>Appends 1 000 tokens; divide the reported time by 1 000 for per-token latency.</summary>
    [Benchmark(OperationsPerInvoke = 1_000)]
    public MarkdownSnapshot AppendThousandTokens()
    {
        for (int i = 0; i < 1_000; i++)
        {
            _document.Append(_tokens[_cursor++ % _tokens.Length]);
        }

        return _document.Snapshot;
    }
}

/// <summary>Throughput of a bulk parse (loading an existing conversation, for example).</summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 2, iterationCount: 6)]
public class ParsingThroughputBenchmarks
{
    private string _small = null!;
    private string _medium = null!;
    private string _large = null!;
    private string _hugeCodeBlock = null!;

    [GlobalSetup]
    public void Setup()
    {
        _small = Corpus.Build(100);
        _medium = Corpus.Build(2_000);
        _large = Corpus.Build(20_000);
        _hugeCodeBlock = Corpus.CodeBlock(50_000);
    }

    [Benchmark(Baseline = true)]
    public int Small() => Parse(_small);

    [Benchmark]
    public int Medium() => Parse(_medium);

    [Benchmark]
    public int Large() => Parse(_large);

    [Benchmark]
    public int CodeBlock50kLines() => Parse(_hugeCodeBlock);

    private static int Parse(string markdown)
    {
        var document = new MarkdownDocument(MarkdownOptions.Default, MarkdownProcessingMode.Inline);
        document.Append(markdown);
        document.Complete();
        return document.Snapshot.Count;
    }
}

/// <summary>
/// Cost of turning two snapshots into render operations, executed on the UI thread once per frame.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 8)]
public class DiffBenchmarks
{
    private readonly BlockDiffEngine _engine = new();
    private MarkdownDocument _document = null!;
    private MarkdownSnapshot _before = null!;
    private MarkdownSnapshot _after = null!;

    [Params(1_000, 50_000)]
    public int Blocks { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _document = new MarkdownDocument(MarkdownOptions.Default, MarkdownProcessingMode.Inline);
        _document.Append(string.Concat(Enumerable.Repeat("paragraph text\n\n", Blocks)));
        _document.Append("streaming tail");
        _before = _document.Snapshot;
        _document.Append(" token");
        _after = _document.Snapshot;
    }

    [Benchmark]
    public int DiffOneAppendedToken() => _engine.Diff(_before, _after).Count;
}

/// <summary>Steady-state memory cost of streaming a long conversation.</summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 1, iterationCount: 3)]
public class MemoryBenchmarks
{
    private string[] _tokens = null!;

    [GlobalSetup]
    public void Setup() => _tokens = Corpus.Tokenize(Corpus.Build(5_000));

    [Benchmark]
    public long StreamWholeDocument()
    {
        var document = new MarkdownDocument(MarkdownOptions.Default, MarkdownProcessingMode.Inline);
        foreach (string token in _tokens)
        {
            document.Append(token);
        }

        document.Complete();
        return document.Snapshot.Count;
    }
}
