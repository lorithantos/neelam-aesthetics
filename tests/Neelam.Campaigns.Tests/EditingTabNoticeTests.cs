using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Neelam.Campaigns.Storage;
using Neelam.Web;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// One editor page's tab (<see cref="EditingTabNotice"/>): opened once the page is live, on the subject it
/// shows, moved when that changes, closed with the page, and another tab's editing drawn on the page's
/// own circuit, news arriving as the page closes dropped. The notices are worked out, and the news handed on,
/// off the test's thread: a test awaits the Task a call hands back (<see cref="EditingTabs.SettledAsync"/>
/// after a close), then the page's <see cref="EditingTabNotice.Drawn"/>, before it reads either.
/// </summary>
public class EditingTabNoticeTests
{
    private static readonly ClientName Client = TestRecords.DefaultClient;

    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 5, 9, 0, 0, TimeSpan.Zero));
    private readonly EditingTabs _tabs;
    private readonly ListLogger<EditingTabNotice> _log = new();

    public EditingTabNoticeTests() =>
        _tabs = new EditingTabs(_clock, Options.Create(new EditingTabsOptions()), new ListLogger<EditingTabs>());

    // A page whose circuit runs the work and then counts a render.
    private EditingTabNotice Notice(Action rendered) =>
        new(_tabs, new TabCircuit(), async work =>
        {
            await work();
            rendered();
        }, _log);

    private EditingTabNotice Notice(Func<Func<Task>, Task> onCircuit) => new(_tabs, new TabCircuit(), onCircuit, _log);

    [Fact]
    public void Opening_the_same_subject_again_keeps_its_tab_and_another_subject_moves_it()
    {
        var first = TabSubject.Campaign(Guid.NewGuid());
        var second = TabSubject.Campaign(Guid.NewGuid());
        var notice = Notice(() => { });

        notice.Open(Client, first);
        var tab = Assert.Single(_tabs.Snapshot());
        Assert.Equal(first, tab.Subject);
        notice.Open(Client, first);
        Assert.Equal(tab.TabId, Assert.Single(_tabs.Snapshot()).TabId);

        notice.Open(Client, second);
        var moved = Assert.Single(_tabs.Snapshot());
        Assert.Equal(second, moved.Subject);
        Assert.NotEqual(tab.TabId, moved.TabId);

        notice.Dispose();
        Assert.True(_tabs.IsEmpty);
        // Closed with its page: nothing opens it again.
        notice.Open(Client, first);
        _ = notice.Typed();
        Assert.True(_tabs.IsEmpty);
        notice.Dispose();
    }

    [Fact]
    public async Task Another_tab_s_typing_is_drawn_on_the_page_s_circuit_and_its_own_is_not()
    {
        var subject = TabSubject.Campaign(Guid.NewGuid());
        var (mineDrawn, theirsDrawn) = (0, 0);
        using var mine = Notice(() => mineDrawn++);
        using var theirs = Notice(() => theirsDrawn++);
        // Nothing typed before it opens: a page on no subject yet says nothing and reports nothing.
        await theirs.Typed();
        Assert.False(theirs.OthersEditing);
        mine.Open(Client, subject);
        theirs.Open(Client, subject);
        _clock.Now += TimeSpan.FromSeconds(1);

        await theirs.Typed();
        await mine.Drawn;
        await theirs.Drawn;

        Assert.True(mine.OthersEditing);
        Assert.Equal(1, mineDrawn);
        Assert.False(theirs.OthersEditing);
        Assert.Equal(0, theirsDrawn);

        theirs.Dispose();
        await _tabs.SettledAsync();
        await mine.Drawn;
        Assert.False(mine.OthersEditing);
        Assert.Equal(2, mineDrawn);
    }

    // A page opening on something already being edited (here by a row of another instance whose clock runs
    // ahead) is not worked out before it holds its tab: once the tab is in, it is told like any other, and
    // drawn once with the notice.
    [Fact]
    public async Task A_page_opening_on_something_already_being_edited_is_told_once_its_tab_is_in()
    {
        var subject = TabSubject.Campaign(Guid.NewGuid());
        using var here = Notice(() => { });
        here.Open(Client, subject);
        var now = _clock.GetUtcNow();
        await _tabs.ApplyRemote(Client, subject, [new RemoteTab(Guid.NewGuid(), now + TimeSpan.FromSeconds(1), now)]);
        var drawn = 0;
        using var mine = Notice(() => drawn++);

        mine.Open(Client, subject);
        await mine.Opened;
        await mine.Drawn;

        Assert.True(mine.OthersEditing);
        Assert.Equal(1, drawn);
    }

    // The news is handed on rather than drawn on the thread that worked it out, where anything the page then
    // told the tabs would be refused: a campaign page finding a change in its form at the render the news
    // brought tells its tab she is editing, and that is taken.
    [Fact]
    public async Task News_is_handed_on_so_a_page_that_tells_the_tabs_as_it_is_drawn_is_not_refused()
    {
        var subject = TabSubject.Campaign(Guid.NewGuid());
        Task? typedAsDrawn = null;
        EditingTabNotice? mine = null;
        mine = Notice(async work =>
        {
            await work();
            typedAsDrawn ??= mine!.Typed();
        });
        using var theirs = Notice(() => { });
        mine.Open(Client, subject);
        theirs.Open(Client, subject);
        _clock.Now += TimeSpan.FromSeconds(1);

        await theirs.Typed();
        await mine.Drawn;

        Assert.NotNull(typedAsDrawn);
        await typedAsDrawn;
        Assert.True(theirs.OthersEditing);
        Assert.Empty(_log.Entries);
        mine.Dispose();
    }

    [Fact]
    public async Task News_the_closing_page_cannot_take_is_dropped_and_anything_else_is_logged_by_type()
    {
        var subject = TabSubject.Campaign(Guid.NewGuid());
        Exception failure = new ObjectDisposedException("circuit");
        using var mine = Notice(_ => Task.FromException(failure));
        using var theirs = Notice(() => { });
        mine.Open(Client, subject);
        theirs.Open(Client, subject);
        _clock.Now += TimeSpan.FromSeconds(1);

        await theirs.Typed();
        await mine.Drawn;
        failure = new OperationCanceledException();
        // Quiet long enough: the notice clears, and the news of it is cancelled too.
        _clock.Now += new EditingTabsOptions().QuietAfter;
        await _tabs.Refresh();
        await mine.Drawn;
        Assert.Empty(_log.Entries);

        failure = new InvalidOperationException("Something the page shows.");
        _clock.Now += TimeSpan.FromSeconds(1);
        await theirs.Typed();
        await mine.Drawn;

        var entry = Assert.Single(_log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Null(entry.Error);
        Assert.Contains(nameof(InvalidOperationException), entry.Message);
        Assert.DoesNotContain("Something the page shows.", entry.Message);
    }
}
