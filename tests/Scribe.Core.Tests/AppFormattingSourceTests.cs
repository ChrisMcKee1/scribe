using System.Text.RegularExpressions;
using System.Xml.Linq;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

/// <summary>The Windows shell routes, with their behavior exercised separately in Core and over scripted injection.</summary>
public sealed class AppFormattingSourceTests
{
    private static readonly string[] ProfileFields = ["TextFormat", "InjectionMethod", "ShiftEnterLineBreaks"];
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void Admission_takes_the_plan_and_one_shot_only_in_the_existing_admitted_capture_factory()
    {
        var source = Read("src", "Scribe.App", "Dictation", "DictationController.cs");
        var start = Body(source, "private void OnActivated(");
        var policy = start.IndexOf("DictationStartPolicy.BeginRecording(", StringComparison.Ordinal);
        var vocabulary = start.IndexOf("var vocabulary = _vocabulary.Current;", StringComparison.Ordinal);
        var formatting = Assert.Single(Regex.Matches(start,
            @"var\s+formatting\s*=\s*DictationFormatPlan\.CaptureAdmitted\(\s*captureSettings\s*,\s*targetApp\s*,\s*_plainTextOnce\s*,\s*out\s+plainTextOnceChange\s*\);"));
        var factory = start.IndexOf("return new CaptureContext(", StringComparison.Ordinal);
        var release = start.IndexOf("() => _hotkeys.CancelToggle(e.Activation));", StringComparison.Ordinal);
        var notify = start.IndexOf("_plainTextOnce.NotifyConsumption(plainTextOnceChange);", StringComparison.Ordinal);
        Assert.True(policy >= 0 && policy < vocabulary && vocabulary < formatting.Index && formatting.Index < factory &&
            factory < release && release < notify);
        Assert.Single(Regex.Matches(start, @"DictationFormatPlan\.\w+\s*\("));
        Assert.DoesNotContain("ConsumeForAdmittedCapture", start, StringComparison.Ordinal);
        Assert.Matches(@"vocabulary,\s*formatting\);", start[factory..release]);
        Assert.Single(Regex.Matches(start, Regex.Escape("ProcessNameForWindow(targetWindow)")));
        Assert.Contains("DictationCaptureSettingsResolver.Resolve(current, e.Trigger)", start, StringComparison.Ordinal);
        Assert.Contains("capture.Formatting", Body(source, "private void StopAndProcess("), StringComparison.Ordinal);
        Assert.Contains("_plainTextOnce.Dispose();", Body(source, "public void BeginShutdown()"), StringComparison.Ordinal);
        Assert.Contains("BeginShutdown();", Body(source, "public void Dispose()"), StringComparison.Ordinal);

        foreach (var path in Directory.EnumerateFiles(Path.Combine(Root(), "src", "Scribe.Core", "Hotkeys"), "*.cs"))
        {
            var hookSource = File.ReadAllText(path);
            Assert.DoesNotContain("PlainTextOnce", hookSource, StringComparison.Ordinal);
            Assert.DoesNotContain("DictationFormatPlan", hookSource, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_pipeline_still_cleans_once_then_applies_rules_then_R_then_the_existing_insertion_contract()
    {
        var source = Read("src", "Scribe.App", "Dictation", "DictationController.cs");
        var cleanup = source.IndexOf(".CleanAsync(recognized, cancellationToken, cleanupWritingStyle)", StringComparison.Ordinal);
        var post = source.IndexOf("_postProcessor.ProcessDetailed(recognized, result.Text)", StringComparison.Ordinal);
        var representation = source.IndexOf("session.Formatting.Represent(text)", StringComparison.Ordinal);
        var final = source.IndexOf("report.FinalText = text;", StringComparison.Ordinal);
        var insertion = source.IndexOf("DictationInsertion.Insert(", StringComparison.Ordinal);
        var failure = source.IndexOf("if (!injection.Succeeded)", insertion, StringComparison.Ordinal);
        var history = source.IndexOf("EnqueueHistory(session.Id", failure, StringComparison.Ordinal);
        var dictated = source.IndexOf("Dictated?.Invoke(insertion.Recorded)", history, StringComparison.Ordinal);
        Assert.True(cleanup >= 0 && cleanup < post && post < representation && representation < final && final < insertion &&
            insertion < failure && failure < history && history < dictated);
        Assert.Contains("return;", source[failure..history], StringComparison.Ordinal);
        Assert.Contains("insertion.Recorded, targetApp", source[history..dictated], StringComparison.Ordinal);
        Assert.Single(Regex.Matches(source, Regex.Escape(".CleanAsync(")));
        Assert.Single(Regex.Matches(source, Regex.Escape("_injector.Inject(")));
        Assert.Contains("typed, session.Formatting.InjectionMethod, session.TargetWindow, session.Formatting.ShiftEnterLineBreaks, targetApp",
            source[insertion..history], StringComparison.Ordinal);
        Assert.DoesNotContain("Html", source[insertion..history], StringComparison.Ordinal);
        Assert.DoesNotContain("Rtf", source[insertion..history], StringComparison.Ordinal);
        var notice = source.IndexOf("FormattingConflict?.Invoke()", representation, StringComparison.Ordinal);
        Assert.True(notice > representation);
        Assert.Contains("TryLog(", source[representation..insertion], StringComparison.Ordinal);

        foreach (var signature in new[] { "private CleanupOptions BuildCleanupOptions(", "private static string? CleanupWritingStyleFor(" })
        {
            var body = Body(source, signature);
            Assert.DoesNotContain(".TextFormat", body, StringComparison.Ordinal);
            Assert.DoesNotContain("DefaultTextFormat", body, StringComparison.Ordinal);
            Assert.DoesNotContain("AppAwareFormattingEnabled", body, StringComparison.Ordinal);
            Assert.DoesNotContain(".Formatting", body, StringComparison.Ordinal);
            Assert.DoesNotContain("PlainTextOnce", body, StringComparison.Ordinal);
            Assert.DoesNotContain("profile.Name", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Every_new_profile_value_crosses_row_loading_building_submissions_loaded_snapshots_and_dirty_comparison()
    {
        var profiles = Read("src", "Scribe.App", "Settings", "SettingsWindow.Profiles.cs");
        var footer = Read("src", "Scribe.App", "Settings", "SettingsWindow.Footer.cs");
        var closing = Read("src", "Scribe.App", "Settings", "SettingsWindow.Closing.cs");
        var tracker = Read("src", "Scribe.Core", "Settings", "SettingsChangeTracker.cs");
        var snapshot = Read("src", "Scribe.Core", "Vocabulary", "DraftSnapshot.cs");
        var loaded = Body(profiles, "private void LoadProfiles()");
        var preset = Body(profiles, "private void AddPresetProfile(");
        foreach (var field in ProfileFields)
        {
            Assert.Matches($@"(?m)^\s+{field} = profile\.{field},", loaded);
            Assert.Matches($@"(?m)^\s+{field} = profile\.{field},", preset);
            Assert.Matches($@"(?m)^\s+Loaded{field} = profile\.{field},", loaded);
            Assert.Contains($"r.{field}", profiles, StringComparison.Ordinal);
            Assert.Contains($"row.Loaded{field} = submitted.{field}", profiles, StringComparison.Ordinal);
            Assert.Contains($"row.{field} == submitted.{field}", profiles, StringComparison.Ordinal);
            Assert.Contains($"row.Loaded{field}", footer, StringComparison.Ordinal);
            Assert.Contains($"{field}: row.{field}", closing, StringComparison.Ordinal);
            Assert.Contains($"Loaded{field}: row.Loaded{field}", closing, StringComparison.Ordinal);
            Assert.Contains($"before.{field} != after.{field}", tracker, StringComparison.Ordinal);
            Assert.Contains($"left.{field} != right.{field}", tracker, StringComparison.Ordinal);
            Assert.Contains($"profile.{field}", snapshot, StringComparison.Ordinal);
        }

        Assert.Contains("ShowProfileFormatting(row);", profiles, StringComparison.Ordinal);
        Assert.Contains("var profileSubmission = CaptureProfileSubmission(profileRows);", closing, StringComparison.Ordinal);
    }

    [Fact]
    public void Global_fields_use_ordinary_preflight_Save_and_the_existing_draft_walk_not_browsing_side_effects()
    {
        var window = Read("src", "Scribe.App", "Settings", "SettingsWindow.xaml.cs");
        var footer = Read("src", "Scribe.App", "Settings", "SettingsWindow.Footer.cs");
        var tryDictation = Read("src", "Scribe.App", "Settings", "SettingsWindow.TryDictation.cs");
        var formatting = Read("src", "Scribe.App", "Settings", "SettingsWindow.Formatting.cs");
        foreach (var source in new[] { footer, tryDictation })
        {
            Assert.Contains("draft.AppAwareFormattingEnabled = AppAwareFormattingCheck.IsChecked == true;", source, StringComparison.Ordinal);
            Assert.Contains("draft.DefaultTextFormat = SelectedDefaultTextFormat;", source, StringComparison.Ordinal);
        }

        Assert.Contains("AppAwareFormattingCheck.IsChecked = source.AppAwareFormattingEnabled;", formatting, StringComparison.Ordinal);
        Assert.DoesNotContain("_applySettings", formatting, StringComparison.Ordinal);
        Assert.DoesNotContain(".Save", formatting, StringComparison.Ordinal);
        Assert.DoesNotContain(".Configure", formatting, StringComparison.Ordinal);
        var save = Body(window, "private async Task<bool> TrySaveAsync()");
        var copy = save.LastIndexOf("CopySettings(preflight.Settings, _settings);", StringComparison.Ordinal);
        var store = save.IndexOf("await _wordPackSaveProtocol.SaveAsync(", StringComparison.Ordinal);
        Assert.True(copy > 0 && copy < store);
        Assert.Contains("var copy = source.Clone();", Body(window, "private static void CopySettings("), StringComparison.Ordinal);
        Assert.Contains("property.SetValue(target, property.GetValue(copy));", window, StringComparison.Ordinal);
        var draft = Body(window, "private string SaveDraftSignature(SaveDraftSections sections");
        Assert.Contains("SectionDictation", draft, StringComparison.Ordinal);
        Assert.Contains(".Profiles(BuildProfiles())", draft, StringComparison.Ordinal);
        Assert.Contains("TryDictationFormattingText.Text = report.Formatting", tryDictation, StringComparison.Ordinal);
    }

    [Fact]
    public void New_controls_are_native_labelled_font_scaled_and_indexed_or_covered_by_the_profile_destination()
    {
        var xaml = XDocument.Load(Path.Combine(Root(), "src", "Scribe.App", "Settings", "SettingsWindow.xaml"));
        var names = xaml.Descendants().Where(element => element.Attribute(X + "Name") is not null)
            .ToDictionary(element => (string)element.Attribute(X + "Name")!, StringComparer.Ordinal);
        foreach (var name in new[] { "AppAwareFormattingCheck", "DefaultTextFormatCombo", "ProfileTextFormatCombo", "ProfileInjectionCombo", "ProfileShiftEnterCombo" })
        {
            var control = names[name];
            Assert.Contains(control.Name.LocalName, new[] { "CheckBox", "ComboBox" });
            Assert.NotNull(control.Attribute("AutomationProperties.LabeledBy"));
            Assert.NotNull(control.Attribute("AutomationProperties.HelpText"));
            Assert.Null(control.Attribute("Width"));
            Assert.Null(control.Attribute("Height"));
            Assert.Null(control.Attribute("FontSize"));
            Assert.Null(control.Attribute("TabIndex"));
            Assert.NotEqual("False", (string?)control.Attribute("IsTabStop"));
        }

        Assert.Equal("TextBlock", names["TryDictationFormattingText"].Name.LocalName);
        Assert.Contains(SettingsSearchIndex.Search("markdown"), result => result.ControlName == "DefaultTextFormatCombo");
        Assert.Contains(SettingsSearchIndex.Search("profile paste"), result => result.ControlName == "ProfileList");
        Assert.Contains(SettingsSearchIndex.Search("plain text once"), result => result.ControlName == "AppAwareFormattingCheck");
    }

    [Fact]
    public void The_one_shot_is_a_single_process_owner_with_revision_checked_tray_presentation_and_no_target_snooping()
    {
        var services = Read("src", "Scribe.Core", "DependencyInjection", "CoreServiceCollectionExtensions.cs");
        var app = Read("src", "Scribe.App", "App.xaml.cs");
        var presentation = Read("src", "Scribe.App", "App.Formatting.cs");
        var tray = Read("src", "Scribe.App", "Tray", "TrayIconHost.cs");
        Assert.Contains("services.AddSingleton<PlainTextOnce>();", services, StringComparison.Ordinal);
        Assert.Contains("plainTextOnce.Arm();", app, StringComparison.Ordinal);
        Assert.Contains("plainTextOnce.Cancel();", app, StringComparison.Ordinal);
        Assert.Contains("plainTextOnce.Changed += OnPlainTextOnceChanged;", app, StringComparison.Ordinal);
        Assert.Contains("state.Revision != owner.Current.Revision", presentation, StringComparison.Ordinal);
        Assert.Contains("state.Revision <= _shownPlainTextOnceRevision", presentation, StringComparison.Ordinal);
        Assert.Contains("Dispatcher.BeginInvoke", presentation, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetHelpText(menuItem, help);", tray, StringComparison.Ordinal);
        Assert.Contains("TrayCommand.PlainTextOnce", tray, StringComparison.Ordinal);
        Assert.DoesNotContain("GetForegroundWindow", presentation, StringComparison.Ordinal);
        Assert.DoesNotContain("ProcessName", presentation, StringComparison.Ordinal);
        Assert.DoesNotContain("Clipboard", presentation, StringComparison.Ordinal);
        Assert.DoesNotContain("Save", presentation, StringComparison.Ordinal);
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([Root(), .. parts]));

    private static string Body(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature);
        var end = source.IndexOf("\n    }", start, StringComparison.Ordinal);
        Assert.True(end > start, signature);
        return source[start..end];
    }

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Scribe.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }
}
