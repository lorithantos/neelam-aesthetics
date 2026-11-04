using Microsoft.AspNetCore.Components;

namespace Neelam.Web;

/// <summary>
/// Fields a page holds, such as an offer's or a photo's, that tell the page of every change made in them.
/// Blazor draws again only the component that handled an event, so without this the page around them (its
/// Still to do, its checks, its preview) would not follow what is typed until something on the page itself
/// changed. Fields inside fields (a tier's benefits) tell the ones around them, which draw again and tell
/// the page. One copy since 2026-11-03; the offer, promotion, benefit and photo fields each had their own.
/// </summary>
public abstract class FieldsComponent : ComponentBase, IHandleEvent
{
    /// <summary>Told after every change here, so what is around it follows what is typed.</summary>
    [Parameter]
    public EventCallback Changed { get; set; }

    async Task IHandleEvent.HandleEventAsync(EventCallbackWorkItem callback, object? arg)
    {
        await callback.InvokeAsync(arg);
        StateHasChanged();
        await Changed.InvokeAsync();
    }
}
