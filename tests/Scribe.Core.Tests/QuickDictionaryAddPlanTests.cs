
using Scribe.Core.Models;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

public sealed class QuickDictionaryAddPlanTests
{
    [Fact]
    public void Each_plan_row_has_exact_message_save_state_severity_and_action()
    {
        AssertPlan(Request("", "Copilot"), Vocab(), QuickDictionaryAdd.PlanKind.Empty, string.Empty, false, QuickDictionaryAdd.PlanSeverity.None, QuickDictionaryAdd.PlanAction.None);
        AssertPlan(new QuickDictionaryAdd.QuickAddRequest("cloud", "Copilot", false, true, ReferencesAvailable: false), Vocab(), QuickDictionaryAdd.PlanKind.ReferencesUnavailable, "Couldn't check your dictionary. Try again.", false, QuickDictionaryAdd.PlanSeverity.Error, QuickDictionaryAdd.PlanAction.TryAgain);
        AssertPlan(Request("cloud\npilot", "Copilot"), Vocab(), QuickDictionaryAdd.PlanKind.LineBreak, "Select words from one line. A dictionary word can't span a line break.", false, QuickDictionaryAdd.PlanSeverity.Warning, QuickDictionaryAdd.PlanAction.None);
        AssertPlan(Request("Copilot", "Copilot"), Vocab(), QuickDictionaryAdd.PlanKind.SameText, $"Scribe already writes {Q("Copilot")} this way. Type what it should write instead.", false, QuickDictionaryAdd.PlanSeverity.Info, QuickDictionaryAdd.PlanAction.None);
        AssertPlan(Request("draft", "Draft"), Vocab(pending: [" DRAFT "]), QuickDictionaryAdd.PlanKind.BlockedBySettings, $"You're changing {Q("draft")} in Settings. Save or undo that change there first.", false, QuickDictionaryAdd.PlanSeverity.Warning, QuickDictionaryAdd.PlanAction.ShowInSettings);
        AssertPlan(Request("Microsoft Teams", "Teams"), Vocab(personal: [Entry("teams", "Microsoft Teams")]), QuickDictionaryAdd.PlanKind.ProducedByYourWord, $"Scribe writes {Q("Microsoft Teams")} because your dictionary changes {Q("teams")} to it. To change what it writes, fix {Q("teams")} instead.", false, QuickDictionaryAdd.PlanSeverity.Warning, QuickDictionaryAdd.PlanAction.FixInstead);
        AssertPlan(Request("Microsoft Teams", "Teams"), Vocab(packs: [Pack("Microsoft 365", "teams", "Microsoft Teams")]), QuickDictionaryAdd.PlanKind.ProducedByWordPack, $"Scribe writes {Q("Microsoft Teams")} because the {Q("Microsoft 365")} word pack changes {Q("teams")} to it. To change what it writes, fix {Q("teams")} instead.", false, QuickDictionaryAdd.PlanSeverity.Warning, QuickDictionaryAdd.PlanAction.FixInstead);
        AssertPlan(Request("cloud", ""), Vocab(personal: [Entry("cloud", "Copilot")]), QuickDictionaryAdd.PlanKind.PendingExisting, $"{Q("cloud")} is already in your dictionary: Scribe writes {Q("Copilot")}. Type something else to change it.", false, QuickDictionaryAdd.PlanSeverity.Info, QuickDictionaryAdd.PlanAction.None);
        AssertPlan(Request("um", ""), Vocab(personal: [Entry("um", "")]), QuickDictionaryAdd.PlanKind.PendingExisting, $"{Q("um")} is already in your dictionary: Scribe removes it.", false, QuickDictionaryAdd.PlanSeverity.Info, QuickDictionaryAdd.PlanAction.None);
        AssertPlan(Request("cloud", "Copilot"), Vocab(personal: [Entry("cloud", "Copilot")]), QuickDictionaryAdd.PlanKind.NoChange, $"{Q("cloud")} is already in your dictionary: Scribe writes {Q("Copilot")}.", false, QuickDictionaryAdd.PlanSeverity.Info, QuickDictionaryAdd.PlanAction.None);
        AssertPlan(new QuickDictionaryAdd.QuickAddRequest("um", "", true, true), Vocab(personal: [Entry("um", "")]), QuickDictionaryAdd.PlanKind.NoChange, $"{Q("um")} is already in your dictionary: Scribe removes it.", false, QuickDictionaryAdd.PlanSeverity.Info, QuickDictionaryAdd.PlanAction.None);
        AssertPlan(Request("cloud", "Copilot"), Vocab(personal: [Entry("cloud", "Copilot", enabled: false)]), QuickDictionaryAdd.PlanKind.UpdateTurnOn, $"You already have {Q("cloud")} in your dictionary, but it's turned off. Saving turns it back on.", true, QuickDictionaryAdd.PlanSeverity.Warning, QuickDictionaryAdd.PlanAction.None);
        AssertPlan(Request("cloud", "GitHub Copilot"), Vocab(personal: [Entry("cloud", "Copilot")]), QuickDictionaryAdd.PlanKind.UpdateReplacement, $"You already have {Q("cloud")} in your dictionary. Saving changes what Scribe writes from {Q("Copilot")} to {Q("GitHub Copilot")}.", true, QuickDictionaryAdd.PlanSeverity.Warning, QuickDictionaryAdd.PlanAction.None);
        AssertPlan(new QuickDictionaryAdd.QuickAddRequest("cloud", "", true, true), Vocab(personal: [Entry("cloud", "Copilot")]), QuickDictionaryAdd.PlanKind.UpdateToRemoval, $"You already have {Q("cloud")} in your dictionary. Saving makes Scribe remove it instead of writing {Q("Copilot")}.", true, QuickDictionaryAdd.PlanSeverity.Warning, QuickDictionaryAdd.PlanAction.None);
        AssertPlan(Request("um", "hello"), Vocab(personal: [Entry("um", "")]), QuickDictionaryAdd.PlanKind.UpdateFromRemoval, $"You already have {Q("um")} in your dictionary, set to remove it. Saving makes Scribe write {Q("hello")} instead.", true, QuickDictionaryAdd.PlanSeverity.Warning, QuickDictionaryAdd.PlanAction.None);
        AssertPlan(Request("cloud", "Copilot", wholeWord: false), Vocab(personal: [Entry("cloud", "Copilot", wholeWord: true)]), QuickDictionaryAdd.PlanKind.UpdateWholeWord, $"You already have {Q("cloud")} in your dictionary. Saving changes it to also match inside longer words.", true, QuickDictionaryAdd.PlanSeverity.Warning, QuickDictionaryAdd.PlanAction.None);
        AssertPlan(Request("cloud", "Copilot", wholeWord: true), Vocab(personal: [Entry("cloud", "Copilot", wholeWord: false)]), QuickDictionaryAdd.PlanKind.UpdateWholeWord, $"You already have {Q("cloud")} in your dictionary. Saving changes it to match whole words only.", true, QuickDictionaryAdd.PlanSeverity.Warning, QuickDictionaryAdd.PlanAction.None);
        AssertPlan(Request("cloud", ""), Vocab(packs: [Pack("AI", "cloud", "Copilot")]), QuickDictionaryAdd.PlanKind.PendingWordPack, $"The {Q("AI")} word pack writes {Q("cloud")} as {Q("Copilot")}. Type something else to use your own spelling.", false, QuickDictionaryAdd.PlanSeverity.Info, QuickDictionaryAdd.PlanAction.None);
        AssertPlan(Request("cloud", "Copilot"), Vocab(packs: [Pack("AI", "cloud", "Copilot")]), QuickDictionaryAdd.PlanKind.SameAsWordPack, $"The {Q("AI")} word pack already writes {Q("cloud")} as {Q("Copilot")}, so there's nothing to add.", false, QuickDictionaryAdd.PlanSeverity.Info, QuickDictionaryAdd.PlanAction.None);
        AssertPlan(Request("cloud", "GitHub Copilot"), Vocab(packs: [Pack("AI", "cloud", "Copilot")]), QuickDictionaryAdd.PlanKind.OverridesWordPack, $"The {Q("AI")} word pack writes {Q("cloud")} as {Q("Copilot")}. Your word will win: Scribe will write {Q("GitHub Copilot")}.", true, QuickDictionaryAdd.PlanSeverity.Info, QuickDictionaryAdd.PlanAction.None);
        AssertPlan(new QuickDictionaryAdd.QuickAddRequest("cloud", "", true, true), Vocab(packs: [Pack("AI", "cloud", "Copilot")]), QuickDictionaryAdd.PlanKind.OverridesWordPack, $"The {Q("AI")} word pack writes {Q("cloud")} as {Q("Copilot")}. Your word will win: Scribe will remove it.", true, QuickDictionaryAdd.PlanSeverity.Info, QuickDictionaryAdd.PlanAction.None);
        AssertPlan(Request("cloud", ""), Vocab(), QuickDictionaryAdd.PlanKind.Pending, $"Type what Scribe should write instead of {Q("cloud")}.", false, QuickDictionaryAdd.PlanSeverity.Info, QuickDictionaryAdd.PlanAction.None);
        AssertPlan(new QuickDictionaryAdd.QuickAddRequest("um", "", true, true), Vocab(), QuickDictionaryAdd.PlanKind.CreateRemoval, $"Scribe will remove {Q("um")} from everything you dictate.", true, QuickDictionaryAdd.PlanSeverity.Warning, QuickDictionaryAdd.PlanAction.None);
        AssertPlan(Request("cloud", "Copilot"), Vocab(), QuickDictionaryAdd.PlanKind.Create, $"Scribe will write {Q("Copilot")} when it hears {Q("cloud")}.", true, QuickDictionaryAdd.PlanSeverity.Info, QuickDictionaryAdd.PlanAction.None);
    }

    [Fact]
    public void Combined_update_sentences_are_added_in_order()
    {
        var plan = QuickDictionaryAdd.Build(Request("cloud", "GitHub Copilot", wholeWord: false), Vocab(personal: [Entry("cloud", "Copilot", wholeWord: true, enabled: false)]));

        Assert.Equal($"You already have {Q("cloud")} in your dictionary. Saving changes what Scribe writes from {Q("Copilot")} to {Q("GitHub Copilot")}. It also turns it back on. It also changes it to also match inside longer words.", plan.Message);
    }

    [Fact]
    public void Older_dictation_and_inside_longer_words_sentences_are_pinned()
    {
        var noChange = QuickDictionaryAdd.Build(new QuickDictionaryAdd.QuickAddRequest("cloud", "Copilot", false, true, "ask cloud", true), Vocab(personal: [Entry("cloud", "Copilot")])).Message;
        var removal = QuickDictionaryAdd.Build(new QuickDictionaryAdd.QuickAddRequest("um", "", true, false), Vocab()).Message;
        var create = QuickDictionaryAdd.Build(Request("cloud", "Copilot", wholeWord: false), Vocab()).Message;

        Assert.EndsWith(" Dictations made before it was added keep the old words.", noChange, StringComparison.Ordinal);
        Assert.Equal($"Scribe will remove {Q("um")} from everything you dictate, even inside longer words.", removal);
        Assert.Equal($"Scribe will write {Q("Copilot")} when it hears {Q("cloud")}, even inside longer words.", create);
    }

    [Fact]
    public void Removal_only_with_explicit_flag_and_messages_never_say_rule_delete_or_saved()
    {
        var pending = QuickDictionaryAdd.Build(Request("um", ""), Vocab());
        var removal = QuickDictionaryAdd.Build(new QuickDictionaryAdd.QuickAddRequest("um", "", true, true), Vocab());

        Assert.Equal(QuickDictionaryAdd.PlanKind.Pending, pending.Kind);
        Assert.Equal(QuickDictionaryAdd.PlanKind.CreateRemoval, removal.Kind);
        foreach (var plan in new[] { pending, removal, QuickDictionaryAdd.Build(Request("cloud", "Copilot"), Vocab()) })
        {
            Assert.DoesNotContain("rule", plan.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("delete", plan.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("saved", plan.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Result_rows_have_exact_messages_and_actions()
    {
        Assert.Equal("Couldn't save this word. Try again, or add it in Settings.", QuickDictionaryAdd.SaveFailed().Message);
        Assert.Equal(QuickDictionaryAdd.PlanAction.AddItInSettings, QuickDictionaryAdd.SaveFailed().Action);
        Assert.Equal($"Saved. Scribe will write {Q("Copilot")} when it hears {Q("cloud")}.", QuickDictionaryAdd.Saved(Request("cloud", "Copilot")).Message);
        Assert.Equal($"Saved. Scribe will remove {Q("um")} from everything you dictate.", QuickDictionaryAdd.Saved(new QuickDictionaryAdd.QuickAddRequest("um", "", true, true)).Message);
        Assert.Equal("Copied. Press Ctrl+V to paste it.", QuickDictionaryAdd.CopySucceeded().Message);
        Assert.Equal("Couldn't copy. Another app may be using the clipboard. Try again.", QuickDictionaryAdd.CopyFailed().Message);
    }

    private static string Q(string value) => $"\"{value}\"";
    private static QuickDictionaryAdd.QuickAddRequest Request(string heard, string writes, bool wholeWord = true) => new(heard, writes, false, wholeWord);
    private static DictionaryEntry Entry(string pattern, string replacement, bool wholeWord = true, bool enabled = true) => new(0, pattern, replacement, wholeWord, enabled);
    private static PackTerm Pack(string name, string pattern, string replacement) => new(name, Entry(pattern, replacement));
    private static QuickAddVocabulary Vocab(DictionaryEntry[]? personal = null, string[]? pending = null, PackTerm[]? packs = null) => new(personal ?? [], new HashSet<string>(pending?.Select(p => p.Trim()) ?? [], StringComparer.OrdinalIgnoreCase), packs ?? []);

    private static void AssertPlan(QuickDictionaryAdd.QuickAddRequest request, QuickAddVocabulary vocabulary, QuickDictionaryAdd.PlanKind kind, string message, bool canSave, QuickDictionaryAdd.PlanSeverity severity, QuickDictionaryAdd.PlanAction action)
    {
        var plan = QuickDictionaryAdd.Build(request, vocabulary);
        Assert.Equal(kind, plan.Kind);
        Assert.Equal(message, plan.Message);
        Assert.Equal(canSave, plan.CanSave);
        Assert.Equal(severity, plan.Severity);
        Assert.Equal(action, plan.Action);
    }
}
