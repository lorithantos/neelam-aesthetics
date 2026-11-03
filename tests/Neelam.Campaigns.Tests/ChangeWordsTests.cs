using Neelam.Campaigns.Storage;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// Every sentence the pages say when storage changed under them, pinned word for word as the owner
/// approved them on 2026-10-11 (WIP "Concurrent edits"), for every subject and every change. Written
/// first against the three copies the frames were in (DraftSession, TemplateSession, HeldDocument) and
/// passing there, then pointed at <see cref="ChangeWords"/> unchanged.
/// </summary>
public class ChangeWordsTests
{
    private const string Campaign = "campaign";
    private const string Template = "template";

    private static ChangeWords Of(string subject) => subject switch
    {
        Campaign => ChangeWords.Campaign,
        Template => ChangeWords.Template,
        _ => ChangeWords.For(subject),
    };

    private static string? What(string subject, CampaignChange change) => Of(subject).WhatChanged(change);

    private static string Notice(string subject, CampaignChange change) => Of(subject).Notice(change);

    private static string NotUndone(string subject, CampaignChange change) => Of(subject).NotUndone(change);

    // The question before an undo, built once for every page: the campaign's and the template's word for
    // word as the campaign's was, the look's as the Look page wrote it.
    [Theory]
    [InlineData(Campaign, false, "Undo the last save? The version before it comes back, and any changes not saved are lost. The undone version can be restored for a day, then it is deleted for good.")]
    [InlineData(Campaign, true, "This deletes the campaign. You can restore it for a day.")]
    [InlineData(Template, false, "Undo the last save? The version before it comes back, and any changes not saved are lost. The undone version can be restored for a day, then it is deleted for good.")]
    [InlineData(Template, true, "This deletes the template. You can restore it for a day.")]
    [InlineData(WatchSubject.Look, false, "Undo the last save? Your pages go back to the look before it. The undone look can be restored for a day, then it is deleted for good.")]
    public void The_undo_question_says_what_comes_back_and_for_how_long(string subject, bool deletes, string question) =>
        Assert.Equal(question, Of(subject).UndoQuestion(TimeSpan.FromDays(1), deletes));

    [Theory]
    [InlineData(WatchSubject.Baseline)]
    [InlineData(WatchSubject.Ladders)]
    public void A_document_with_no_undo_has_no_undo_question(string subject) =>
        Assert.Throws<InvalidOperationException>(() => Of(subject).UndoQuestion(TimeSpan.FromDays(1)));

    [Theory]
    [InlineData(Campaign, CampaignChange.SavedSince, "the campaign was saved again since you opened it")]
    [InlineData(Campaign, CampaignChange.Undone, "the version on this page was undone since you opened it")]
    [InlineData(Campaign, CampaignChange.Deleted, "the campaign was deleted since you opened it")]
    [InlineData(Campaign, CampaignChange.ApprovalWithdrawn, "its approval was withdrawn since you opened it")]
    [InlineData(Campaign, CampaignChange.Approved, "its approval changed since you opened it")]
    [InlineData(Campaign, CampaignChange.None, null)]
    [InlineData(Template, CampaignChange.SavedSince, "the template was saved again since you opened it")]
    [InlineData(Template, CampaignChange.Undone, "the version on this page was undone since you opened it")]
    [InlineData(Template, CampaignChange.Deleted, "the template was deleted since you opened it")]
    [InlineData(Template, CampaignChange.ApprovalWithdrawn, null)]
    [InlineData(Template, CampaignChange.Approved, null)]
    [InlineData(Template, CampaignChange.None, null)]
    [InlineData(WatchSubject.Look, CampaignChange.SavedSince, "your look was changed since you opened this page")]
    [InlineData(WatchSubject.Look, CampaignChange.Undone, "the look on this page was undone since you opened it")]
    [InlineData(WatchSubject.Look, CampaignChange.Deleted, "your look was changed since you opened this page")]
    [InlineData(WatchSubject.Look, CampaignChange.None, null)]
    [InlineData(WatchSubject.Baseline, CampaignChange.SavedSince, "the parts every template should have were changed since you opened this page")]
    [InlineData(WatchSubject.Baseline, CampaignChange.Undone, "the parts every template should have were changed since you opened this page")]
    [InlineData(WatchSubject.Baseline, CampaignChange.None, null)]
    [InlineData(WatchSubject.Ladders, CampaignChange.SavedSince, "your tier name ladders were changed since you opened this page")]
    [InlineData(WatchSubject.Ladders, CampaignChange.Undone, "your tier name ladders were changed since you opened this page")]
    [InlineData(WatchSubject.Ladders, CampaignChange.None, null)]
    public void What_changed_finishes_a_sentence(string subject, CampaignChange change, string? words) =>
        Assert.Equal(words, What(subject, change));

    [Theory]
    [InlineData(Campaign, CampaignChange.SavedSince, "The campaign was saved again since you opened it, in another tab or by someone else.")]
    [InlineData(Campaign, CampaignChange.Undone, "The version on this page was undone since you opened it, in another tab or by someone else.")]
    [InlineData(Campaign, CampaignChange.Deleted, "The campaign was deleted since you opened it, in another tab or by someone else. You can restore it from Recently deleted in your campaigns.")]
    [InlineData(Campaign, CampaignChange.ApprovalWithdrawn, "Its approval was withdrawn since you opened it, in another tab or by someone else.")]
    [InlineData(Campaign, CampaignChange.Approved, "Its approval changed since you opened it, in another tab or by someone else.")]
    [InlineData(Template, CampaignChange.SavedSince, "The template was saved again since you opened it, in another tab or by someone else.")]
    [InlineData(Template, CampaignChange.Undone, "The version on this page was undone since you opened it, in another tab or by someone else.")]
    [InlineData(Template, CampaignChange.Deleted, "The template was deleted since you opened it, in another tab or by someone else. Load the latest to restore it.")]
    [InlineData(WatchSubject.Look, CampaignChange.SavedSince, "Your look was changed since you opened this page, in another tab or by someone else.")]
    [InlineData(WatchSubject.Look, CampaignChange.Undone, "The look on this page was undone since you opened it, in another tab or by someone else.")]
    [InlineData(WatchSubject.Baseline, CampaignChange.SavedSince, "The parts every template should have were changed since you opened this page, in another tab or by someone else.")]
    [InlineData(WatchSubject.Baseline, CampaignChange.Undone, "The parts every template should have were changed since you opened this page, in another tab or by someone else.")]
    [InlineData(WatchSubject.Ladders, CampaignChange.SavedSince, "Your tier name ladders were changed since you opened this page, in another tab or by someone else.")]
    [InlineData(WatchSubject.Ladders, CampaignChange.Undone, "Your tier name ladders were changed since you opened this page, in another tab or by someone else.")]
    public void The_notice_says_what_changed_and_who_may_have(string subject, CampaignChange change, string notice) =>
        Assert.Equal(notice, Notice(subject, change));

    [Theory]
    [InlineData(Campaign)]
    [InlineData(Template)]
    [InlineData(WatchSubject.Look)]
    [InlineData(WatchSubject.Baseline)]
    [InlineData(WatchSubject.Ladders)]
    public void Nothing_changed_has_no_notice(string subject) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Notice(subject, CampaignChange.None));

    [Fact]
    public void A_subject_that_is_not_one_of_hers_has_no_words() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ChangeWords.For("palette"));

    [Theory]
    [InlineData(Campaign, CampaignChange.SavedSince, "Not undone: the campaign was saved again since you opened it. Undo takes back only the newest version, so load the latest above first.")]
    [InlineData(Campaign, CampaignChange.Undone, "Not undone: the version on this page was undone since you opened it. Undo takes back only the newest version, so load the latest above first.")]
    [InlineData(Campaign, CampaignChange.Deleted, "Not undone: the campaign was deleted since you opened it. Undo takes back only the newest version, so load the latest above first.")]
    [InlineData(Template, CampaignChange.SavedSince, "Not undone: the template was saved again since you opened it. Undo takes back only the newest version, so load the latest above first.")]
    [InlineData(Template, CampaignChange.Undone, "Not undone: the version on this page was undone since you opened it. Undo takes back only the newest version, so load the latest above first.")]
    [InlineData(Template, CampaignChange.Deleted, "Not undone: the template was deleted since you opened it. Undo takes back only the newest version, so load the latest above first.")]
    [InlineData(WatchSubject.Look, CampaignChange.SavedSince, "Not undone: your look was changed since you opened this page. Undo takes back only the newest version, so load the latest above first.")]
    [InlineData(WatchSubject.Look, CampaignChange.Undone, "Not undone: the look on this page was undone since you opened it. Undo takes back only the newest version, so load the latest above first.")]
    public void An_undo_refused_says_why(string subject, CampaignChange change, string words) =>
        Assert.Equal(words, NotUndone(subject, change));

    [Theory]
    [InlineData(WatchSubject.Look, CampaignChange.SavedSince, "Not saved: your look was changed since you opened this page.")]
    [InlineData(WatchSubject.Baseline, CampaignChange.SavedSince, "Not saved: the parts every template should have were changed since you opened this page.")]
    [InlineData(WatchSubject.Baseline, CampaignChange.Undone, "Not saved: the parts every template should have were changed since you opened this page.")]
    [InlineData(WatchSubject.Ladders, CampaignChange.SavedSince, "Not saved: your tier name ladders were changed since you opened this page.")]
    [InlineData(WatchSubject.Ladders, CampaignChange.Undone, "Not saved: your tier name ladders were changed since you opened this page.")]
    public void A_save_refused_says_why(string subject, CampaignChange change, string words) =>
        Assert.Equal(words, ChangeWords.For(subject).NotSaved(change));

    [Theory]
    [InlineData(CampaignChange.SavedSince, "Not ready to copy into Square: the campaign was saved again since you opened it. Load the latest above, and copy from it once it is approved.")]
    [InlineData(CampaignChange.Undone, "Not ready to copy into Square: the version on this page was undone since you opened it. Load the latest above, and copy from it once it is approved.")]
    [InlineData(CampaignChange.Deleted, "Not ready to copy into Square: the campaign was deleted since you opened it. Load the latest above, and copy from it once it is approved.")]
    [InlineData(CampaignChange.ApprovalWithdrawn, "Not ready to copy into Square: its approval was withdrawn since you opened it. Load the latest above, and copy from it once it is approved.")]
    [InlineData(CampaignChange.Approved, "Not ready to copy into Square: its approval changed since you opened it. Load the latest above, and copy from it once it is approved.")]
    public void The_export_refused_says_why(CampaignChange change, string words) =>
        Assert.Equal(words, ChangeWords.Campaign.NotExported(change));

    [Theory]
    [InlineData(CampaignChange.SavedSince, "Not approved: the campaign was saved again since you opened it. Load the latest above, check it, then approve it.")]
    [InlineData(CampaignChange.Undone, "Not approved: the version on this page was undone since you opened it. Load the latest above, check it, then approve it.")]
    [InlineData(CampaignChange.Deleted, "Not approved: the campaign was deleted since you opened it. Load the latest above, check it, then approve it.")]
    [InlineData(CampaignChange.ApprovalWithdrawn, "Not approved: its approval was withdrawn since you opened it. Load the latest above, check it, then approve it.")]
    [InlineData(CampaignChange.Approved, "Not approved: its approval changed since you opened it. Load the latest above, check it, then approve it.")]
    public void An_approval_refused_says_why(CampaignChange change, string words) =>
        Assert.Equal(words, ChangeWords.Campaign.NotApproved(change));
}
