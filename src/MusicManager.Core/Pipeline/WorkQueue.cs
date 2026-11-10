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

    public void SetActivity(string? activity)
    {
        CurrentActivity = activity;
        Notify();
    }

    public void Notify() => Changed?.Invoke();
}
