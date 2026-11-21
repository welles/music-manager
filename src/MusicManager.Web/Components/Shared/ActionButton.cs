using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;

// The buttons swap their own parameters for the duration of a render (see BuildRenderTree).
#pragma warning disable BL0005

namespace MusicManager.Web.Components.Shared;

/// <summary>
/// Shared busy handling of <see cref="ActionButton"/> and <see cref="ActionIconButton"/>: buttons that start
/// work on the server. While the click handler runs, the button is disabled and shows a spinner instead of its
/// icon, so a slow action (MusicBrainz, file writes) can't be clicked twice and visibly is in progress.
/// </summary>
/// <remarks>
/// The rendered class always contains <c>mm-action</c>: <c>wwwroot/js/action.js</c> greys such a button out
/// in the browser on click, before the server has even received the event. The server's busy render changes
/// the class attribute (<c>mm-busy</c>) and with it drops the browser-side marker again.
/// </remarks>
internal static class ActionBusy
{
    public const string ActionClass = "mm-action";
    public const string BusyClass = "mm-busy";

    /// <summary>SVG content for MudIcon: a ring segment that spins (SMIL, no CSS needed).</summary>
    public const string Spinner =
        "<circle cx=\"12\" cy=\"12\" r=\"9\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2.5\" " +
        "stroke-linecap=\"round\" stroke-dasharray=\"40 100\">" +
        "<animateTransform attributeName=\"transform\" type=\"rotate\" from=\"0 12 12\" to=\"360 12 12\" " +
        "dur=\"0.8s\" repeatCount=\"indefinite\"/></circle>";

    public static string Classes(string? cls, bool busy) =>
        $"{cls} {ActionClass}{(busy ? " " + BusyClass : "")}".TrimStart();

    /// <summary>Runs <paramref name="click"/> with the busy state shown before the handler's work starts.</summary>
    public static async Task RunAsync(Func<bool> isBusy, Action<bool> setBusy, Action render, Func<Task> click)
    {
        if (isBusy()) return;
        setBusy(true);
        render();
        // Return to the dispatcher so the busy state goes out to the browser before the handler starts,
        // even if the handler does synchronous work before its first await.
        await Task.Yield();
        try
        {
            await click();
        }
        finally
        {
            setBusy(false);
            render();
        }
    }
}

/// <summary>A <see cref="MudButton"/> for actions on the server: disabled with a spinner while <c>OnClick</c> runs.</summary>
public class ActionButton : MudButton
{
    private bool _busy;

    protected override Task OnClickHandler(MouseEventArgs ev) =>
        ActionBusy.RunAsync(() => _busy, b => _busy = b, StateHasChanged, () => base.OnClickHandler(ev));

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        // Rendering is synchronous: swap the parameters for this render only and restore them afterwards.
        var (disabled, cls, icon) = (Disabled, Class, StartIcon);
        Class = ActionBusy.Classes(cls, _busy);
        if (_busy)
        {
            Disabled = true;
            StartIcon = ActionBusy.Spinner;
        }
        try
        {
            base.BuildRenderTree(builder);
        }
        finally
        {
            (Disabled, Class, StartIcon) = (disabled, cls, icon);
        }
    }
}

/// <summary>A <see cref="MudIconButton"/> for actions on the server: disabled with a spinner while <c>OnClick</c> runs.</summary>
public class ActionIconButton : MudIconButton
{
    private bool _busy;

    protected override Task OnClickHandler(MouseEventArgs ev) =>
        ActionBusy.RunAsync(() => _busy, b => _busy = b, StateHasChanged, () => base.OnClickHandler(ev));

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        var (disabled, cls, icon) = (Disabled, Class, Icon);
        Class = ActionBusy.Classes(cls, _busy);
        if (_busy)
        {
            Disabled = true;
            Icon = ActionBusy.Spinner;
        }
        try
        {
            base.BuildRenderTree(builder);
        }
        finally
        {
            (Disabled, Class, Icon) = (disabled, cls, icon);
        }
    }
}
