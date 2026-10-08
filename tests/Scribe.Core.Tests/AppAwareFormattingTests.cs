using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.Settings;
using Scribe.Core.TextInjection;
using Scribe.Core.Vocabulary;

namespace Scribe.Core.Tests;

public sealed class AppAwareFormattingTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static IEnumerable<object[]> MalformedValues() =>
        new[] { "\"future-format\"", "\"1\"", "9", "-1", "1.5", "null", "true", "false", "{}", "{\"nested\":[1,2]}", "[]", "[{}]" }
            .Select(value => new object[] { value });

    [Fact]
    public void Missing_fields_keep_all_existing_installs_opted_out_and_profiles_inheriting()
    {
        using var db = ScribeDatabase.CreateInMemory();
        var repository = new SettingsRepository(db);
        repository.Set(SettingsRepository.SettingsKey, """{"profiles":[{"name":"Editor","processNames":["Code"]}]}""");
        var settings = repository.Load();

        Assert.False(repository.LastLoadFailed);
        Assert.False(settings.AppAwareFormattingEnabled);
        Assert.Equal(DictationTextFormat.Plain, settings.DefaultTextFormat);
        Assert.Null(settings.Profiles[0].TextFormat);
        Assert.Null(settings.Profiles[0].InjectionMethod);
        Assert.Null(settings.Profiles[0].ShiftEnterLineBreaks);
        Assert.False(AppSettings.CreateDefault().AppAwareFormattingEnabled);
        Assert.False(AppSettings.CreateForExistingInstall().AppAwareFormattingEnabled);
    }

    [Theory]
    [MemberData(nameof(MalformedValues))]
    public void New_fields_tolerate_every_unknown_json_shape_without_losing_the_document(string value)
    {
        using var db = ScribeDatabase.CreateInMemory();
        var repository = new SettingsRepository(db);
        repository.Set(SettingsRepository.SettingsKey,
            $$"""
            {"appAwareFormattingEnabled":{{value}},"defaultTextFormat":{{value}},
             "profiles":[{"name":"Kept","processNames":["Code"],"textFormat":{{value}},
                          "injectionMethod":{{value}},"shiftEnterLineBreaks":{{value}},
                          "writingStyle":"Still kept","newlineHandling":"KeepNewlines"}],
             "decodeThreads":3,"addSpaceAfterDictation":false}
            """);
        var settings = repository.Load();

        Assert.False(repository.LastLoadFailed);
        Assert.Equal(3, settings.DecodeThreads);
        Assert.False(settings.AddSpaceAfterDictation);
        Assert.Equal(value == "true", settings.AppAwareFormattingEnabled);
        Assert.Equal(DictationTextFormat.Plain, settings.DefaultTextFormat);
        var profile = Assert.Single(settings.Profiles);
        Assert.Equal("Kept", profile.Name);
        Assert.Equal("Still kept", profile.WritingStyle);
        Assert.Equal(NewlineInjectionMode.KeepNewlines, profile.NewlineHandling);
        Assert.Equal(value == "null" ? (DictationTextFormat?)null : DictationTextFormat.Plain, profile.TextFormat);
        Assert.Null(profile.InjectionMethod);
        Assert.Equal(value == "true" ? true : value == "false" ? false : (bool?)null, profile.ShiftEnterLineBreaks);
    }

    [Theory]
    [InlineData("\"true\"")]
    [InlineData("\"false\"")]
    [InlineData("1")]
    [InlineData("0")]
    public void A_string_or_number_cannot_opt_an_install_in(string value)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>($$"""{"appAwareFormattingEnabled":{{value}}}""", Json)!;
        Assert.False(settings.AppAwareFormattingEnabled);
    }

    [Fact]
    public void Known_values_are_exact_case_insensitive_names_not_enum_numbers_or_lists()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(
            """
            {"appAwareFormattingEnabled":true,"defaultTextFormat":"mArKdOwNsOuRcE",
             "profiles":[{"textFormat":"pLaIn","injectionMethod":"cLiPbOaRdPaStE","shiftEnterLineBreaks":false},
                         {"textFormat":"Plain, MarkdownSource","injectionMethod":"0"}]}
            """, Json)!;
        Assert.True(settings.AppAwareFormattingEnabled);
        Assert.Equal(DictationTextFormat.MarkdownSource, settings.DefaultTextFormat);
        Assert.Equal(DictationTextFormat.Plain, settings.Profiles[0].TextFormat);
        Assert.Equal(InjectionMethod.ClipboardPaste, settings.Profiles[0].InjectionMethod);
        Assert.False(settings.Profiles[0].ShiftEnterLineBreaks);
        Assert.Equal(DictationTextFormat.Plain, settings.Profiles[1].TextFormat);
        Assert.Null(settings.Profiles[1].InjectionMethod);
    }

    [Theory]
    [InlineData(-1, "Plain")]
    [InlineData(0, "Plain")]
    [InlineData(1, "MarkdownSource")]
    [InlineData(2, "Plain")]
    public void Global_and_profile_formats_write_only_the_supported_string_tokens(int value, string expected)
    {
        var settings = new AppSettings
        {
            DefaultTextFormat = (DictationTextFormat)value,
            Profiles = [new() { TextFormat = (DictationTextFormat)value }, new() { TextFormat = null }],
        };
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(settings, Json));
        var format = document.RootElement.GetProperty("defaultTextFormat");
        Assert.Equal(JsonValueKind.String, format.ValueKind);
        Assert.Equal(expected, format.GetString());
        var profiles = document.RootElement.GetProperty("profiles");
        var profileFormat = profiles[0].GetProperty("textFormat");
        Assert.Equal(JsonValueKind.String, profileFormat.ValueKind);
        Assert.Equal(expected, profileFormat.GetString());
        Assert.Equal(JsonValueKind.Null, profiles[1].GetProperty("textFormat").ValueKind);
    }

    [Fact]
    public void SaveBundle_normalization_clone_builder_and_preset_instantiation_carry_every_new_field()
    {
        var profile = ProfileBuilder.Build(
            [new("Editor", " Code.exe, Code ", "Brief.", NewlineInjectionMode.KeepNewlines,
                DictationTextFormat.MarkdownSource, InjectionMethod.ClipboardPaste, false)])[0];
        var preset = new ProfilePresets.Preset("Test", profile);
        var settings = new AppSettings
        {
            AppAwareFormattingEnabled = true,
            DefaultTextFormat = DictationTextFormat.MarkdownSource,
            Profiles = [ProfilePresets.Instantiate(preset)],
        };
        var submitted = settings.Clone();
        settings.DefaultTextFormat = DictationTextFormat.Plain;
        settings.AppAwareFormattingEnabled = false;
        settings.Profiles[0].TextFormat = DictationTextFormat.Plain;
        settings.Profiles[0].InjectionMethod = InjectionMethod.UnicodeType;
        settings.Profiles[0].ShiftEnterLineBreaks = true;
        settings.Profiles[0].ProcessNames.Add("later");

        using var db = ScribeDatabase.CreateInMemory();
        var repository = new SettingsRepository(db);
        repository.SaveBundle(submitted, null, null);
        var saved = repository.Load();
        Assert.False(repository.LastLoadFailed);
        Assert.True(saved.AppAwareFormattingEnabled);
        Assert.Equal(DictationTextFormat.MarkdownSource, saved.DefaultTextFormat);
        AssertNewProfile(saved.Profiles[0]);
        AssertNewProfile(profile);
        AssertNewProfile(submitted.Profiles[0]);
        Assert.Equal(["Code"], saved.Profiles[0].ProcessNames);

        repository.Set(SettingsRepository.SettingsKey,
            """
            {"appAwareFormattingEnabled":true,"defaultTextFormat":"MarkdownSource","profiles":[null,
             {"name":null,"processNames":null,"textFormat":"MarkdownSource","injectionMethod":"ClipboardPaste","shiftEnterLineBreaks":false}]}
            """);
        var normalized = repository.Load();
        Assert.False(repository.LastLoadFailed);
        AssertNewProfile(Assert.Single(normalized.Profiles));
        Assert.Empty(normalized.Profiles[0].ProcessNames);
    }

    [Fact]
    public void An_old_reader_ignores_the_new_properties_and_an_old_resave_loses_them()
    {
        var settings = NewSettings();
        settings.Profiles[0].WritingStyle = "Still a legacy writing style.";
        var json = JsonSerializer.Serialize(settings, Json);
        var old = JsonSerializer.Deserialize<LegacySettings>(json, Json)!;

        Assert.Equal(settings.InjectionMethod, old.InjectionMethod);
        Assert.Equal(settings.ShiftEnterLineBreaks, old.ShiftEnterLineBreaks);
        Assert.Equal(settings.NewlineHandling, old.NewlineHandling);
        Assert.Equal(settings.Profiles[0].WritingStyle, old.Profiles[0].WritingStyle);
        Assert.Equal(settings.Profiles[0].NewlineHandling, old.Profiles[0].NewlineHandling);
        var reread = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(old, Json), Json)!;
        Assert.False(reread.AppAwareFormattingEnabled);
        Assert.Equal(DictationTextFormat.Plain, reread.DefaultTextFormat);
        Assert.Null(reread.Profiles[0].TextFormat);
        Assert.Null(reread.Profiles[0].InjectionMethod);
        Assert.Null(reread.Profiles[0].ShiftEnterLineBreaks);

        // A reader that recognizes a property but not its new enum name can still fail. No new names are put in either
        // existing enum, and the documentation warns that older re-saves discard the new choices.
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RecognizedButOlderFormat>(json, Json));
        Assert.Equal(["ClipboardPaste", "UnicodeType"], Enum.GetNames<InjectionMethod>());
        Assert.Equal(["SmartFlatten", "AlwaysFlatten", "KeepNewlines"], Enum.GetNames<NewlineInjectionMode>());
    }

    [Fact]
    public void Formatting_off_matches_the_frozen_legacy_output_and_delivery_oracle()
    {
        var random = new Random(74029);
        var inputs = new List<string>
        {
            "", "plain prose", "  first \r\n \tsecond\n\nthird  ", "- literal\n* markers\n1. kept",
            "```csharp\nvar url = \"https://example.test/a?b=1#c\";\n```",
            "Greek \u03C2, Cyrillic \u041F, emoji \U0001F680, cafe\u0301 and \u2028.",
            "user punctuation \u2014 and \u2013 stays", new string(['a', '\uD800', '\r', '\n', 'b']),
            string.Concat(Enumerable.Repeat("- long source\t \r\n", 1600)),
        };
        const string alphabet = "a B.*`#:/\r\n\t\u00A0\u212A\u041F";
        for (var i = 0; i < 80; i++)
        {
            inputs.Add(new string(Enumerable.Range(0, random.Next(1, 200)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray()));
        }

        foreach (var globalNewlines in Enum.GetValues<NewlineInjectionMode>())
        foreach (var profileNewlines in new NewlineInjectionMode?[] { null, NewlineInjectionMode.KeepNewlines, NewlineInjectionMode.AlwaysFlatten })
        foreach (var method in Enum.GetValues<InjectionMethod>())
        foreach (var shift in new[] { false, true })
        foreach (var target in new string?[] { null, "Code", "CODE.exe", "pwsh", "unknown", "msrdc" })
        {
            var settings = new AppSettings
            {
                AppAwareFormattingEnabled = false,
                DefaultTextFormat = DictationTextFormat.MarkdownSource,
                NewlineHandling = globalNewlines,
                InjectionMethod = method,
                ShiftEnterLineBreaks = shift,
                Profiles =
                [
                    new() { Name = "First", ProcessNames = ["Code", "pwsh", "msrdc"], NewlineHandling = profileNewlines,
                        TextFormat = DictationTextFormat.MarkdownSource, InjectionMethod = method == InjectionMethod.UnicodeType
                            ? InjectionMethod.ClipboardPaste : InjectionMethod.UnicodeType, ShiftEnterLineBreaks = !shift },
                    new() { Name = "Later", ProcessNames = ["Code"], NewlineHandling = NewlineInjectionMode.AlwaysFlatten,
                        TextFormat = DictationTextFormat.Plain },
                ],
            };
            var legacyProfile = LegacyMatch(settings.Profiles, target);
            var legacyNewlines = legacyProfile?.NewlineHandling ?? globalNewlines;
            var plan = DictationFormatPlan.Capture(settings, target, plainOnce: true);
            Assert.Equal(DictationFormatDecision.FormattingOff, plan.Decision);
            Assert.Equal(DictationTextFormat.Plain, plan.TextFormat);
            Assert.Equal(method, plan.InjectionMethod);
            Assert.Equal(shift, plan.ShiftEnterLineBreaks);
            Assert.Equal(legacyNewlines, plan.NewlineHandling);
            Assert.False(settings.EnableAiCleanup);
            foreach (var input in inputs)
            {
                var expected = LegacyApply(input, legacyNewlines, target);
                var represented = plan.Represent(input);
                Assert.Equal(Utf16(expected), Utf16(represented));
                foreach (var space in new[] { false, true })
                {
                    var legacyTyped = space && expected.Length > 0 && !char.IsWhiteSpace(expected[^1]) ? expected + " " : expected;
                    Assert.Equal(Utf16(legacyTyped), Utf16(DictationInsertion.TextToType(represented, space)));
                }
            }
        }
    }

    [Fact]
    public void On_uses_only_the_first_matching_profile_and_unmatched_or_explicitly_plain_targets_stay_plain()
    {
        var settings = NewSettings();
        settings.Profiles.Add(new AppProfile
        {
            ProcessNames = ["Code"], TextFormat = DictationTextFormat.Plain,
            InjectionMethod = InjectionMethod.UnicodeType, ShiftEnterLineBreaks = true,
        });
        var selected = DictationFormatPlan.Capture(settings, "code.EXE");
        Assert.Equal(DictationTextFormat.MarkdownSource, selected.TextFormat);
        Assert.Equal(InjectionMethod.ClipboardPaste, selected.InjectionMethod);
        Assert.False(selected.ShiftEnterLineBreaks);

        var unmatched = DictationFormatPlan.Capture(settings, "other");
        Assert.Equal(DictationTextFormat.Plain, unmatched.TextFormat);
        Assert.Equal(settings.InjectionMethod, unmatched.InjectionMethod);
        Assert.Equal(settings.ShiftEnterLineBreaks, unmatched.ShiftEnterLineBreaks);
        settings.Profiles[0].TextFormat = DictationTextFormat.Plain;
        Assert.Equal(DictationTextFormat.Plain, DictationFormatPlan.Capture(settings, "Code").TextFormat);
        Assert.False(settings.EnableAiCleanup);
    }

    [Fact]
    public void Nullable_overrides_inherit_and_a_later_save_cannot_change_the_admitted_plan()
    {
        var settings = NewSettings();
        settings.Profiles[0].TextFormat = null;
        settings.Profiles[0].InjectionMethod = null;
        settings.Profiles[0].ShiftEnterLineBreaks = null;
        var admitted = DictationFormatPlan.Capture(settings, "Code");
        Assert.Equal(settings.DefaultTextFormat, admitted.TextFormat);
        Assert.Equal(settings.InjectionMethod, admitted.InjectionMethod);
        Assert.Equal(settings.ShiftEnterLineBreaks, admitted.ShiftEnterLineBreaks);

        settings.AppAwareFormattingEnabled = false;
        settings.Profiles[0].NewlineHandling = NewlineInjectionMode.AlwaysFlatten;
        settings.InjectionMethod = InjectionMethod.ClipboardPaste;
        settings.ShiftEnterLineBreaks = false;
        Assert.Equal(DictationTextFormat.MarkdownSource, admitted.TextFormat);
        Assert.Equal("a\nb", admitted.Represent("a\nb"));
        Assert.Equal(InjectionMethod.UnicodeType, admitted.InjectionMethod);
        Assert.True(admitted.ShiftEnterLineBreaks);
        Assert.Equal("a b", DictationFormatPlan.Capture(settings, "Code").Represent("a\nb"));
    }

    [Fact]
    public void Stock_flatten_presets_are_rejected_through_production_validation_and_runtime_paths_without_changing_them()
    {
        foreach (var preset in new[] { ProfilePresets.TerminalsAndShells, ProfilePresets.IdeIntegratedTerminals, ProfilePresets.AiChatAndAgents, ProfilePresets.Teams })
        {
            var profile = ProfilePresets.Instantiate(preset);
            profile.TextFormat = DictationTextFormat.MarkdownSource;
            profile.InjectionMethod = InjectionMethod.ClipboardPaste;
            profile.ShiftEnterLineBreaks = false;
            var settings = new AppSettings { AppAwareFormattingEnabled = true, Profiles = [profile] };
            var plan = DictationFormatPlan.Capture(settings, profile.ProcessNames[0]);
            Assert.Equal(DictationFormatDecision.MarkdownNewlineConflict, plan.Decision);
            Assert.Equal(DictationTextFormat.Plain, plan.TextFormat);
            Assert.Equal(settings.InjectionMethod, plan.InjectionMethod);
            Assert.Equal(settings.ShiftEnterLineBreaks, plan.ShiftEnterLineBreaks);
            Assert.Equal("one two", plan.Represent("one\n\ntwo"));
            var issue = Assert.Single(SettingsDraftValidator.Validate(new SettingsDraft(settings)));
            Assert.Equal(ValidationCode.MarkdownNewlineConflict, issue.Code);
            Assert.Equal(ValidationSeverity.Blocking, issue.Severity);
            Assert.Contains("Plain text", issue.Message, StringComparison.Ordinal);
            Assert.Contains("Keep line breaks", issue.Message, StringComparison.Ordinal);
            Assert.Equal(NewlineInjectionMode.AlwaysFlatten, profile.NewlineHandling);
            Assert.Equal(NewlineInjectionMode.AlwaysFlatten, preset.Profile.NewlineHandling);

            settings.AppAwareFormattingEnabled = false;
            Assert.Empty(SettingsDraftValidator.Validate(new SettingsDraft(settings)));
        }
    }

    [Theory]
    [InlineData("pwsh")]
    [InlineData("WindowsTerminal.exe")]
    public void Terminal_smart_flatten_and_inherited_always_flatten_refuse_new_preferences(string process)
    {
        var settings = NewSettings();
        settings.Profiles[0].ProcessNames = [process];
        settings.Profiles[0].NewlineHandling = null;
        settings.NewlineHandling = NewlineInjectionMode.SmartFlatten;
        Assert.Contains(SettingsDraftValidator.Validate(new SettingsDraft(settings)), issue => issue.Code == ValidationCode.MarkdownNewlineConflict);
        var target = process.Replace(".exe", "", StringComparison.Ordinal);
        Assert.Equal(DictationFormatDecision.MarkdownNewlineConflict, DictationFormatPlan.Capture(settings, target).Decision);
        settings.NewlineHandling = NewlineInjectionMode.KeepNewlines;
        Assert.Empty(SettingsDraftValidator.Validate(new SettingsDraft(settings)));
        Assert.Equal(DictationFormatDecision.MarkdownSource, DictationFormatPlan.Capture(settings, target).Decision);

        settings.NewlineHandling = NewlineInjectionMode.AlwaysFlatten;
        Assert.Contains(SettingsDraftValidator.Validate(new SettingsDraft(settings)), issue => issue.ControlName == "ProfileTextFormatCombo");
    }

    [Fact]
    public void Validation_uses_the_effective_profile_newlines_not_an_unused_global_one_line_setting()
    {
        var settings = NewSettings();
        settings.NewlineHandling = NewlineInjectionMode.AlwaysFlatten;
        settings.Profiles[0].TextFormat = null;
        Assert.Empty(SettingsDraftValidator.Validate(new SettingsDraft(settings)));
        Assert.Equal(DictationTextFormat.MarkdownSource, DictationFormatPlan.Capture(settings, "Code").TextFormat);
        settings.Profiles[0].NewlineHandling = null;
        Assert.Contains(SettingsDraftValidator.Validate(new SettingsDraft(settings)), issue => issue.Code == ValidationCode.MarkdownNewlineConflict);
        settings.Profiles.Clear();
        Assert.Empty(SettingsDraftValidator.Validate(new SettingsDraft(settings)));
        Assert.Equal(DictationTextFormat.Plain, DictationFormatPlan.Capture(settings, "Code").TextFormat);
    }

    [Fact]
    public void A_newline_conflict_selects_the_offending_profile_after_empty_placeholders_are_filtered()
    {
        ProfileDraftRow[] rows =
        [
            new("placeholder", DraftRowOrigin.New, false, "", ""),
            new("plain-row", DraftRowOrigin.New, true, "Editor", "Code",
                NewlineHandling: NewlineInjectionMode.KeepNewlines, TextFormat: DictationTextFormat.Plain),
            new("conflicting-row", DraftRowOrigin.New, true, "Command window", "pwsh",
                NewlineHandling: NewlineInjectionMode.AlwaysFlatten, TextFormat: DictationTextFormat.MarkdownSource),
        ];
        var settings = new AppSettings
        {
            AppAwareFormattingEnabled = true,
            Profiles = ProfileBuilder.Build(rows.Select(row => new ProfileBuilder.Row(
                row.Name, row.Apps, row.WritingStyle, row.NewlineHandling,
                row.TextFormat, row.InjectionMethod, row.ShiftEnterLineBreaks)).ToList()),
        };

        Assert.Equal(2, settings.Profiles.Count);
        var issue = Assert.Single(SettingsDraftValidator.Validate(new SettingsDraft(settings, ProfileRows: rows)));
        Assert.Equal(ValidationCode.MarkdownNewlineConflict, issue.Code);
        Assert.Equal(ValidationSeverity.Blocking, issue.Severity);
        Assert.Equal(SettingsPage.AppProfiles, issue.Page);
        Assert.Equal("ProfileTextFormatCombo", issue.ControlName);
        Assert.Equal("conflicting-row", issue.RowKey);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Every_new_edit_is_dirty_and_keeps_a_held_save_open(int field)
    {
        var submitted = NewSettings();
        var editing = submitted.Clone();
        var held = new TaskCompletionSource<VocabularyRefresh>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watching = StoredChangeAcknowledgement.Watch(held.Task, () => Draft(editing)).CompleteAsync();
        var baseline = new SettingsSaveBaseline<AppSettings>(submitted.Clone());
        var submission = baseline.Submit(submitted);
        Edit(editing, field);
        held.SetResult(new VocabularyRefresh(VocabularyRefreshOutcome.Applied, VocabularyGeneration.Empty));
        Assert.Equal(StoredChangeOutcome.ChangedWhileSaving, await watching);
        Assert.True(baseline.Complete(submission));
        Assert.Contains(field < 2 ? SettingsPage.Dictation : SettingsPage.AppProfiles,
            SettingsChangeTracker.Compare(baseline.Current, editing).Pages);
        Assert.Equal(Draft(submitted), Draft(baseline.Current));
        Assert.NotEqual(Draft(submitted), Draft(editing));
    }

    [Fact]
    public void New_profile_fields_are_compared_in_loaded_rows_and_prevent_placeholder_loss()
    {
        var settings = NewSettings();
        var before = new LoadedProfileDraftRow("row", "Editor", "Code", "", NewlineInjectionMode.KeepNewlines,
            DictationTextFormat.MarkdownSource, InjectionMethod.ClipboardPaste, false);
        var draft = new ProfileDraftRow("row", DraftRowOrigin.Saved, false, "Editor", "Code", "Editor", "Code", "", "",
            NewlineInjectionMode.KeepNewlines, NewlineInjectionMode.KeepNewlines,
            DictationTextFormat.MarkdownSource, DictationTextFormat.MarkdownSource, InjectionMethod.ClipboardPaste,
            InjectionMethod.ClipboardPaste, false, false);
        Assert.False(SettingsChangeTracker.Compare(settings, settings.Clone(), profileRows: [draft], loadedProfileRows: [before]).IsDirty);
        foreach (var edited in new[]
        {
            draft with { TextFormat = DictationTextFormat.Plain },
            draft with { InjectionMethod = InjectionMethod.UnicodeType },
            draft with { ShiftEnterLineBreaks = true },
        })
        {
            Assert.Contains(SettingsPage.AppProfiles,
                SettingsChangeTracker.Compare(settings, settings.Clone(), profileRows: [edited], loadedProfileRows: [before]).Pages);
        }

        foreach (var nonempty in new[]
        {
            new ProfileDraftRow("new", DraftRowOrigin.New, true, "", "", TextFormat: DictationTextFormat.Plain),
            new ProfileDraftRow("new", DraftRowOrigin.New, true, "", "", InjectionMethod: InjectionMethod.UnicodeType),
            new ProfileDraftRow("new", DraftRowOrigin.New, true, "", "", ShiftEnterLineBreaks: false),
        })
        {
            Assert.False(SettingsDraftValidator.IsPlaceholder(nonempty));
            Assert.Contains(SettingsDraftValidator.Validate(new SettingsDraft(settings, ProfileRows: [nonempty])),
                issue => issue.Code == ValidationCode.ProfileNameEmpty && issue.Severity == ValidationSeverity.Blocking);
        }
    }

    internal static AppSettings NewSettings() => new()
    {
        AppAwareFormattingEnabled = true,
        DefaultTextFormat = DictationTextFormat.MarkdownSource,
        Profiles =
        [
            new() { Name = "Editor", ProcessNames = ["Code"], NewlineHandling = NewlineInjectionMode.KeepNewlines,
                TextFormat = DictationTextFormat.MarkdownSource, InjectionMethod = InjectionMethod.ClipboardPaste,
                ShiftEnterLineBreaks = false },
        ],
    };

    private static void AssertNewProfile(AppProfile profile)
    {
        Assert.Equal(DictationTextFormat.MarkdownSource, profile.TextFormat);
        Assert.Equal(InjectionMethod.ClipboardPaste, profile.InjectionMethod);
        Assert.False(profile.ShiftEnterLineBreaks);
    }

    private static string Draft(AppSettings settings) => new DraftSnapshot()
        .Flag(settings.AppAwareFormattingEnabled).Number((long)settings.DefaultTextFormat).Profiles(settings.Profiles).Hash();

    private static void Edit(AppSettings settings, int field)
    {
        switch (field)
        {
            case 0: settings.AppAwareFormattingEnabled = false; break;
            case 1: settings.DefaultTextFormat = DictationTextFormat.Plain; break;
            case 2: settings.Profiles[0].TextFormat = DictationTextFormat.Plain; break;
            case 3: settings.Profiles[0].InjectionMethod = InjectionMethod.UnicodeType; break;
            case 4: settings.Profiles[0].ShiftEnterLineBreaks = true; break;
        }
    }

    private static byte[] Utf16(string value) => MemoryMarshal.AsBytes(value.AsSpan()).ToArray();

    // Frozen from df474f2a's matcher and formatter. The new path is compared to actual old decisions and bytes, not to
    // itself or to a source-string assertion. The terminal list here is only the test's oracle, never product policy.
    private static AppProfile? LegacyMatch(IReadOnlyList<AppProfile> profiles, string? process)
    {
        if (string.IsNullOrWhiteSpace(process)) return null;
        static string Normalize(string text) => text.Trim().EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? text.Trim()[..^4] : text.Trim();
        return profiles.FirstOrDefault(profile => profile.ProcessNames.Any(candidate =>
            !string.IsNullOrWhiteSpace(candidate) && string.Equals(Normalize(candidate), Normalize(process), StringComparison.OrdinalIgnoreCase)));
    }

    internal static string LegacyApply(string text, NewlineInjectionMode mode, string? target)
    {
        string[] terminalNames =
        [
            "WindowsTerminal", "wt", "OpenConsole", "conhost", "cmd", "powershell", "pwsh", "alacritty", "wezterm",
            "wezterm-gui", "ConEmu", "ConEmu64", "mintty", "Hyper", "Tabby", "putty", "kitty", "warp",
        ];
        var flatten = mode == NewlineInjectionMode.AlwaysFlatten ||
            mode == NewlineInjectionMode.SmartFlatten && !string.IsNullOrWhiteSpace(target) &&
            terminalNames.Contains(target.Trim(), StringComparer.OrdinalIgnoreCase);
        return !flatten || text.IndexOfAny(['\r', '\n']) < 0 ? text : Regex.Replace(text, @"[ \t]*[\r\n]+[ \t]*", " ").Trim();
    }

    public sealed class LegacySettings
    {
        public InjectionMethod InjectionMethod { get; set; } = InjectionMethod.UnicodeType;
        public NewlineInjectionMode NewlineHandling { get; set; } = NewlineInjectionMode.SmartFlatten;
        public bool ShiftEnterLineBreaks { get; set; } = true;
        public List<LegacyProfile> Profiles { get; set; } = [];
    }

    public sealed class LegacyProfile
    {
        public string? Name { get; set; }
        public List<string> ProcessNames { get; set; } = [];
        public string? WritingStyle { get; set; }
        public NewlineInjectionMode? NewlineHandling { get; set; }
    }

    public enum OlderFormat { Plain }
    public sealed class RecognizedButOlderFormat
    {
        public OlderFormat DefaultTextFormat { get; set; }
    }
}
