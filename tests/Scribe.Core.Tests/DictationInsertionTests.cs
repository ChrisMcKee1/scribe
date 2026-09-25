using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;
using Scribe.Core.Tests.Concurrency;
using Scribe.Core.TextInjection;
using static Scribe.Core.TextInjection.InjectionNativeMethods;

namespace Scribe.Core.Tests;

/// <summary>
/// Issue #78: with <see cref="AppSettings.AddSpaceAfterDictation"/> on, the default, the target is given one space after
/// each dictation, so back-to-back dictations do not run together, while everything that keeps the text (history, the
/// tray's recent dictations, the recovery notice's copy, quick add, the playground's report) keeps it as dictated. The
/// step runs through the real <see cref="TextInjector"/> over scripted input and clipboard boundaries, so every
/// insertion path is covered: typing, the clipboard paste, the paste's typing fallback and a standard edit control.
/// </summary>
public sealed class DictationInsertionTests
{
    private const nint Target = 0x4242;

    // --- The rule -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Hello world.", "Hello world. ")]
    [InlineData("Is it ready?", "Is it ready? ")]
    [InlineData("hello", "hello ")]
    [InlineData("a", "a ")]
    public void One_space_follows_a_dictation_that_does_not_end_in_white_space(string text, string typed) =>
        Assert.Equal(typed, DictationInsertion.TextToType(text, addSpaceAfterDictation: true));

    [Theory]
    [InlineData("ends in a space ")]
    [InlineData("two spaces already  ")]
    [InlineData("ends in a tab\t")]
    [InlineData("ends in a line feed\n")]
    [InlineData("ends in a carriage return\r")]
    [InlineData("ends in a Windows line break\r\n")]
    [InlineData("ends in a vertical tab\u000B")]
    [InlineData("ends in a form feed\u000C")]
    [InlineData("ends in a next line\u0085")]
    [InlineData("ends in a no-break space\u00A0")]
    [InlineData("ends in a narrow no-break space\u202F")]
    [InlineData("ends in an em space\u2003")]
    [InlineData("ends in an ideographic space\u3000")]
    [InlineData("ends in a line separator\u2028")]
    [InlineData("ends in a paragraph separator\u2029")]
    public void A_dictation_already_ending_in_white_space_is_typed_as_it_is(string text) =>
        Assert.Same(text, DictationInsertion.TextToType(text, addSpaceAfterDictation: true));

    [Fact]
    public void Every_white_space_character_counts_as_already_there_and_no_other_character_does()
    {
        var wrong = new List<string>();
        for (var code = 0; code <= char.MaxValue; code++)
        {
            var last = (char)code;
            var text = "word" + last;
            var typed = DictationInsertion.TextToType(text, addSpaceAfterDictation: true);
            var expected = char.IsWhiteSpace(last) ? text : text + " ";
            if (!string.Equals(expected, typed, StringComparison.Ordinal))
            {
                wrong.Add($"U+{code:X4}");
            }
        }

        Assert.Empty(wrong);
    }

    [Theory]
    [InlineData("Ship it \U0001F680")] // an emoji is a surrogate pair: the space goes after both halves
    [InlineData("cafe\u0301")] // a combining accent ends the last letter, and the space follows it
    [InlineData("\u4F60\u597D\u3002")] // an ideographic full stop is punctuation, not a space
    [InlineData("\u05E9\u05DC\u05D5\u05DD")] // right-to-left text
    [InlineData("\u00A1Hola!")]
    [InlineData("zero width space\u200B")] // a format character to .NET, not white space
    public void Unicode_endings_that_are_not_white_space_get_one_space_after_them(string text)
    {
        var typed = DictationInsertion.TextToType(text, addSpaceAfterDictation: true);

        Assert.Equal(text + " ", typed);
        Assert.Equal(text, typed[..^1]);
    }

    [Theory]
    [InlineData("Hello world.")]
    [InlineData("ends in a space ")]
    [InlineData("")]
    public void Turned_off_the_text_is_typed_exactly(string text) =>
        Assert.Same(text, DictationInsertion.TextToType(text, addSpaceAfterDictation: false));

    [Fact]
    public void A_dictation_that_inserts_nothing_is_given_no_space()
    {
        Assert.Same(string.Empty, DictationInsertion.TextToType(string.Empty, addSpaceAfterDictation: true));

        var rig = new Rig();
        var insertion = rig.Insert(string.Empty, InjectionMethod.UnicodeType);

        Assert.False(insertion.SpaceAdded);
        Assert.Empty(rig.Platform.Batches);
        Assert.Equal("none", insertion.Injection.Method);
        Assert.Empty(rig.Recovery.GetRecent());
    }

    [Fact]
    public void The_space_is_added_once_and_never_doubled()
    {
        var once = DictationInsertion.TextToType("Hello.", addSpaceAfterDictation: true);

        Assert.Equal("Hello. ", once);
        Assert.Same(once, DictationInsertion.TextToType(once, addSpaceAfterDictation: true));
    }

    [Fact]
    public void A_missing_text_is_a_caller_bug()
    {
        Assert.Throws<ArgumentNullException>(() => DictationInsertion.TextToType(null!, addSpaceAfterDictation: true));
        Assert.Throws<ArgumentNullException>(() =>
            DictationInsertion.Insert(null!, true, new LastTranscriptStore(), _ => InjectionResult.Empty));
    }

    // --- Every insertion path, through the real injector ------------------------------------------------------------

    [Fact]
    public void Typing_gives_the_target_the_space_and_keeps_the_dictation_without_it()
    {
        var rig = new Rig();

        var insertion = rig.Insert("Send the report today.", InjectionMethod.UnicodeType);

        Assert.True(insertion.Injection.Succeeded);
        Assert.Equal("unicode", insertion.Injection.Method);
        Assert.Equal("Send the report today. ", rig.Typed());
        Assert.True(insertion.SpaceAdded);
        Assert.Equal("Send the report today.", insertion.Recorded);
        Assert.Equal(["Send the report today."], rig.Recovery.GetRecent());
    }

    [Fact]
    public void Back_to_back_dictations_arrive_separated_while_the_tray_keeps_each_as_dictated()
    {
        var rig = new Rig();

        rig.Insert("First thought.", InjectionMethod.UnicodeType);
        rig.Insert("Second thought.", InjectionMethod.UnicodeType);

        Assert.Equal("First thought. Second thought. ", rig.Typed());
        Assert.Equal(["Second thought.", "First thought."], rig.Recovery.GetRecent());
    }

    [Fact]
    public void Pasting_gives_the_target_the_space_and_puts_the_user_s_clipboard_back()
    {
        var rig = new Rig();
        rig.Clipboard.SeedText("what the user had copied");
        string? pasted = null;
        rig.Platform.OnSleep = ms =>
        {
            if (ms == TextInjector.PasteSettleDelayMs)
            {
                pasted = rig.Clipboard.TargetReads();
            }
        };

        var insertion = rig.Insert("Paste me.", InjectionMethod.ClipboardPaste);

        Assert.True(insertion.Injection.Succeeded);
        Assert.Equal(PasteDelivery.ChordInserted, insertion.Injection.Paste);
        Assert.Equal("Paste me. ", pasted);
        Assert.Equal(ClipboardRestoreOutcome.Restored, insertion.Injection.ClipboardRestore);
        Assert.Equal("what the user had copied", rig.Clipboard.Text);
        Assert.Equal(0, rig.Platform.UnicodeEvents);
        Assert.Equal(["Paste me."], rig.Recovery.GetRecent());
    }

    [Fact]
    public void A_paste_that_falls_back_to_typing_types_the_space_too()
    {
        // An image on the clipboard cannot be saved and put back, so the injector types instead.
        var rig = new Rig();
        rig.Clipboard.SeedFormats(TextInjectionFakes.CF_DIB);

        var insertion = rig.Insert("Keep my screenshot.", InjectionMethod.ClipboardPaste);

        Assert.True(insertion.Injection.Succeeded);
        Assert.Equal(PasteDelivery.NonTextContent, insertion.Injection.Paste);
        Assert.Equal("Keep my screenshot. ", rig.Typed());
        Assert.Equal(["Keep my screenshot."], rig.Recovery.GetRecent());
    }

    [Fact]
    public void A_standard_edit_control_is_given_the_space_too()
    {
        var rig = new Rig();
        rig.Platform.StandardEdit = true;

        var insertion = rig.Insert("Notepad text.", InjectionMethod.UnicodeType);

        Assert.Equal("win32-edit", insertion.Injection.Method);
        Assert.Equal(["Notepad text. "], rig.Platform.StandardEditTexts);
        Assert.Empty(rig.Platform.Batches);
        Assert.Equal(["Notepad text."], rig.Recovery.GetRecent());
    }

    [Theory]
    [InlineData(InjectionMethod.UnicodeType)]
    [InlineData(InjectionMethod.ClipboardPaste)]
    public void Turned_off_the_target_is_given_exactly_the_dictation(InjectionMethod method)
    {
        var rig = new Rig();
        rig.Clipboard.SeedText("what the user had copied");
        string? pasted = null;
        rig.Platform.OnSleep = ms =>
        {
            if (ms == TextInjector.PasteSettleDelayMs)
            {
                pasted = rig.Clipboard.TargetReads();
            }
        };

        var insertion = rig.Insert("No space please.", method, addSpace: false);

        Assert.True(insertion.Injection.Succeeded);
        Assert.False(insertion.SpaceAdded);
        Assert.Equal("No space please.", method == InjectionMethod.UnicodeType ? rig.Typed() : pasted);
        Assert.Equal(["No space please."], rig.Recovery.GetRecent());
    }

    [Fact]
    public void Dictionary_output_is_given_the_space_and_a_snippet_ending_in_a_line_break_is_not()
    {
        using var db = ScribeDatabase.CreateInMemory();
        var dictionary = new DictionaryRepository(db);
        dictionary.AddRange([DictionaryEntry.New("dot net", ".NET")]);
        var snippets = new SnippetRepository(db);
        snippets.SaveAll([Snippet.New("insert my sign off", "Best regards,\nChris\n")]);
        var processor = new TextPostProcessor(dictionary, NullLogger<TextPostProcessor>.Instance, snippets);

        var corrected = processor.Process("we ship on dot net");
        var signOff = processor.Process("Insert my sign off.");
        Assert.Equal("we ship on .NET", corrected);
        Assert.Equal("Best regards,\nChris\n", signOff);

        var rig = new Rig();
        var first = rig.Insert(corrected, InjectionMethod.UnicodeType, shiftEnter: false);
        var second = rig.Insert(signOff, InjectionMethod.UnicodeType, shiftEnter: false);

        Assert.True(first.SpaceAdded);
        Assert.False(second.SpaceAdded);
        Assert.Equal("we ship on .NET Best regards,\nChris\n", rig.Typed());
        Assert.Equal(["Best regards,\nChris\n", "we ship on .NET"], rig.Recovery.GetRecent());
    }

    [Fact]
    public void A_single_line_target_is_given_the_space_after_its_line_breaks_are_flattened()
    {
        // The controller handles line breaks first. Flattening trims, so a space added before it would be lost.
        const string dictated = "Line one.\nLine two.";
        Assert.Equal("Line one. Line two.", InjectionTextFormatter.Apply(
            DictationInsertion.TextToType(dictated, addSpaceAfterDictation: true), NewlineInjectionMode.SmartFlatten, "pwsh"));

        var flattened = InjectionTextFormatter.Apply(dictated, NewlineInjectionMode.SmartFlatten, "pwsh");
        var rig = new Rig();
        var insertion = rig.Insert(flattened, InjectionMethod.UnicodeType);

        Assert.Equal("Line one. Line two. ", rig.Typed());
        Assert.True(insertion.SpaceAdded);
        Assert.Equal(["Line one. Line two."], rig.Recovery.GetRecent());
    }

    // --- Failure, partial insertion and shutdown -----------------------------------------------------------------

    [Fact]
    public void A_failed_insertion_leaves_the_dictation_copyable_without_the_space()
    {
        var rig = new Rig();
        rig.Platform.Foreground = 0x9999; // focus moved to another window before anything was typed

        var insertion = rig.Insert("Focus moved.", InjectionMethod.UnicodeType);

        Assert.False(insertion.Injection.Succeeded);
        Assert.Equal(InjectionResult.FocusChangedError, insertion.Injection.Error);
        Assert.Empty(rig.Platform.Batches);
        Assert.Equal("Focus moved.", insertion.Recorded);

        // What the tray's recent dictations and the recovery notice's copy give the user.
        Assert.Equal(["Focus moved."], rig.Recovery.GetRecent());
        Assert.Equal("Focus moved.", rig.Recovery.Get());
    }

    [Fact]
    public void A_partial_insertion_counts_the_space_as_due_and_keeps_the_whole_dictation_without_it()
    {
        // Windows accepts the first two characters and then nothing more.
        var rig = new Rig();
        rig.Platform.Deliver = (batch, inputs) => batch == 0 ? 4u : 0u;

        var insertion = rig.Insert("Only part arrives.", InjectionMethod.UnicodeType, shiftEnter: false);

        Assert.False(insertion.Injection.Succeeded);
        Assert.Equal(4, insertion.Injection.Sent);
        Assert.Equal(TextInjector.CountKeyEvents("Only part arrives. ", 0, 19, shiftEnter: false), insertion.Injection.Total);
        Assert.Equal(["Only part arrives."], rig.Recovery.GetRecent());
    }

    [Fact]
    public void Shutdown_before_insertion_types_nothing_and_keeps_the_dictation()
    {
        var rig = new Rig();
        using var shutdown = new CancellationTokenSource();
        shutdown.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            rig.Insert("Too late.", InjectionMethod.UnicodeType, cancellationToken: shutdown.Token));

        Assert.Empty(rig.Injected);
        Assert.Empty(rig.Platform.Batches);
        Assert.Equal(["Too late."], rig.Recovery.GetRecent());
    }

    // --- The pipeline's tail, and the controller that runs it -----------------------------------------------------

    [Fact]
    public void The_target_gets_the_space_while_history_and_the_recovery_copy_keep_the_dictation()
    {
        // The end of DictationController's processing through the real Core pieces it uses, with the defaults a new
        // install has: line-break handling for the target, the insertion step, and history given what the step recorded.
        using var db = ScribeDatabase.CreateInMemory();
        var history = new HistoryRepository(db);
        using var writer = new HistoryWriter(history, NullLogger<HistoryWriter>.Instance);
        var settings = AppSettings.CreateDefault();
        var rig = new Rig();

        var text = InjectionTextFormatter.Apply("Ship the fix today.", settings.NewlineHandling, "WINWORD");
        var insertion = rig.Insert(text, settings.InjectionMethod, settings.AddSpaceAfterDictation, settings.ShiftEnterLineBreaks);
        Assert.True(writer.Enqueue(new HistoryEntry(0, DateTimeOffset.UtcNow, insertion.Recorded, 1200, 40), audio: null));
        Assert.True(writer.WaitForAcceptedWrites(BlockedThreads.SafetyTimeout));

        Assert.Equal("Ship the fix today. ", rig.Typed());
        Assert.Equal("Ship the fix today. ", insertion.Typed);
        Assert.Equal("Ship the fix today.", Assert.Single(history.GetRecent()).Text);
        Assert.Equal(["Ship the fix today."], rig.Recovery.GetRecent());

        // After a restart the tray and quick add read the ring seeded from history, which has no space either.
        var restarted = new LastTranscriptStore();
        restarted.Seed(history.GetRecent().Select(entry => entry.Text));
        Assert.Equal(["Ship the fix today."], restarted.GetRecent());
    }

    [Fact]
    public void The_controller_types_only_through_the_insertion_step_and_keeps_what_it_recorded()
    {
        var controller = ReadSource("src", "Scribe.App", "Dictation", "DictationController.cs");

        // One way into the target, inside the step, which keeps the recovery copy itself.
        Assert.Single(Regex.Matches(controller, Regex.Escape("_injector.Inject(")));
        Assert.DoesNotMatch(@"_lastTranscript\.Set\(", controller);
        var insert = controller.IndexOf("DictationInsertion.Insert(", StringComparison.Ordinal);
        Assert.True(insert > 0, "the controller inserts through DictationInsertion.Insert");
        var call = controller[insert..controller.IndexOf("cancellationToken);", insert, StringComparison.Ordinal)];
        Assert.Contains("settings.AddSpaceAfterDictation,", call, StringComparison.Ordinal);
        Assert.Contains("_lastTranscript,", call, StringComparison.Ordinal);
        Assert.Contains("typed => _injector.Inject(", call, StringComparison.Ordinal);
        Assert.Contains("typed, settings.InjectionMethod, session.TargetWindow, settings.ShiftEnterLineBreaks", call, StringComparison.Ordinal);

        // After AI cleanup (its guards and the dash normalizer run inside CleanAsync), the dictionary and snippets, and the
        // line breaks handled for the target, which trims; the report keeps the text as dictated.
        var cleanup = controller.IndexOf(".CleanAsync(recognized, cancellationToken, cleanupWritingStyle)", StringComparison.Ordinal);
        var postProcess = controller.IndexOf("_postProcessor.ProcessDetailed(recognized, result.Text)", StringComparison.Ordinal);
        var flatten = controller.IndexOf("InjectionTextFormatter.Apply(text, newlineMode, targetApp)", StringComparison.Ordinal);
        var report = controller.IndexOf("report.FinalText = text;", StringComparison.Ordinal);
        Assert.InRange(cleanup, 1, postProcess);
        Assert.InRange(postProcess, cleanup, flatten);
        Assert.InRange(flatten, postProcess, report);
        Assert.InRange(report, flatten, insert);
        Assert.Single(Regex.Matches(controller, Regex.Escape("report.FinalText =")));

        // History and the Dictated event get what the step recorded, and history stores its text as given.
        Assert.Single(Regex.Matches(controller, @"\bEnqueueHistory\(session\.Id"));
        Assert.Contains(
            "EnqueueHistory(session.Id, settings, audio, result, insertion.Recorded, targetApp, cleanup, report.CleanupDuration);",
            controller,
            StringComparison.Ordinal);
        Assert.Contains("Dictated?.Invoke(insertion.Recorded);", controller, StringComparison.Ordinal);
        var enqueue = controller.IndexOf("private void EnqueueHistory(", StringComparison.Ordinal);
        Assert.Contains("Text: text,", controller[enqueue..controller.IndexOf("\n    }", enqueue, StringComparison.Ordinal)], StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_Dictation_shows_saves_and_names_the_switch()
    {
        var xaml = ReadSource("src", "Scribe.App", "Settings", "SettingsWindow.xaml");
        var code = ReadSource("src", "Scribe.App", "Settings", "SettingsWindow.xaml.cs");

        // On the Dictation page, in the Text insertion group, last in its Tab order, named by its title for screen readers.
        var page = xaml[xaml.IndexOf("x:Name=\"SectionDictation\"", StringComparison.Ordinal)..
            xaml.IndexOf("x:Name=\"SectionOverlay\"", StringComparison.Ordinal)];
        var toggle = page.IndexOf("x:Name=\"SpaceAfterDictationCheck\"", StringComparison.Ordinal);
        Assert.True(toggle > page.IndexOf("Text=\"Text insertion\"", StringComparison.Ordinal));
        Assert.True(toggle > page.IndexOf("x:Name=\"ShiftEnterCheck\"", StringComparison.Ordinal));
        Assert.Contains("<TextBlock x:Name=\"SpaceAfterDictationTitle\" Text=\"Add a space after each dictation\"", page, StringComparison.Ordinal);
        Assert.Contains(
            "Text=\"Makes back-to-back dictations flow. Turn it off if an app needs text without a trailing space.\"",
            page,
            StringComparison.Ordinal);
        var element = page[toggle..page.IndexOf("/>", toggle, StringComparison.Ordinal)];
        Assert.Contains("AutomationProperties.LabeledBy=\"{Binding ElementName=SpaceAfterDictationTitle}\"", element, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.HelpText=\"{Binding Text, ElementName=SpaceAfterDictationHint}\"", element, StringComparison.Ordinal);
        Assert.DoesNotContain("IsTabStop=\"False\"", element, StringComparison.Ordinal);
        Assert.DoesNotContain("TabIndex", element, StringComparison.Ordinal);

        // Shown from the settings the window loaded, and written back only by Save, like the page's other switches.
        Assert.Single(Regex.Matches(code, Regex.Escape("SpaceAfterDictationCheck.IsChecked = _settings.AddSpaceAfterDictation;")));
        Assert.Single(Regex.Matches(code, Regex.Escape("_settings.AddSpaceAfterDictation = SpaceAfterDictationCheck.IsChecked == true;")));
        Assert.Single(Regex.Matches(code, @"\.AddSpaceAfterDictation\s*=[^=]"));
    }

    private static string ReadSource(params string[] parts)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Scribe.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine([root.FullName, .. parts]));
    }

    /// <summary>The real injector over scripted input and clipboard boundaries, and the tray's real recovery ring.</summary>
    private sealed class Rig
    {
        public TextInjectionFakes.Platform Platform { get; } = new() { Foreground = Target };

        public TextInjectionFakes.Clipboard Clipboard { get; } = new();

        public LastTranscriptStore Recovery { get; } = new();

        /// <summary>Every text the step handed to the injector.</summary>
        public List<string> Injected { get; } = [];

        public DictationInsertionResult Insert(
            string text,
            InjectionMethod method,
            bool addSpace = true,
            bool shiftEnter = true,
            CancellationToken cancellationToken = default)
        {
            var injector = new TextInjector(NullLogger<TextInjector>.Instance, Platform, Clipboard);
            return DictationInsertion.Insert(
                text,
                addSpace,
                Recovery,
                typed =>
                {
                    Injected.Add(typed);
                    return injector.Inject(typed, method, Target, shiftEnter);
                },
                cancellationToken);
        }

        /// <summary>What the target received as typing: each character as itself, each Return keypress as a line feed.</summary>
        public string Typed()
        {
            var text = new StringBuilder();
            foreach (var input in Platform.Batches.SelectMany(batch => batch))
            {
                var key = input.U.ki;
                if ((key.dwFlags & KEYEVENTF_KEYUP) != 0)
                {
                    continue;
                }

                if ((key.dwFlags & KEYEVENTF_UNICODE) != 0)
                {
                    text.Append((char)key.wScan);
                }
                else if (key.wVk == VK_RETURN)
                {
                    text.Append('\n');
                }
            }

            return text.ToString();
        }
    }
}
