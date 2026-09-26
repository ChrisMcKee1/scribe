using System.Text.RegularExpressions;

namespace Scribe.Core.Tests.Vocabulary;

/// <summary>
/// The shell's side of vocabulary publication, pinned by source because the app has no tests of its own; what it calls is
/// tested in Core (<see cref="Scribe.Core.Vocabulary.VocabularyPublisher"/> and <c>StoredSettingsReapply</c>). A change
/// that stores vocabulary is reported as applied only once the generation built for it is what the next dictation is
/// admitted with, and a build that failed is reported as saved but not applied. The first generation is built off the
/// dispatcher, where the library source's first read may load a cold catalog, and awaited before the hotkey is installed.
/// Nothing waits on either synchronously.
/// </summary>
public sealed class VocabularyApplicationSourceTests
{
    [Fact]
    public void Every_change_that_stores_vocabulary_is_reported_applied_only_after_its_generation_is_published()
    {
        var controller = Read("src", "Scribe.App", "Dictation", "DictationController.cs");
        var app = Read("src", "Scribe.App", "App.xaml.cs");
        var window = Read("src", "Scribe.App", "Settings", "SettingsWindow.xaml.cs");

        // The controller hands every refresh it asks for back to its caller and discards none.
        Assert.DoesNotContain("_ = _vocabulary.RefreshAsync()", controller, StringComparison.Ordinal);
        foreach (var signature in new[]
        {
            "public Task<VocabularyRefresh> ApplySettings(AppSettings settings)",
            "public Task<VocabularyRefresh> ReloadVocabulary()",
        })
        {
            var body = Body(controller, signature);
            Assert.Contains("var vocabulary = _vocabulary.RefreshAsync();", body, StringComparison.Ordinal);
            Assert.Contains("return vocabulary;", body, StringComparison.Ordinal);
        }

        // The Settings window's two application callbacks carry the answer.
        Assert.Contains("private readonly Func<AppSettings, Task<Scribe.Core.Vocabulary.VocabularyRefresh>> _applySettings;", window, StringComparison.Ordinal);
        Assert.Contains("private readonly Func<Task<Scribe.Core.Vocabulary.VocabularyRefresh>> _reloadVocabulary;", window, StringComparison.Ordinal);

        // A Save reports success only after the generation its application asked for is awaited; a build that failed is
        // said to have saved the settings without them applying yet, and the window stays open with that.
        var save = Body(window, "private async Task<bool> TrySaveAsync()");
        var apply = save.IndexOf("var applying = _applySettings(_settings);", StringComparison.Ordinal);
        var awaited = save.IndexOf("var outcome = await acknowledgement.CompleteAsync();", StringComparison.Ordinal);
        var notApplied = save.IndexOf("if (outcome == Scribe.Core.Vocabulary.StoredChangeOutcome.NotInUseYet)", StringComparison.Ordinal);
        var succeeded = save.LastIndexOf("return true;", StringComparison.Ordinal);
        Assert.True(
            apply > 0 && apply < awaited && awaited < notApplied && notApplied < succeeded,
            "A Save is reported before dictation can use what it stored.");
        Assert.Contains("VocabularyNotice.SavedButNotApplied(\"Settings saved\")", save[notApplied..succeeded], StringComparison.Ordinal);
        Assert.Contains("if (await ConfirmDictionaryOverlapAsync() && await TrySaveAsync())", Body(window, "private async void SaveButton_Click("), StringComparison.Ordinal);

        // The Usage page's Add says the term was added only once the stored settings' generation is published.
        var add = Body(window, "private async void UsageNovelTermAddButton_Click(");
        var reapply = add.IndexOf("var reapplied = StoredSettingsReapply.Reapply(_settingsRepository, _applySettings, _reloadVocabulary);", StringComparison.Ordinal);
        var refreshed = add.IndexOf("var refresh = await reapplied.Vocabulary;", StringComparison.Ordinal);
        var added = add.IndexOf("to your dictionary.\"", StringComparison.Ordinal);
        Assert.True(reapply > 0 && reapply < refreshed && refreshed < added, "The Usage page's Add is reported before dictation can use it.");
        Assert.Contains("VocabularyNotice.SavedButNotApplied(", add, StringComparison.Ordinal);

        // The app's Save callback returns the application's answer, and the reload callback is the controller's.
        Assert.Contains("var applying = _controller!.ApplySettings(settings);", app, StringComparison.Ordinal);
        Assert.Contains("return applying;", app, StringComparison.Ordinal);
        Assert.Contains("() => _controller!.ReloadVocabulary(),", app, StringComparison.Ordinal);

        // Quick add and learning from history store the dictionary outside a save, and each awaits the generation built
        // for it, off the dispatcher, before saying the change is in effect.
        var quickAdd = Body(app, "private async void OnQuickAddSaved(");
        var quickReload = quickAdd.IndexOf("var refresh = await Task.Run(() => controller.ReloadVocabulary());", StringComparison.Ordinal);
        var willNow = quickAdd.IndexOf("will now be written as", StringComparison.Ordinal);
        Assert.True(quickReload > 0 && quickReload < willNow, "Quick add says a rule will be written before dictation can use it.");
        Assert.Contains("if (!refresh.Applied)", quickAdd, StringComparison.Ordinal);
        Assert.Contains("VocabularyNotice.SavedButNotApplied(\"Saved the rule\")", quickAdd, StringComparison.Ordinal);

        var learn = Body(app, "private async void LearnFromHistory()");
        var learnReload = learn.IndexOf("applied = (await Task.Run(() => controller.ReloadVocabulary())).Applied;", StringComparison.Ordinal);
        var learnedNotice = learnReload > 0 ? learn.IndexOf("_tray.ShowNotification(", learnReload, StringComparison.Ordinal) : -1;
        Assert.True(learnReload > 0 && learnedNotice > learnReload, "Learning says terms were learned before dictation can use them.");
        Assert.Contains("VocabularyNotice.SavedButNotApplied(learnedNotice)", learn[learnedNotice..], StringComparison.Ordinal);
        Assert.DoesNotContain("ITextPostProcessor>().Reload()", app, StringComparison.Ordinal);

        AssertNothingWaitsSynchronously(controller, app, window);
    }

    [Fact]
    public void The_first_generation_is_built_off_the_dispatcher_and_awaited_before_the_hotkey_is_installed()
    {
        var controller = Read("src", "Scribe.App", "Dictation", "DictationController.cs");
        var app = Read("src", "Scribe.App", "App.xaml.cs");
        var publisher = Read("src", "Scribe.Core", "Vocabulary", "VocabularyPublisher.cs");

        // Startup: the controller exists, then its first generation is awaited, and only then does anything that takes
        // input appear: the tray, and at the controller's Start, the hotkey. A shutdown that began meanwhile ends it there.
        var start = Body(app, "private async Task<bool> StartAsync()");
        var constructed = start.IndexOf("_controller = new DictationController(", StringComparison.Ordinal);
        var prepared = start.IndexOf("await _controller.PrepareAsync();", StringComparison.Ordinal);
        var tray = start.IndexOf("_tray = new TrayIconHost(", StringComparison.Ordinal);
        var started = start.IndexOf("_controller.Start();", StringComparison.Ordinal);
        Assert.True(
            constructed > 0 && constructed < prepared && prepared < tray && tray < started,
            "The first vocabulary generation is not awaited before the tray and the hotkey.");
        Assert.Contains("if (Dispatcher.HasShutdownStarted || _controller.IsClosing)", start[prepared..tray], StringComparison.Ordinal);

        // The controller: PrepareAsync awaits the publisher's first generation; Start builds none, refuses to run before
        // PrepareAsync, and is the one place the hook is installed.
        Assert.Contains("await _vocabulary.StartAsync();", Body(controller, "public async Task PrepareAsync()"), StringComparison.Ordinal);
        var startBody = Body(controller, "public void Start()");
        Assert.DoesNotContain("_vocabulary.", startBody, StringComparison.Ordinal);
        var guard = startBody.IndexOf("if (!_prepared)", StringComparison.Ordinal);
        var hook = startBody.IndexOf("_hotkeys.Start();", StringComparison.Ordinal);
        Assert.True(guard > 0 && guard < hook, "Start can install the hotkey before the first vocabulary generation.");
        Assert.Single(Regex.Matches(controller, Regex.Escape("_hotkeys.Start();")));

        // The publisher builds the first generation the way it builds every other, through its scheduler, which is the
        // thread pool in production, never on the caller's thread, and bounds the wait for it by the startup deadline.
        Assert.Contains("static work => _ = Task.Run(work)", publisher, StringComparison.Ordinal);
        var startAsync = Body(publisher, "public Task<VocabularyRefresh> StartAsync()");
        Assert.Contains("return FirstGenerationAsync(Request(StartupDeadline, first: true));", startAsync, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildAndPublish(", startAsync, StringComparison.Ordinal);
        Assert.DoesNotContain("public VocabularyGeneration Start()", publisher, StringComparison.Ordinal);

        AssertNothingWaitsSynchronously(controller, app, Read("src", "Scribe.App", "Settings", "SettingsWindow.xaml.cs"));
    }

    [Fact]
    public void A_save_whose_draft_changed_while_it_waited_neither_reports_success_nor_closes()
    {
        // Round 3, A6: the window stays editable while its Save awaits the generation for what it stored, so an edit made
        // then is not in what was stored. The Save takes its draft when the wait starts, compares it once the answer is
        // in (StoredChangeAcknowledgement, tested in Core), and on a change says so and returns false, which keeps Save and
        // close from closing over the edit. It never saves the edit itself.
        var window = Read("src", "Scribe.App", "Settings", "SettingsWindow.xaml.cs");
        var save = Body(window, "private async Task<bool> TrySaveAsync()");

        // The draft is taken with nothing since the Save read what it stores that could let an edit in: no await from the
        // row signatures it marks as saved and its first control read, through the store and the application, to the watch.
        var signatures = save.IndexOf("var dictionarySignature = DictionarySignature();", StringComparison.Ordinal);
        var read = save.IndexOf("_externalMicrophone.ForSave(ShownMicrophone).ApplyTo(_settings);", StringComparison.Ordinal);
        var store = save.IndexOf("_settingsRepository.SaveBundle(", StringComparison.Ordinal);
        var apply = save.IndexOf("var applying = _applySettings(_settings);", StringComparison.Ordinal);
        var watch = save.IndexOf(
            "var acknowledgement = Scribe.Core.Vocabulary.StoredChangeAcknowledgement.Watch(applying, SaveDraftSignature);",
            StringComparison.Ordinal);
        var awaited = save.IndexOf("var outcome = await acknowledgement.CompleteAsync();", StringComparison.Ordinal);
        Assert.True(
            signatures > 0 && signatures < read && read < store && store < apply && apply < watch && watch < awaited,
            "The Save does not take its draft between storing it and awaiting its generation.");
        Assert.DoesNotMatch(@"\bawait\b", save[signatures..watch]);

        // A change while waiting is reported and the Save returns false; only an unchanged draft in use returns true.
        var tail = save[awaited..];
        var changed = tail.IndexOf("if (outcome == Scribe.Core.Vocabulary.StoredChangeOutcome.ChangedWhileSaving)", StringComparison.Ordinal);
        var succeeded = tail.IndexOf("return true;", StringComparison.Ordinal);
        Assert.True(changed > 0 && succeeded > changed, "A Save whose draft changed while it waited can still report success.");
        var changedBranch = tail[changed..succeeded];
        Assert.Contains("Scribe.Core.Vocabulary.VocabularyNotice.SettingsChangedWhileSaving", changedBranch, StringComparison.Ordinal);
        Assert.Contains("return false;", changedBranch, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(tail, Regex.Escape("return true;")));

        // Nothing after the wait stores anything: the later edit waits for the user's next Save.
        Assert.DoesNotContain("_settingsRepository.", tail, StringComparison.Ordinal);
        Assert.DoesNotContain("_applySettings(", tail, StringComparison.Ordinal);

        // Save and close closes only on a Save that returned true.
        var saveClose = Body(window, "private async void SaveCloseButton_Click(");
        var closes = saveClose.IndexOf("Close();", StringComparison.Ordinal);
        var guarded = saveClose.IndexOf("if (await ConfirmDictionaryOverlapAsync() && await TrySaveAsync())", StringComparison.Ordinal);
        Assert.True(guarded > 0 && closes > guarded, "Save and close can close without a Save that returned true.");
        Assert.Single(Regex.Matches(saveClose, Regex.Escape("Close();")));

        // The draft is what a Save stores, read the way the Save reads it: the editors of the pages whose controls it reads,
        // and everything it computes beyond a plain editor as it computes it, each value framed. Hashed, never logged.
        var draft = Body(window, "private string SaveDraftSignature()");
        foreach (var part in new[]
        {
            "new FrameworkElement[] { SectionGeneral, SectionDictation, SectionOverlay, SectionAi }",
            "draft.Part(page.Name);",
            "AppendEditorValues(page, carriedElsewhere, draft);",
            ".Binding(_pendingBinding with { Mode = SelectedMode })",
            "_pendingDictationOnlyBinding with { Mode = DictationOnlySelectedMode }",
            ".Flag(_capturing)",
            ".Flag(_externalAiCleanup.ForSave(AiCleanupCheck.IsChecked == true))",
            ".Microphone(_externalMicrophone.ForSave(ShownMicrophone))",
            ".Subscription(AzureSubscriptionSelection.ResolveAuthenticationSubscription(",
            "_selectedAzureDeployment, SelectedAzureSubscription, AzureEndpointBox.Text, AzureDeploymentBox.Text));",
            ".Profiles(BuildProfiles())",
            ".LibrarySet(_libraryLoad.IsLoaded ? CollectEnabledLibraryIds() : _settings.EnabledDictionaryLibraryIds)",
            ".Flag(_dictionaryLoad.HasChanges(DictionarySignature()))",
            ".Flag(_snippetLoad.HasChanges(SnippetSignature()))",
            ".Flag(RowEditInProgress(DictionaryGrid))",
            ".Flag(RowEditInProgress(LibraryGrid))",
            "return draft.Hash();",
        })
        {
            Assert.Contains(part, draft, StringComparison.Ordinal);
        }

        // A row read that finishes during the wait publishes what storage holds, which is no change: the dictionary and the
        // snippets go by whether they differ from storage, never by their raw signatures, and the libraries by the set a
        // Save writes, never by the rows' signature (the library snapshot is not updated by a Save).
        Assert.DoesNotContain("(DictionarySignature())", draft.Replace("HasChanges(DictionarySignature())", string.Empty), StringComparison.Ordinal);
        Assert.DoesNotContain("(SnippetSignature())", draft.Replace("HasChanges(SnippetSignature())", string.Empty), StringComparison.Ordinal);
        Assert.DoesNotContain("LibrarySignature()", draft, StringComparison.Ordinal);
        Assert.DoesNotContain("_log.", draft, StringComparison.Ordinal);

        var editors = Body(window, "private static void AppendEditorValues(DependencyObject node, HashSet<DependencyObject> skipped, Scribe.Core.Vocabulary.DraftSnapshot draft)");
        foreach (var part in new[]
        {
            "if (skipped.Contains(node))",
            "case DataGrid:",
            "case Wpf.Ui.Controls.PasswordBox secret:",
            "case Wpf.Ui.Controls.NumberBox number:",
            "draft.Part(\"number\").Text(number.Text).Number(number.Value);",
            "case TextBox text:",
            "draft.Part(\"text\").Text(text.Text);",
            "case PasswordBox secret:",
            "draft.Part(\"password\").Text(secret.Password);",
            "case System.Windows.Controls.Primitives.ToggleButton toggle:",
            "draft.Part(\"toggle\").Flag(toggle.IsChecked);",
            "case ComboBox { IsEditable: true } editable:",
            "draft.Part(\"editable\").Text(editable.Text);",
            "case ComboBox combo:",
            "draft.Part(\"choice\").Number(combo.SelectedIndex).Text(combo.Text);",
            "case Slider slider:",
            "draft.Part(\"slider\").Number(slider.Value);",
            "LogicalTreeHelper.GetChildren(node)",
        })
        {
            Assert.Contains(part, editors, StringComparison.Ordinal);
        }

        Assert.Contains(
            "grid.Items is IEditableCollectionView rows && (rows.IsEditingItem || rows.IsAddingNew)",
            window,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Every_control_a_save_reads_is_one_the_save_draft_compares()
    {
        // The draft reads the editors of four pages by walking their logical trees, and the grids and lists by their row
        // signatures. A control a Save reads anywhere else would be missing from it, and an edit to it made while the
        // Save waited would be closed over. So every named control the Save reads, in its body or through the properties
        // it reads the pickers by, is declared inside one of the four pages, outside any template, resource, tooltip or
        // menu (which are not logical children), or is one of the lists the signatures cover.
        var window = Read("src", "Scribe.App", "Settings", "SettingsWindow.xaml.cs");
        var xaml = System.Xml.Linq.XDocument.Load(Path.Combine(Root(), "src", "Scribe.App", "Settings", "SettingsWindow.xaml"));
        System.Xml.Linq.XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var named = xaml.Descendants()
            .Where(element => element.Attribute(x + "Name") is not null)
            .ToLookup(element => element.Attribute(x + "Name")!.Value);

        var pages = new[] { "SectionGeneral", "SectionDictation", "SectionOverlay", "SectionAi" };
        var rowLists = new[] { "DictionaryGrid", "LibraryGrid", "SnippetList", "ProfileList" };
        var read = Regex.Matches(Body(window, "private async Task<bool> TrySaveAsync()"), @"\b[A-Z]\w*\b")
            .Select(match => match.Value)
            .Where(named.Contains)
            .Concat(["DeviceCombo", "ModeCombo", "DictationOnlyModeCombo", "AiProviderCombo", "AiPromptStyleCombo", "AzureAuthModeBox", "AzureApiKeyBox", "AzureSubscriptionBox", "OverlayPositionGrid"])
            .Distinct()
            .ToList();
        Assert.Contains("AiCleanupCheck", read);
        Assert.Contains("SpClientSecretBox", read);
        Assert.Contains("ThreadsSlider", read);

        foreach (var name in read)
        {
            // The pages the Save shows on a validation problem, and the status line it writes, are not values it stores.
            if (rowLists.Contains(name) || name.StartsWith("Section", StringComparison.Ordinal) || name == "AzureStatusText")
            {
                continue;
            }

            var declared = Assert.Single(named[name]);
            var ancestors = declared.Ancestors().ToList();
            var page = ancestors.FirstOrDefault(element => pages.Contains(element.Attribute(x + "Name")?.Value));
            Assert.True(page is not null, $"The Save reads {name}, which is on no page the draft compares.");
            var between = ancestors.TakeWhile(element => element != page).Select(element => element.Name.LocalName).ToList();
            Assert.DoesNotContain(between, element =>
                element.Contains("Template", StringComparison.Ordinal) ||
                element.Contains("Resources", StringComparison.Ordinal) ||
                element.Contains("ToolTip", StringComparison.Ordinal) ||
                element.Contains("ContextMenu", StringComparison.Ordinal) ||
                element.Contains("Flyout", StringComparison.Ordinal) ||
                element.Contains("Popup", StringComparison.Ordinal));
        }

        // The walk skips exactly the controls whose stored value the draft carries as the Save computes it, and the one whose
        // value a Save never takes: skipping a control with nothing carrying it would drop what a Save stores from it. The Azure
        // model picker is walked, not skipped (round 4, A7): its typed text applies only when it loses focus, so the
        // subscription it resolves to cannot stand in for it.
        var draft = Body(window, "private string SaveDraftSignature()");
        var skippedList = Regex.Match(draft, @"HashSet<DependencyObject> carriedElsewhere =\s*\[(?<names>[^\]]*)\]");
        Assert.True(skippedList.Success, "The draft's list of controls the walk skips was not found.");
        var skipped = skippedList.Groups["names"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var carriedBy = new Dictionary<string, string>
        {
            // Start with Windows applies from its own switch when flipped; a Save stores the Windows observation it read
            // before the wait, never the switch.
            ["LaunchCheck"] = string.Empty,
            ["HotkeyBox"] = "_pendingBinding with { Mode = SelectedMode }",
            ["ModeCombo"] = "_pendingBinding with { Mode = SelectedMode }",
            ["DictationOnlyHotkeyBox"] = "_pendingDictationOnlyBinding with { Mode = DictationOnlySelectedMode }",
            ["DictationOnlyModeCombo"] = "_pendingDictationOnlyBinding with { Mode = DictationOnlySelectedMode }",
            ["DeviceCombo"] = "_externalMicrophone.ForSave(ShownMicrophone)",
            ["AiCleanupCheck"] = "_externalAiCleanup.ForSave(AiCleanupCheck.IsChecked == true)",
            ["AzureSubscriptionBox"] = "_selectedAzureDeployment, SelectedAzureSubscription, AzureEndpointBox.Text, AzureDeploymentBox.Text",
        };
        Assert.Equal(carriedBy.Keys.Order(StringComparer.Ordinal), skipped.Order(StringComparer.Ordinal));
        Assert.DoesNotContain("AzureModelBox", skipped);
        foreach (var (name, carrier) in carriedBy)
        {
            if (carrier.Length == 0)
            {
                Assert.DoesNotContain(name, read);
            }
            else
            {
                Assert.Contains(carrier, draft, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void A_start_that_gives_up_on_the_first_generation_ends_through_AbandonStartup_with_the_failure_notice()
    {
        // Round 3, A5: the first generation's wait is bounded (VocabularyPublisher.StartupDeadline), and what the publisher
        // throws then reaches the startup guard AGENTS.md describes: logged by shape and stack, the startup failure notice
        // naming the log file, and Shutdown, which releases the single-instance mutex in OnExit.
        var app = Read("src", "Scribe.App", "App.xaml.cs");
        var controller = Read("src", "Scribe.App", "Dictation", "DictationController.cs");

        // Nothing between the publisher and the guard catches it: PrepareAsync awaits the start with no handler, and
        // StartAsync awaits PrepareAsync with none either.
        var prepare = Body(controller, "public async Task PrepareAsync()");
        Assert.Contains("await _vocabulary.StartAsync();", prepare, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"\bcatch\b", prepare);
        var start = Body(app, "private async Task<bool> StartAsync()");
        var constructed = start.IndexOf("_controller = new DictationController(", StringComparison.Ordinal);
        var prepared = start.IndexOf("await _controller.PrepareAsync();", StringComparison.Ordinal);
        Assert.True(constructed > 0 && prepared > constructed);
        Assert.DoesNotMatch(@"\btry\b", start[constructed..prepared]);
        Assert.DoesNotContain("catch (TimeoutException", app, StringComparison.Ordinal);

        // OnStartup hands anything StartAsync throws to AbandonStartup, which tells the user and shuts down.
        var onStartup = Body(app, "protected override async void OnStartup(StartupEventArgs e)");
        var awaited = onStartup.IndexOf("started = await StartAsync();", StringComparison.Ordinal);
        var abandoned = onStartup.IndexOf("AbandonStartup(ex);", StringComparison.Ordinal);
        Assert.True(awaited > 0 && abandoned > awaited, "A failed start does not reach AbandonStartup.");
        Assert.Contains("catch (Exception ex)", onStartup[awaited..abandoned], StringComparison.Ordinal);
        var abandon = Body(app, "private void AbandonStartup(Exception failure)");
        Assert.Contains("FailureShape.DescribeWithStack(failure)", abandon, StringComparison.Ordinal);
        Assert.Contains("ShowStartupFailureNotice();", abandon, StringComparison.Ordinal);
        Assert.Contains("Shutdown();", abandon, StringComparison.Ordinal);
        Assert.Contains(
            "Scribe.Core.Lifecycle.StartupFailureNotice.Compose(log is { } status && status.Healthy ? status.Path : null)",
            Body(app, "private static void ShowStartupFailureNotice()"),
            StringComparison.Ordinal);

        // The comment over the await says exactly what ends startup there and what does not (Grok's round 2 wording point).
        Assert.DoesNotContain("A failure here is a startup failure like any other", app, StringComparison.Ordinal);
        Assert.Contains("within VocabularyPublisher.StartupDeadline", start[constructed..prepared], StringComparison.Ordinal);
        Assert.Contains("A first build that cannot read its", start[constructed..prepared], StringComparison.Ordinal);
    }

    // No vocabulary task is waited on synchronously, which on the dispatcher would stall the UI and could deadlock a build
    // whose continuation needs it.
    private static void AssertNothingWaitsSynchronously(params string[] sources)
    {
        foreach (var source in sources)
        {
            Assert.DoesNotMatch(@"(RefreshAsync|StartAsync|PrepareAsync|ApplySettings|ReloadVocabulary)\([^;]*\)\s*\.\s*(Result\b|Wait\(|GetAwaiter\(\))", source);
            Assert.DoesNotMatch(@"\b(applying|applied|refresh|reapplied|vocabulary)\s*\.\s*(Result\b|Wait\(|GetAwaiter\(\))", source);
            Assert.DoesNotMatch(@"\.Vocabulary\s*\.\s*(Result\b|Wait\(|GetAwaiter\(\))", source);
        }
    }

    // A method's text, from its signature to the closing brace at its indentation.
    private static string Body(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Not found: {signature}");
        var end = source.IndexOf("\n    }", start, StringComparison.Ordinal);
        Assert.True(end > start, $"No end found for: {signature}");
        return source[start..end];
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([Root(), .. parts]));

    private static string Root()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Scribe.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return root.FullName;
    }
}
