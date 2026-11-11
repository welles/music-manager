using Microsoft.AspNetCore.Components;
using MusicManager.Core.Pipeline;

namespace MusicManager.Web.Components.Shared;

/// <summary>Base class for pages that reload when the pipeline reports changes (throttled).</summary>
public abstract class LivePage : ComponentBase, IDisposable
{
    [Inject] protected PipelineNotifier Notifier { get; set; } = null!;

    private DateTime _lastReload = DateTime.MinValue;
    private bool _reloadPending;
    private bool _subscribed;
    private volatile bool _disposed;
    private long _loadedVersion;

    protected override async Task OnInitializedAsync()
    {
        _loadedVersion = Notifier.Version;
        await ReloadAsync();
    }

    protected override void OnAfterRender(bool firstRender)
    {
        // Only interactive renders subscribe; prerendered instances are thrown away.
        if (firstRender && !_subscribed)
        {
            Notifier.Changed += OnChanged;
            _subscribed = true;
            // Something finished between loading and subscribing (e.g. a short job started on the previous page).
            if (Notifier.Version != _loadedVersion) OnChanged();
        }
    }

    protected abstract Task ReloadAsync();

    private async void OnChanged()
    {
        // async void: must never throw, otherwise the whole process goes down.
        try
        {
            if (_reloadPending || _disposed) return;
            _reloadPending = true;
            var wait = _lastReload.AddSeconds(1) - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait);
            _reloadPending = false;
            _lastReload = DateTime.UtcNow;
            if (_disposed) return;
            await InvokeAsync(async () =>
            {
                await ReloadAsync();
                StateHasChanged();
            });
        }
        catch
        {
            // The circuit is gone or the page was disposed in the meantime.
        }
    }

    public virtual void Dispose()
    {
        _disposed = true;
        if (_subscribed) Notifier.Changed -= OnChanged;
        GC.SuppressFinalize(this);
    }
}
