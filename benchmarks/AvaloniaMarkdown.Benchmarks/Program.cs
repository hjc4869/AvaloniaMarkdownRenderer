using System.Diagnostics;
using BenchmarkDotNet.Running;

namespace AvaloniaMarkdown.Benchmarks;

public static class Program
{
    public static void Main(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("quick", StringComparison.OrdinalIgnoreCase))
        {
            RunQuickProfile();
            return;
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }

    /// <summary>
    /// A fast, dependency-free sanity profile. Useful in CI where a full BenchmarkDotNet run is
    /// too slow, and as an executable proof that append latency does not grow with document size.
    /// </summary>
    private static void RunQuickProfile()
    {
        Console.WriteLine("Append latency vs. document size (lower is better, must stay flat)");
        Console.WriteLine("  sections     blocks   tokens   total ms   avg us/token   p99 us   alloc MB");

        foreach (int sections in new[] { 100, 1_000, 10_000, 50_000 })
        {
            var document = new MarkdownDocument(MarkdownOptions.Default, MarkdownProcessingMode.Inline);
            document.Append(Corpus.Build(sections));

            string[] tokens = Corpus.Tokenize(Corpus.Build(200));
            var samples = new double[tokens.Length];

            long before = GC.GetAllocatedBytesForCurrentThread();
            var total = Stopwatch.StartNew();

            for (int i = 0; i < tokens.Length; i++)
            {
                long start = Stopwatch.GetTimestamp();
                document.Append(tokens[i]);
                samples[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds * 1000;
            }

            total.Stop();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Array.Sort(samples);
            double average = samples.Average();
            double p99 = samples[(int)(samples.Length * 0.99)];

            Console.WriteLine(
                $"  {sections,8:N0}   {document.Snapshot.Count,8:N0}   {tokens.Length,6:N0}   " +
                $"{total.Elapsed.TotalMilliseconds,8:F1}   {average,12:F2}   {p99,6:F1}   {allocated / 1024.0 / 1024.0,8:F2}");
        }

        Console.WriteLine();
        Console.WriteLine("Bulk parse throughput");
        Console.WriteLine("  characters      blocks     ms     MB/s");

        foreach (int sections in new[] { 1_000, 10_000, 50_000 })
        {
            string markdown = Corpus.Build(sections);
            var stopwatch = Stopwatch.StartNew();

            var document = new MarkdownDocument(MarkdownOptions.Default, MarkdownProcessingMode.Inline);
            document.Append(markdown);
            document.Complete();

            stopwatch.Stop();
            double megabytes = markdown.Length * 2 / 1024.0 / 1024.0;

            Console.WriteLine(
                $"  {markdown.Length,10:N0}   {document.Snapshot.Count,9:N0}   {stopwatch.Elapsed.TotalMilliseconds,6:F0}   " +
                $"{megabytes / stopwatch.Elapsed.TotalSeconds,6:F1}");
        }

        Console.WriteLine();
        Console.WriteLine("Steady-state memory");

        long baseline = GC.GetTotalMemory(forceFullCollection: true);
        var big = new MarkdownDocument(MarkdownOptions.Default, MarkdownProcessingMode.Inline);
        big.Append(Corpus.Build(50_000));
        big.Complete();
        long retained = GC.GetTotalMemory(forceFullCollection: true) - baseline;

        Console.WriteLine($"  {big.Snapshot.Count:N0} blocks retained {retained / 1024.0 / 1024.0:F1} MB " +
                          $"({retained / (double)big.Snapshot.Count:F0} bytes/block)");
    }
}
