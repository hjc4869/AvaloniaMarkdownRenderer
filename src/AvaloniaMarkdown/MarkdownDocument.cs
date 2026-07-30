using System.Text;
using AvaloniaMarkdown.Diffing;
using AvaloniaMarkdown.Flattening;
using AvaloniaMarkdown.Parsing;
using AvaloniaMarkdown.Text;

namespace AvaloniaMarkdown;

/// <summary>How a <see cref="MarkdownDocument"/> executes its parsing work.</summary>
public enum MarkdownProcessingMode
{
    /// <summary>Parse on a pooled background thread. Never blocks the caller. Default.</summary>
    Background,

    /// <summary>Parse synchronously on the calling thread. Intended for tests and benchmarks.</summary>
    Inline,
}

/// <summary>
/// A streaming markdown document: the public entry point of the engine.
/// </summary>
/// <remarks>
/// <para>
/// Mutating calls (<see cref="Append(string)"/>, <see cref="Replace"/>, <see cref="Clear"/>,
/// <see cref="Reset"/>, <see cref="Complete"/>) are safe to make from any thread and are applied
/// in submission order. In <see cref="MarkdownProcessingMode.Background"/> the caller only
/// enqueues; tokenising, parsing and flattening all happen on a pooled worker so the UI thread
/// never parses.
/// </para>
/// <para>
/// Each batch produces an immutable <see cref="MarkdownSnapshot"/> and raises
/// <see cref="SnapshotChanged"/> <b>on the worker thread</b>. Views coalesce those notifications
/// onto the UI thread.
/// </para>
/// </remarks>
public sealed class MarkdownDocument
{
    private readonly TextBuffer _buffer = new();
    private readonly BlockParser _parser;
    private readonly DocumentFlattener _flattener;
    private readonly MarkdownProcessingMode _mode;

    private readonly object _gate = new();
    private readonly Queue<Command> _queue = new();
    private readonly List<TaskCompletionSource> _idleWaiters = new();
    private readonly StringBuilder _coalesceBuffer = new();

    private bool _running;
    private int _lineStart;
    private int _scanFrom;
    private volatile bool _completed;

    private MarkdownSnapshot _snapshot = MarkdownSnapshot.Empty;

    public MarkdownDocument()
        : this(MarkdownOptions.Default, MarkdownProcessingMode.Background)
    {
    }

    public MarkdownDocument(MarkdownOptions options, MarkdownProcessingMode mode = MarkdownProcessingMode.Background)
    {
        Options = options;
        _mode = mode;
        _parser = new BlockParser(_buffer);
        _flattener = new DocumentFlattener(_buffer, options);
    }

    /// <summary>Raised on the parsing thread after every applied batch.</summary>
    public event EventHandler<MarkdownSnapshot>? SnapshotChanged;

    public MarkdownOptions Options { get; }

    /// <summary>The most recently published snapshot. Safe to read from any thread.</summary>
    public MarkdownSnapshot Snapshot => Volatile.Read(ref _snapshot);

    /// <summary>True once <see cref="Complete"/> has been applied.</summary>
    public bool IsCompleted => _completed;

    /// <summary>Appends a chunk of streamed markdown.</summary>
    public void Append(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        Enqueue(new Command(CommandKind.Append, text));
    }

    /// <summary>Appends a chunk of streamed markdown.</summary>
    public void Append(ReadOnlySpan<char> text)
    {
        if (!text.IsEmpty)
        {
            Enqueue(new Command(CommandKind.Append, text.ToString()));
        }
    }

    /// <summary>Replaces the whole document with <paramref name="text"/>.</summary>
    public void Replace(string text)
    {
        Enqueue(new Command(CommandKind.Replace, text));
    }

    /// <summary>Removes all content but keeps buffers allocated.</summary>
    public void Clear() => Enqueue(new Command(CommandKind.Clear, null));

    /// <summary>Equivalent to <see cref="Clear"/>; provided for API symmetry.</summary>
    public void Reset() => Enqueue(new Command(CommandKind.Clear, null));

    /// <summary>
    /// Marks the end of the stream: the trailing partial line is committed and every open block
    /// is closed. Named <c>Complete</c> because <c>Finalize</c> is reserved by the CLR.
    /// </summary>
    public void Complete() => Enqueue(new Command(CommandKind.Complete, null));

    /// <summary>Returns the full source text. Blocks until the queue drains.</summary>
    public string GetText()
    {
        WaitForIdleAsync().GetAwaiter().GetResult();
        lock (_gate)
        {
            return _buffer.Substring(0, _buffer.Length);
        }
    }

    /// <summary>Completes once every queued command has been applied.</summary>
    public Task WaitForIdleAsync()
    {
        lock (_gate)
        {
            if (!_running && _queue.Count == 0)
            {
                return Task.CompletedTask;
            }

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _idleWaiters.Add(tcs);
            return tcs.Task;
        }
    }

    // ------------------------------------------------------------------
    // Scheduling
    // ------------------------------------------------------------------

    private void Enqueue(Command command)
    {
        if (_mode == MarkdownProcessingMode.Inline)
        {
            lock (_gate)
            {
                _queue.Enqueue(command);
            }

            Pump();
            return;
        }

        lock (_gate)
        {
            _queue.Enqueue(command);
            if (_running)
            {
                return;
            }

            _running = true;
        }

        ThreadPool.UnsafeQueueUserWorkItem(static state => ((MarkdownDocument)state!).Pump(), this);
    }

    private void Pump()
    {
        while (true)
        {
            MarkdownSnapshot previous = Snapshot;
            MarkdownSnapshot snapshot;

            lock (_gate)
            {
                if (_queue.Count == 0)
                {
                    _running = false;
                    ReleaseIdleWaiters();
                    return;
                }

                snapshot = Apply(DequeueBatched());
                Volatile.Write(ref _snapshot, snapshot);
            }

            if (!ReferenceEquals(snapshot, previous))
            {
                SnapshotChanged?.Invoke(this, snapshot);
            }
        }
    }

    /// <summary>
    /// Takes the next command, merging any immediately following appends into it. A burst of
    /// streamed tokens therefore costs one parse cycle, one flatten and one snapshot instead of
    /// one of each per token. Must be called under <see cref="_gate"/>.
    /// </summary>
    private Command DequeueBatched()
    {
        Command command = _queue.Dequeue();

        if (command.Kind is not (CommandKind.Append or CommandKind.Replace) ||
            _queue.Count == 0 ||
            _queue.Peek().Kind != CommandKind.Append)
        {
            return command;
        }

        _coalesceBuffer.Clear();
        _coalesceBuffer.Append(command.Text);

        while (_queue.Count > 0 && _queue.Peek().Kind == CommandKind.Append)
        {
            _coalesceBuffer.Append(_queue.Dequeue().Text);
        }

        return new Command(command.Kind, _coalesceBuffer.ToString());
    }

    private void ReleaseIdleWaiters()
    {
        if (_idleWaiters.Count == 0)
        {
            return;
        }

        foreach (TaskCompletionSource waiter in _idleWaiters)
        {
            waiter.TrySetResult();
        }

        _idleWaiters.Clear();
    }

    // ------------------------------------------------------------------
    // Pipeline
    // ------------------------------------------------------------------

    private MarkdownSnapshot Apply(Command command)
    {
        switch (command.Kind)
        {
            case CommandKind.Clear:
                ResetCore();
                return _flattener.Flatten(_parser.Root, promote: true);

            case CommandKind.Replace:
                ResetCore();
                return AppendCore(command.Text!);

            case CommandKind.Append:
                return AppendCore(command.Text!);

            case CommandKind.Complete:
                return CompleteCore();

            default:
                return Snapshot;
        }
    }

    private void ResetCore()
    {
        _buffer.Clear();
        _parser.Reset();
        _flattener.Reset();
        _lineStart = 0;
        _scanFrom = 0;
        _completed = false;
    }

    private MarkdownSnapshot AppendCore(string text)
    {
        _parser.BeginAppendCycle();
        _buffer.Append(text);

        int committedLines = 0;
        while (true)
        {
            int newline = _buffer.IndexOf('\n', _scanFrom);
            if (newline < 0)
            {
                break;
            }

            int end = newline;
            if (end > _lineStart && _buffer[end - 1] == '\r')
            {
                end--;
            }

            _parser.ProcessCommittedLine(new SourceSpan(_lineStart, end - _lineStart));
            committedLines++;

            _lineStart = newline + 1;
            _scanFrom = _lineStart;
        }

        _scanFrom = _buffer.Length;

        MarkdownSnapshot snapshot;
        if (committedLines > 0)
        {
            snapshot = _flattener.Flatten(_parser.Root, promote: true);
        }
        else
        {
            snapshot = Snapshot;
        }

        int tailLength = _buffer.Length - _lineStart;
        if (tailLength > 0 && _buffer[_buffer.Length - 1] == '\r')
        {
            tailLength--;
        }

        if (tailLength > 0)
        {
            _parser.ProcessSpeculativeLine(new SourceSpan(_lineStart, tailLength));
            snapshot = _flattener.Flatten(_parser.Root, promote: false);
        }
        else if (committedLines == 0)
        {
            snapshot = _flattener.Flatten(_parser.Root, promote: true);
        }

        _parser.EndAppendCycle();
        return snapshot;
    }

    private MarkdownSnapshot CompleteCore()
    {
        _parser.BeginAppendCycle();

        int tailLength = _buffer.Length - _lineStart;
        if (tailLength > 0 && _buffer[_buffer.Length - 1] == '\r')
        {
            tailLength--;
        }

        if (tailLength > 0)
        {
            _parser.ProcessCommittedLine(new SourceSpan(_lineStart, tailLength));
            _lineStart = _buffer.Length;
            _scanFrom = _lineStart;
        }

        _parser.CloseAll();
        _parser.EndAppendCycle();
        _completed = true;

        return _flattener.Flatten(_parser.Root, promote: true);
    }

    private enum CommandKind
    {
        Append,
        Replace,
        Clear,
        Complete,
    }

    private readonly record struct Command(CommandKind Kind, string? Text);
}
