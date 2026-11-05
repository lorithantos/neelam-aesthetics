using Microsoft.AspNetCore.Components.Server.Circuits;

namespace Neelam.Web;

/// <summary>
/// The id of the circuit a page runs on, one per circuit (a Blazor Server circuit is one DI scope). A page
/// passes <see cref="Id"/> to <see cref="EditingTabs.Register"/>, and the circuit's
/// <see cref="EditingTabsCircuitHandler"/> shares the same instance, so a dropped connection reaches the
/// tabs the page registered.
/// </summary>
public sealed class TabCircuit
{
    /// <summary>New for each circuit.</summary>
    public Guid Id { get; } = Guid.NewGuid();
}

/// <summary>
/// Tells <see cref="EditingTabs"/> when the browser behind a circuit drops and when it comes back. Blazor
/// keeps a disconnected circuit for a few minutes, so without this a reloaded page would see its own old tab
/// as another open one. Nothing on a circuit opening or closing: a tab leaves when its page disposes its
/// handle. <see cref="EditingTabs.SetConnected"/> catches errors from the tabs' callbacks, so nothing
/// thrown reaches Blazor.
/// </summary>
public sealed class EditingTabsCircuitHandler(TabCircuit circuit, EditingTabs tabs) : CircuitHandler
{
    /// <summary>The circuit this handler is for, the one its pages register their tabs on.</summary>
    public Guid CircuitId => circuit.Id;

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        tabs.SetConnected(CircuitId, false);
        return Task.CompletedTask;
    }

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        tabs.SetConnected(CircuitId, true);
        return Task.CompletedTask;
    }
}
