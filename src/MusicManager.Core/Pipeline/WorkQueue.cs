using System.Threading.Channels;

namespace MusicManager.Core.Pipeline;

public enum WorkKind
{
    /// <summary>Analyze and match all queued files of a group.</summary>
    ProcessGroup,

    /// <summary>Write and file an approved file.</summary>
    WriteFile,
}

public sealed record WorkItem(WorkKind Kind, int Id);

/// <summary>Single queue; all file system work runs sequentially in <see cref="PipelineWorker"/>.</summary>
public sealed class WorkQueue
{
    private readonly Channel<WorkItem> _channel = Channel.CreateUnbounded<WorkItem>();

    public void Enqueue(WorkItem item) => _channel.Writer.TryWrite(item);

    public IAsyncEnumerable<WorkItem> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);

    public int Count => _channel.Reader.Count;
}

/// <summary>Notifies the frontend about changes so pages refresh live.</summary>
public sealed class PipelineNotifier
{
    public event Action? Changed;

    public string? CurrentActivity { get; private set; }

    private long _version;

    /// <summary>Increases with every notification – lets pages detect changes they missed before subscribing.</summary>
    public long Version => Interlocked.Read(ref _version);

    public void SetActivity(string? activity)
    {
        CurrentActivity = activity;
        Notify();
    }

    public void Notify()
    {
        Interlocked.Increment(ref _version);
        // A failing subscriber (e.g. a disconnected page) must not break the pipeline.
        foreach (var handler in Changed?.GetInvocationList() ?? [])
        {
            try { ((Action)handler)(); }
            catch { /* ignored */ }
        }
    }
}
