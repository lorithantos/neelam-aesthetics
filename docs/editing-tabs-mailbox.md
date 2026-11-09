# EditingTabs: a mailbox in place of the `_evaluating` lock

Design for the owner, 2026-11-08. Nothing is built. The open questions are at the end.
Where a section below follows a recommended answer to one of those questions, it says so.

## 1. Callers and contexts (RazorGraph `neelam`, built 21:01Z)

| Member | Production caller | Context | Needs the result? |
|---|---|---|---|
| `Register` | `EditingTabNotice.Open` only, from `Look.OnAfterRender` and `TemplateEdit.OnAfterRender` (both sync), TemplateEdit's `HandleHerEventAsync` `then` (an `Action`), and `CampaignEdit.OpenTab` (from `OnAfterRenderAsync`, `AfterActingAsync`, `StartedAsync`) | circuit | **The handle, yes** (`_tab = tabs.Register(...)`, then `_tab.OthersEditing`). The evaluation, no |
| `Typed` | `EditingTabNotice.Typed` from the same three pages and the keeper's `changed` (in `OnAfterRenderAsync`) | circuit | No |
| `Close` | `EditingTab.Dispose`, from `EditingTabNotice.Open`/`Dispose`, from each page's `Disposing()`, from sync `WatchedComponent.Dispose` | circuit, dispose path | No, and it cannot wait |
| `SetConnected` | `EditingTabsCircuitHandler.OnConnectionDown/UpAsync` | circuit handler | No. Its doc says nothing thrown reaches Blazor |
| `ApplyRemote`, `Refresh`, `Snapshot` | `EditingTabsBeat.BeatAsync` | the beat's hosted service | `Snapshot` yes. ApplyRemote then Refresh need **order** |
| `DrainClosed` | `EditingTabsBeat.DeleteClosedAsync` | the beat | Yes |
| `WaitForTabsAsync`, `IsEmpty` | `EditingTabsBeat.ExecuteAsync` | the beat | Yes. They do not evaluate |

`Program.cs:128-131, 210` registers `AddSingleton<EditingTabs>()`, `TabCircuit` and the handler scoped, and `AddHostedService<EditingTabsBeat>()`. **No production caller needs an evaluation's result.** Only tests read notices straight after a call.

## 2. What the consumer owns

**A: everything.** The loop alone touches `_tabs`, `_remote`, `_closed` and `_closedLately`. Typed's time travels in the message, so `TabState` needs no compare-and-swap. `Snapshot`, `DrainClosed` and `IsEmpty` post, or read what the loop publishes.
- Gain: the ordering subtleties in membership (closed before it leaves the open; "open first, then closed" in ApplyRemote) can no longer go wrong.
- Cost: `Snapshot`/`DrainClosed` become async (the beat can take it), `IsEmpty`/`WaitForTabsAsync` need a published count, and about 15 test sites plus `OtherTabs.AnyTyped` turn async.

**B (recommended): the notices.** The loop owns `_remote` outright, as a plain `Dictionary`, and runs every `Flipped`/`Tell`. Membership stays lock-free, exactly as now. `Snapshot`, `DrainClosed`, `IsEmpty` and `WaitForTabsAsync` do not change.

The invariant that makes B sound: **every membership change posts an item after it is made.** Only the loop writes `_remote`, so the check-then-act in Close and ApplyRemote is serial. A `HasOpen` that reads a moving `_tabs` is put right by the item the change posts. The staleness window is the same as today's, at most one `Interval`.

## 3. Ordering and coalescing

Callers rely on five orders today:
1. Typed stamps `LastTyped` before evaluating.
2. A tab is in `_tabs` before `_opened` wakes the beat.
3. A closing tab joins the closed before it leaves the open.
4. The beat runs ApplyRemote, then Refresh.
5. A tab opening while another types is worked out as it opens.

A FIFO channel keeps 1 to 4: they happen on the caller, or come from one poster. It keeps 5 by order: whichever of the two items runs second sees both the stamp and the new tab.

Tells all come from the loop, in order, before the item's Task completes. Today they can arrive out of order, which is harmless only because a page reads the latest value when it renders.

**Coalescing: batch on dequeue (recommended).**
1. Each wake, the loop reads and claims every waiting item.
2. It applies their state steps (`_remote` set or dropped) in FIFO order.
3. It runs one evaluation over the union of their scopes. A Refresh in the batch means all tabs. A subject keeps its `except` tab only if every item for it names the same one.
4. It tells the tabs whose notice differs from before the batch.
5. It completes every item's Task.

The latest keystroke cannot be lost. Typed stamps on the caller before it posts, and the channel write happens before the loop's read. A flip and a flip back in one batch tell no one, which is right.

Joining an unclaimed item at post time would need a stamp-then-check race and buys nothing more. Honest evidence: an evaluation is microseconds over a few tabs, so a batch larger than one will be rare.

## 4. Signatures

**S1: async.** `RegisterAsync` makes `EditingTabNotice.Open` async, and the change spreads:
- `Look` and `TemplateEdit` move to `OnAfterRenderAsync`.
- `WatchedComponent.HandleHerEventAsync`'s `then` becomes `Func<Task>`. Its synchronous path would have to await, which changes the "drawn exactly as ComponentBase draws" timing that `HerEventTests` pin.
- `CampaignEdit.OpenTab` changes.
- Close needs `IAsyncDisposable` through `WatchedComponent.Dispose`.
- An async Open yields on the circuit, so two renders could register two tabs and leak one.

**S2 (recommended): synchronous call shape.**
- `Register` puts the tab in, posts its evaluation and returns the handle.
- `Typed`, `SetConnected`, `Refresh` and `ApplyRemote` return the item's Task. Callers that ignore it write `_ = ...`, which also avoids CS4014 in async page methods.
- Dispose stays `void`.
- For tests: an internal `EditingTab.Opened` Task and an internal `EditingTabs.SettledAsync()`, which completes once everything posted before it has run.
- The circuit handler keeps returning `Task.CompletedTask`, so a cancelled Task never reaches Blazor.
- The beat awaits its Tasks.

"May be told before this returns" becomes "worked out by the evaluator as soon as it is in". Two things rely on the old wording:
- `EditingTabNotice.Open` lines 39-40 redraw when the tab was told before the page held it. That happens today because `InvokeAsync` runs inline on the circuit. A tell from the loop's pool thread queues behind `Open` instead, so the redraw becomes redundant (harmless, and removable).
- The test `A_tab_told_as_it_opens_is_drawn_again_once_the_page_holds_it` is rewritten.

The channel takes `SingleReader = true, AllowSynchronousContinuations = false`, as `StartLog`'s does (`StartRecording.cs:286-289`, the in-repo template for this loop). Otherwise a post could run the loop inline on a circuit and bring that re-entrancy back.

## 5. Items, cancellation, failure, lifetime

```csharp
sealed class Item(Scope scope, Action? step, CancellationToken token)
{
    int _state;  // 0 waiting, 1 claimed, 2 cancelled
    public readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool TryClaim() => Interlocked.CompareExchange(ref _state, 1, 0) == 0;
    public void TryCancel() { if (Interlocked.CompareExchange(ref _state, 2, 0) == 0) Done.TrySetCanceled(token); }
}
```

**Cancellation.** A token that has already fired is refused without posting. Otherwise `token.Register(item.TryCancel)`, disposed when the item completes. The loop skips any item it cannot claim, and once claimed the item reports what happened. Under B only the beat's posts take a token (ApplyRemote, Refresh), and a cancel before the claim is a true "did not happen". Typed, SetConnected, Register and Close change state on the caller before they post, so a token could only skip the re-evaluation and leave a notice stale: **they take none**. Under A every post could take one truthfully, the one place where A fits "takes a token" better.

ImageSelectorV2's `ImageFetchDispatcher` has the same per-item completion source, but its token cancels only the enqueue, and its `StopAsync` leaves queued callers waiting forever. Neither is copied here.

**Failure** (following the recommended answer to question 7).
- Each batch runs in a try/catch. A failure is logged as a Warning by type only, faults that batch's Tasks, and the loop goes on. `Tell` keeps its per-callback catch.
- Only a bug outside that catch ends the loop. It then logs an Error, calls `TryComplete(ex)` on that loop's writer, drains with `TryRead` and faults every pending item, so nothing waits forever.
- The next post builds a fresh loop, as JanetHome's `WriteQueue` does when a lane dies: the dead lane unregisters itself (by value, so it never removes a replacement), and the next write gets a new one (`C:\repos\lorithantos\JanetHome\src\Janet.Core\WriteQueue.cs:256-261`). The fresh loop starts from the current membership, and its first item re-evaluates every tab.
- Past a few deaths in a short window it stops rebuilding, logs Critical, and turns notices off: posts complete at once and tell no one. The 412 backstop on save remains.
- Fire-and-forget posts carry no completion source, so nothing goes unobserved.

**Lifetime.**
- The loop starts in the constructor (`Task.Run`, no captured context). The 6 test constructions have no host, and a parked async loop holds no thread.
- `EditingTabs` becomes `IAsyncDisposable`. The container disposes it after the hosted services (the beat) stop: it completes the writer, cancels unclaimed items and awaits the loop.
- A page disposed later in shutdown gets a cancelled Task, which is true, and never throws.

**No post from inside the loop** (following the recommended answer to question 8). `Tell` runs on the loop, so a callback that waits on a post deadlocks it. The contract already forbids blocking ("hand the news on and return"), and the production callback only queues `InvokeAsync`, which runs later elsewhere. The loop sets a flag only around its synchronous batch, and clears it before any await. While it is set, Post throws instead of queueing, as `WriteQueue.Submit` refuses a write from inside a queued write (`WriteQueue.cs:107-114`). `A_tab_is_told_outside_any_lock` does exactly what this refuses (`Task.Run(Refresh).Wait()`). It becomes "a tab told that posts is refused".

## 6. Tests

Awaiting the returned Task replaces every read-after-call, and no `Eventually` is added. No test calls `.Wait()` on a post. Under B:
- `EditingTabsTests`: about 17 of 23 become async. Unchanged: `Opening_gives_...`, `Typing_marks_the_time`, `The_beat_sees_...`, `Waiting_for_tabs_...`, `A_tab_the_beat_took_as_closed_...`, `The_settings_...`. Rewritten: `A_tab_is_told_outside_any_lock`.
- `EditingTabNoticeTests`: 3 of 4. `EditingTabNotice.Typed` returns the Task.
- `EditingTabsCircuitHandlerTests`: 1 of 2.
- `EditingTabsBeatTests`: about 2 of 9 (`Every_notice_is_worked_out_again_...` asserts right after `b.Typed()`). The rest already wait for a finished beat.
- `EditingTabPageTests` and `WorkingCopyPageTests`: none. They wait for the rendered notice, and `LastTyped` is stamped on the caller.
- About 7 new tests:
  - a burst is one evaluation with the newest `LastTyped`;
  - a cancel before the claim;
  - a cancel after it;
  - a dead loop fails what it held and the next post is served by a fresh one;
  - repeated deaths stop the rebuilds and turn notices off;
  - a post after disposal;
  - a tell that posts is refused.

That is **about 23 reshaped and 7 new**. A adds about 15 Snapshot/DrainClosed sites.

## 7. A reusable `Mailbox<TState>`?

Not now: there is no second user. The lock survey found `_evaluating` the only candidate, and `StartLog`'s channel is a fire-and-forget event queue with no replies, claim or token. Write it inside `EditingTabs`, about 60 private lines (a little more with the rebuild). Lift it into `Neelam.Web` when a second user appears, shaped by both.

## 8. The parked thread item

"Editor actions that fail after their storage write went through say it did not happen" shares this design's **principle**: past its commit point, an operation reports what happened. It does not share the **mechanism**. Those actions are not posted anywhere. Their commit point is a write in the middle of an action (`UndoLastSaveAsync` then `ReopenAsync`; `SaveAsync` then `KeepApprovalAsync`; `RestoreAsync` then `OpenAsync`), and their failure is an exception in the re-read, not a token. So the rule is stated once and applied twice, with no shared type.

## 9. Risks, and when to keep the lock

- **The gain is modest.** The lock is correct today, never held over I/O, and covers microseconds of work across a few tabs. The mailbox buys in-order tells, no circuit waiting on the beat's evaluation, awaitable tests, free coalescing and a `_remote` nothing else can touch. It is a clarity and testability change, not a fix.
- **New failure modes.** The loop can die; it is rebuilt, and only repeated deaths turn the notices off (Critical logged, 412 backstop remains). A callback that posts is refused rather than deadlocking. Both are handled and both are tested.
- **About 30 tests touched.** Flaky tests are pri-1, so every wait is an awaited Task, never a poll.
- Register no longer works the notice out before it returns. Nothing in production relies on that.

**Keep the lock if** the owner wants Register's synchronous working-out kept, or values 1.5 days over the clarity. Then the cheaper improvement is a lock doc comment that names the invariants.

## Open questions for the owner

1. **Which state does the loop own: A (everything) or B (notices and `_remote`)?** Recommend **B**: the same gains, the beat's reads unchanged, about half the churn. A is purer, and every token in it means something.
2. **Register and SetConnected: async (S1) or synchronous with fire-and-forget posts (S2)?** Recommend **S2**.
3. **May Register drop "may be told before this returns"?** Recommend **yes**, and remove Open's redraw-once-more.
4. **Coalesce now?** Recommend **yes, batch on dequeue** (about 20 lines, no extra race).
5. **A shared `Mailbox<TState>`?** Recommend **no**, until a second user appears.
6. **One rule for the mailbox's cancellation and the thread item?** Recommend stating "past its commit point an operation reports what happened" once in README, and implementing each separately.
7. **When the loop dies: restart, or log and stop?** (Revised 2026-11-08. This first said log and stop; the owner then called a dead loop critical.) Recommend **rebuild in process on the next post**, as JanetHome's WriteQueue does (section 5, Failure). Past a few deaths in a short window, stop rebuilding, log Critical and turn notices off, so a deterministic bug cannot flood the log. No app restart: that would cut every open circuit for one component's bug.
8. **Refuse a post made from inside the loop, by construction?** Recommend **yes** (section 5, No post from inside the loop). Production's tell only queues `InvokeAsync`, so nothing changes there. The planned "a tell that posts" test becomes "a tell that posts is refused".

## Size

B with S2: **about 1.5 LLM-assisted active days**: half a day for the rewrite and docs, half a day for the tests, and the rest for review. The rebuild and the refusal are inside that. A takes about 2 days. A general `Mailbox<TState>` adds about 0.5. The pace is set by the answers above.
