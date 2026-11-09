using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Neelam.Campaigns.Storage;
using Neelam.Web;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// A dropped browser's tabs stop counting at once and count again when it comes back
/// (<see cref="EditingTabsCircuitHandler"/>), and a page and the handler of one circuit share its
/// <see cref="TabCircuit"/>.
/// </summary>
public class EditingTabsCircuitHandlerTests
{
    private static readonly ClientName Client = TestRecords.DefaultClient;
    private static readonly TabSubject Campaign = TabSubject.Campaign(Guid.NewGuid());

    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 5, 9, 0, 0, TimeSpan.Zero));
    private readonly EditingTabs _tabs;

    public EditingTabsCircuitHandlerTests() =>
        _tabs = new EditingTabs(_clock, Options.Create(new EditingTabsOptions()), new ListLogger<EditingTabs>());

    [Fact]
    public async Task A_dropped_connection_stops_its_tabs_counting_and_coming_back_counts_them_again()
    {
        var circuit = new TabCircuit();
        var typistHandler = new EditingTabsCircuitHandler(circuit, _tabs);
        var mine = _tabs.Register(Client, Campaign, Guid.NewGuid(), () => { });
        var typist = _tabs.Register(Client, Campaign, circuit.Id, () => { });
        _clock.Now += TimeSpan.FromSeconds(1);
        await typist.Typed();
        Assert.True(mine.OthersEditing);

        // The handler hands Blazor a finished Task at once, with the tabs already marked; the notices follow.
        var down = typistHandler.OnConnectionDownAsync(null!, CancellationToken.None);
        Assert.True(down.IsCompletedSuccessfully);
        Assert.False(typist.Connected);
        await _tabs.SettledAsync();
        Assert.False(mine.OthersEditing);

        var up = typistHandler.OnConnectionUpAsync(null!, CancellationToken.None);
        Assert.True(up.IsCompletedSuccessfully);
        Assert.True(typist.Connected);
        await _tabs.SettledAsync();
        Assert.True(mine.OthersEditing);
    }

    [Fact]
    public void A_scope_gives_the_page_and_the_handler_the_same_circuit_and_scopes_differ()
    {
        using var app = new DemoApp();
        using var one = app.Services.CreateScope();
        using var two = app.Services.CreateScope();

        var handler = Assert.Single(one.ServiceProvider.GetServices<CircuitHandler>().OfType<EditingTabsCircuitHandler>());
        var page = one.ServiceProvider.GetRequiredService<TabCircuit>();

        Assert.NotEqual(Guid.Empty, page.Id);
        Assert.Same(page, one.ServiceProvider.GetRequiredService<TabCircuit>());
        Assert.Equal(page.Id, handler.CircuitId);
        Assert.NotEqual(page.Id, two.ServiceProvider.GetRequiredService<TabCircuit>().Id);
    }
}
