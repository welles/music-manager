using Microsoft.AspNetCore.Components;
using MusicManager.Core.Pipeline;

namespace MusicManager.Web.Components.Shared;

/// <summary>Base class for pages that reload when the pipeline reports changes (throttled).</summary>
public abstract class LivePage : ComponentBase, IDisposable
{
    [Inject] protected PipelineNotifier Notifier { get; set; } = null!;

    private DateTime _lastReload = DateTime.MinValue;
    private bool _reloadPending;

    protected override async Task OnInitializedAsync()
    {
        Notifier.Changed += OnChanged;
        await ReloadAsync();
    }

    protected abstract Task ReloadAsync();

    private async void OnChanged()
    {
        if (_reloadPending) return;
        _reloadPending = true;
        var wait = _lastReload.AddSeconds(1) - DateTime.UtcNow;
        if (wait > TimeSpan.Zero) await Task.Delay(wait);
        _reloadPending = false;
        _lastReload = DateTime.UtcNow;
        await InvokeAsync(async () =>
        {
            await ReloadAsync();
            StateHasChanged();
        });
    }

    public virtual void Dispose()
    {
        Notifier.Changed -= OnChanged;
        GC.SuppressFinalize(this);
    }
}
