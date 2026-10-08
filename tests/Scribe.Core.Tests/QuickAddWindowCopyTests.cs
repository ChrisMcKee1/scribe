using System.Reflection;
using System.Runtime.Loader;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Scribe.Core.Models;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

public sealed class QuickAddWindowCopyTests
{
    [Fact]
    public void The_footer_saves_a_pending_correction_then_copies_and_keeps_the_real_window_open() =>
        PrivateDesktopTest.Run(typeof(QuickAddWindowCopyTests), () =>
        {
            using var rig = new WindowRig();
            foreach (var scale in new[] { 1.0, 2.25 })
            {
                rig.SetTextScale(scale);
                rig.Open("ask cloud pilot and check azure");
                rig.Correction("cloud pilot", "Copilot");
                var copy = rig.Control<Button>("CopyFixedButton");
                Assert.True(copy.IsEnabled);
                Assert.Equal("Save and co_py", copy.Content);

                rig.Click("CopyFixedButton");

                Assert.True(rig.Window.IsVisible);
                Assert.Equal("ask Copilot and check azure", rig.Copies[^1]);
                Assert.Equal("ask Copilot and check azure", rig.Field<string>("_transcript"));
                Assert.Equal("", rig.Control<TextBox>("HeardBox").Text);
                Assert.Equal("", rig.Control<TextBox>("ShouldBeBox").Text);
                Assert.Equal("Co_py dictation", copy.Content);
                Assert.True(copy.IsEnabled);
                Assert.False(rig.Saved[^1].CloseAfterSaving);
                Assert.Equal("ask cloud pilot and check azure", rig.Saved[^1].SourceTranscript);
                Assert.Equal("ask Copilot and check azure", rig.Saved[^1].CorrectedTranscript);
                Assert.Equal(new[] { "persist", "saved", "copy" }, rig.Order);
                Assert.Equal(QuickDictionaryAdd.CopySucceeded().Message, rig.Control<TextBlock>("StatusText").Text);
                rig.Close();
                rig.Reset();
            }
        });

    [Fact]
    public void Normal_and_combined_saves_always_copy_the_latest_full_dictation_without_saving_twice() =>
        PrivateDesktopTest.Run(typeof(QuickAddWindowCopyTests), () =>
        {
            using var rig = new WindowRig();
            rig.Open("ask cloud pilot near red mond");
            rig.Correction("cloud pilot", "Copilot");
            rig.Click("SaveButton");
            rig.Click("CopyFixedButton");
            Assert.Equal("ask Copilot near red mond", Assert.Single(rig.Copies));
            Assert.Equal(1, rig.PersistAttempts);

            rig.Correction("red mond", "Redmond");
            rig.Click("CopyFixedButton");
            Assert.Equal("ask Copilot near Redmond", rig.Copies[^1]);
            Assert.Equal(2, rig.PersistAttempts);

            rig.Correction("ask", "Ask");
            rig.Click("SaveButton");
            rig.Click("CopyFixedButton");
            rig.Click("CopyFixedButton");
            Assert.Equal("Ask Copilot near Redmond", rig.Copies[^1]);
            Assert.Equal("Ask Copilot near Redmond", rig.Copies[^2]);
            Assert.Equal(3, rig.PersistAttempts);
            Assert.Equal(3, rig.Saved.Count);
            Assert.All(rig.Saved, saved => Assert.False(saved.CloseAfterSaving));
            Assert.True(rig.Window.IsVisible);
        });

    [Fact]
    public void A_failed_pending_save_never_copies_the_previous_saved_dictation_and_can_be_retried() =>
        PrivateDesktopTest.Run(typeof(QuickAddWindowCopyTests), () =>
        {
            using var rig = new WindowRig();
            rig.Open("ask cloud pilot near red mond");
            rig.Correction("cloud pilot", "Copilot");
            rig.Click("SaveButton");
            rig.Correction("red mond", "Redmond");
            rig.PersistFails = true;

            rig.Click("CopyFixedButton");

            Assert.Empty(rig.Copies);
            Assert.Single(rig.Saved);
            Assert.Equal(2, rig.PersistAttempts);
            Assert.Equal("ask Copilot near red mond", rig.Field<string>("_transcript"));
            Assert.Equal("red mond", rig.Control<TextBox>("HeardBox").Text);
            Assert.Equal("Redmond", rig.Control<TextBox>("ShouldBeBox").Text);
            Assert.Equal(QuickDictionaryAdd.SaveFailed().Message, rig.Control<TextBlock>("StatusText").Text);
            Assert.True(rig.Window.IsVisible);
            Assert.True(rig.Control<Button>("CopyFixedButton").IsEnabled);

            rig.PersistFails = false;
            rig.Click("CopyFixedButton");
            Assert.Equal("ask Copilot near Redmond", Assert.Single(rig.Copies));
            Assert.Equal(2, rig.Saved.Count);
        });

    [Fact]
    public void A_failed_clipboard_write_can_retry_the_corrected_text_without_another_save() =>
        PrivateDesktopTest.Run(typeof(QuickAddWindowCopyTests), () =>
        {
            using var rig = new WindowRig { CopySucceeds = false };
            rig.Open("ask cloud pilot");
            rig.Correction("cloud pilot", "Copilot");

            rig.Click("CopyFixedButton");

            Assert.Equal("ask Copilot", Assert.Single(rig.Copies));
            Assert.Equal(QuickDictionaryAdd.CopyFailed().Message, rig.Control<TextBlock>("StatusText").Text);
            Assert.True(rig.Control<Button>("CopyFixedButton").IsEnabled);
            Assert.Equal("Co_py dictation", rig.Control<Button>("CopyFixedButton").Content);
            Assert.True(rig.Window.IsVisible);

            rig.CopySucceeds = true;
            rig.Click("CopyFixedButton");
            Assert.Equal(new[] { "ask Copilot", "ask Copilot" }, rig.Copies);
            Assert.Equal(1, rig.PersistAttempts);
            Assert.Single(rig.Saved);
            Assert.Equal(QuickDictionaryAdd.CopySucceeded().Message, rig.Control<TextBlock>("StatusText").Text);
        });

    [Fact]
    public void Invalid_corrections_and_unavailable_references_disable_copy_without_losing_the_source() =>
        PrivateDesktopTest.Run(typeof(QuickAddWindowCopyTests), () =>
        {
            using var rig = new WindowRig();
            rig.Open("ask cloud pilot");
            foreach (var (heard, writes) in new[]
            {
                ("cloud pilot", "cloud pilot"), ("cloud\npilot", "Copilot"), ("cloud pilot", ""), ("", "Copilot"),
            })
            {
                rig.Correction(heard, writes);
                Assert.False(rig.Control<Button>("CopyFixedButton").IsEnabled);
                rig.Click("CopyFixedButton");
            }

            rig.Correction("cloud pilot", "Copilot");
            Assert.True(rig.Control<Button>("CopyFixedButton").IsEnabled);
            rig.ReferencesFail = true;
            rig.Click("CopyFixedButton");
            Assert.Equal(QuickDictionaryAdd.PlanKind.ReferencesUnavailable, rig.Field<QuickDictionaryAdd.Plan>("_currentPlan").Kind);
            Assert.False(rig.Control<Button>("CopyFixedButton").IsEnabled);
            Assert.Empty(rig.Copies);
            Assert.Equal(0, rig.PersistAttempts);
            Assert.Equal("ask cloud pilot", rig.Field<string>("_transcript"));
            Assert.True(rig.Window.IsVisible);
        });

    [Fact]
    public void A_missing_or_deleted_source_cannot_copy_but_the_pending_word_can_still_be_saved() =>
        PrivateDesktopTest.Run(typeof(QuickAddWindowCopyTests), () =>
        {
            using var rig = new WindowRig();
            rig.Open();
            Assert.Equal(Visibility.Visible, rig.Control<Button>("CopyFixedButton").Visibility);
            Assert.False(rig.Control<Button>("CopyFixedButton").IsEnabled);
            rig.Correction("cloud pilot", "Copilot");
            Assert.True(rig.Control<Button>("SaveButton").IsEnabled);
            Assert.False(rig.Control<Button>("CopyFixedButton").IsEnabled);
            rig.Click("CopyFixedButton");
            Assert.Equal(0, rig.PersistAttempts);
            rig.Click("SaveButton");
            Assert.Single(rig.Saved);
            Assert.Empty(rig.Copies);
            rig.Close();
            rig.Reset();

            rig.Open("ask cloud pilot");
            rig.Correction("cloud pilot", "Copilot");
            rig.Invoke("ClearTranscripts");
            Assert.Equal("cloud pilot", rig.Control<TextBox>("HeardBox").Text);
            Assert.Equal("Copilot", rig.Control<TextBox>("ShouldBeBox").Text);
            Assert.True(rig.Control<Button>("SaveButton").IsEnabled);
            Assert.False(rig.Control<Button>("CopyFixedButton").IsEnabled);
            rig.Click("CopyFixedButton");
            Assert.Equal(0, rig.PersistAttempts);
            rig.Click("SaveButton");
            Assert.Single(rig.Saved);
            Assert.Empty(rig.Copies);
            Assert.Equal("", rig.Field<string>("_transcript"));
        });

    [Fact]
    public void A_source_deleted_by_a_saved_callback_is_not_restored_or_copied() =>
        PrivateDesktopTest.Run(typeof(QuickAddWindowCopyTests), () =>
        {
            using var rig = new WindowRig();
            rig.Open("ask cloud pilot");
            rig.Correction("cloud pilot", "Copilot");
            rig.AfterSaved = () => rig.Invoke("ClearTranscripts");

            rig.Click("CopyFixedButton");

            Assert.Single(rig.Saved);
            Assert.Empty(rig.Copies);
            Assert.Equal("", rig.Field<string>("_transcript"));
            Assert.False(rig.Control<Button>("CopyFixedButton").IsEnabled);
            Assert.True(rig.Window.IsVisible);
        });

    [Fact]
    public void A_source_selected_by_a_saved_callback_does_not_copy_another_dictation() =>
        PrivateDesktopTest.Run(typeof(QuickAddWindowCopyTests), () =>
        {
            using var rig = new WindowRig();
            rig.Open("ask cloud pilot", "another dictation");
            rig.Correction("cloud pilot", "Copilot");
            rig.AfterSaved = () => rig.Control<ComboBox>("RecentPicker").SelectedIndex = 1;

            rig.Click("CopyFixedButton");

            Assert.Single(rig.Saved);
            Assert.Empty(rig.Copies);
            Assert.Equal("another dictation", rig.Field<string>("_transcript"));
            Assert.True(rig.Window.IsVisible);
        });

    [Fact]
    public void Switching_sources_uses_the_selected_current_dictation_and_leaves_copy_visible() =>
        PrivateDesktopTest.Run(typeof(QuickAddWindowCopyTests), () =>
        {
            using var rig = new WindowRig();
            rig.Open("ask cloud pilot", "another dictation");
            rig.Correction("cloud pilot", "Copilot");
            rig.Click("SaveButton");
            var picker = rig.Control<ComboBox>("RecentPicker");
            picker.SelectedIndex = 1;
            rig.Click("CopyFixedButton");
            Assert.Equal("another dictation", rig.Copies[^1]);
            picker.SelectedIndex = 0;
            rig.Click("CopyFixedButton");
            Assert.Equal("ask Copilot", rig.Copies[^1]);
            Assert.Equal(1, rig.PersistAttempts);
            Assert.Equal(Visibility.Visible, rig.Control<Button>("CopyFixedButton").Visibility);
        });

    [Fact]
    public void Removal_copies_the_remaining_dictation_but_never_copies_an_empty_result() =>
        PrivateDesktopTest.Run(typeof(QuickAddWindowCopyTests), () =>
        {
            using var rig = new WindowRig();
            rig.Open("ask filler Copilot");
            rig.Correction("filler", "");
            rig.Control<CheckBox>("RemoveBox").IsChecked = true;
            rig.Click("CopyFixedButton");
            Assert.Equal("ask  Copilot", Assert.Single(rig.Copies));
            Assert.Equal("", Assert.Single(rig.SavedEntries).Replacement);
            Assert.True(rig.Window.IsVisible);
            rig.Close();
            rig.Reset();

            rig.Open("filler");
            rig.Correction("filler", "");
            rig.Control<CheckBox>("RemoveBox").IsChecked = true;
            rig.Click("CopyFixedButton");
            Assert.Equal("", Assert.Single(rig.SavedEntries).Replacement);
            Assert.Empty(rig.Copies);
            Assert.False(rig.Control<Button>("CopyFixedButton").IsEnabled);
            Assert.True(rig.Window.IsVisible);
        });

    [Fact]
    public void Save_and_close_and_Close_keep_their_existing_behavior_without_copying() =>
        PrivateDesktopTest.Run(typeof(QuickAddWindowCopyTests), () =>
        {
            using var rig = new WindowRig();
            rig.Open("ask cloud pilot");
            rig.Correction("cloud pilot", "Copilot");
            rig.Click("SaveCloseButton");
            Assert.False(rig.Window.IsVisible);
            Assert.True(Assert.Single(rig.Saved).CloseAfterSaving);
            Assert.Empty(rig.Copies);
            rig.Reset();

            rig.Open("ask cloud pilot");
            rig.Click("CloseButton");
            Assert.False(rig.Window.IsVisible);
            Assert.Empty(rig.Saved);
            Assert.Empty(rig.Copies);
        });

    [Fact]
    public void Copy_is_accessible_in_the_fixed_wrapping_footer_at_normal_and_large_text_sizes() =>
        PrivateDesktopTest.Run(typeof(QuickAddWindowCopyTests), () =>
        {
            using var rig = new WindowRig();
            foreach (var scale in new[] { 1.0, 2.25 })
            {
                rig.SetTextScale(scale);
                rig.Open("ask cloud pilot " + string.Join(" ", Enumerable.Repeat("more words to fill the scroll area", 40)));
                rig.Window.Width = 440;
                rig.Window.Height = 640;
                rig.Correction("cloud pilot", "Copilot");
                var copy = rig.Control<Button>("CopyFixedButton");
                var footer = rig.Control<Border>("QuickAddFooter");
                var actions = rig.Control<WrapPanel>("FooterActions");
                var body = rig.Control<ScrollViewer>("BodyScroll");
                body.ScrollToEnd();
                PrivateDesktopTest.RenderCheckpoint("copy footer layout", rig.Window.UpdateLayout);

                Assert.Equal(2, Grid.GetRow(footer));
                Assert.Same(actions, copy.Parent);
                Assert.Null(Ancestor<ScrollViewer>(copy));
                Assert.True(copy.IsVisible);
                Assert.True(copy.IsTabStop);
                Assert.True(body.ViewportHeight > 0);
                var buttons = actions.Children.OfType<Button>().ToArray();
                Assert.Equal(4, buttons.Length);
                foreach (var button in buttons)
                {
                    var position = button.TranslatePoint(new Point(), footer);
                    Assert.InRange(position.X, 0, footer.ActualWidth);
                    Assert.InRange(position.X + button.ActualWidth, 0, footer.ActualWidth);
                    Assert.InRange(position.Y + button.ActualHeight, 0, footer.ActualHeight);
                }

                Assert.True(buttons.Max(button => button.TranslatePoint(new Point(), actions).Y)
                    > buttons.Min(button => button.TranslatePoint(new Point(), actions).Y), "The footer must wrap rather than clip its buttons.");
                var peer = new ButtonAutomationPeer(copy);
                Assert.Equal("Save and copy dictation", peer.GetName());
                Assert.Equal("Save this correction, then copy the corrected dictation and keep this window open.", peer.GetHelpText());
                Assert.Contains("p", peer.GetAccessKey(), StringComparison.OrdinalIgnoreCase);
                rig.Control<Button>("SaveCloseButton").Focus();
                Assert.True(rig.Control<Button>("SaveCloseButton").MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)));
                Assert.Same(copy, Keyboard.FocusedElement);
                Assert.True(copy.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)));
                Assert.Same(rig.Control<Button>("CloseButton"), Keyboard.FocusedElement);
                rig.Click("CopyFixedButton");
                Assert.Equal("Copy dictation", peer.GetName());
                Assert.True(rig.Window.IsVisible);
                rig.Close();
                rig.Reset();
            }
        });

    private static T? Ancestor<T>(DependencyObject node) where T : DependencyObject
    {
        for (var parent = VisualTreeHelper.GetParent(node); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is T ancestor)
            {
                return ancestor;
            }
        }

        return null;
    }

    private sealed record SavedCorrection(string? SourceTranscript, string? CorrectedTranscript, bool CloseAfterSaving);

    private sealed class WindowRig : IDisposable
    {
        private readonly Application _app = new() { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        private readonly Assembly _assembly;
        private readonly Type _type;
        private readonly string _appBin;
        private readonly List<DictionaryEntry> _entries = [];
        private Exception? _dispatcherFailure;

        public Window Window { get; private set; } = null!;
        public List<string> Copies { get; } = [];
        public List<string> Order { get; } = [];
        public List<SavedCorrection> Saved { get; } = [];
        public IReadOnlyList<DictionaryEntry> SavedEntries => _entries;
        public int PersistAttempts { get; private set; }
        public bool PersistFails { get; set; }
        public bool ReferencesFail { get; set; }
        public bool CopySucceeds { get; set; } = true;
        public Action? AfterSaved { get; set; }

        public WindowRig()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "Scribe.slnx")))
            {
                root = root.Parent;
            }

            Assert.NotNull(root);
            var output = new DirectoryInfo(AppContext.BaseDirectory);
            while (output is not null && output.Name is not ("Debug" or "Release"))
            {
                output = output.Parent;
            }

            Assert.NotNull(output);
            _appBin = Path.Combine(root.FullName, "src", "Scribe.App", "bin", output.Name, "net10.0-windows10.0.22000.0");
            AssemblyLoadContext.Default.Resolving += Resolve;
            _app.DispatcherUnhandledException += (_, e) =>
            {
                _dispatcherFailure ??= e.Exception;
                e.Handled = true;
            };
            _assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(_appBin, "Scribe.dll"));
            _type = _assembly.GetType("Scribe.App.QuickAdd.QuickAddWindow", throwOnError: true)!;
            var controls = _type.BaseType!.Assembly;
            var themeType = controls.GetType("Wpf.Ui.Markup.ThemesDictionary", throwOnError: true)!;
            var theme = Assert.IsAssignableFrom<ResourceDictionary>(Activator.CreateInstance(themeType));
            var property = themeType.GetProperty("Theme")!;
            property.SetValue(theme, Enum.Parse(property.PropertyType, "Dark"));
            _app.Resources.MergedDictionaries.Add(theme);
            _app.Resources.MergedDictionaries.Add(Assert.IsAssignableFrom<ResourceDictionary>(
                Activator.CreateInstance(controls.GetType("Wpf.Ui.Markup.ControlsDictionary", throwOnError: true)!)));
            SetTextScale(1);
        }

        public void SetTextScale(double factor)
        {
            foreach (var (key, size) in new[]
            {
                ("ScribeFontCaption", 12), ("ScribeFontBody", 14), ("ScribeFontBodyLarge", 16),
                ("ScribeFontSubtitle", 20), ("ScribeFontTitle", 26), ("ScribeFontIconLarge", 22),
                ("ScribeFontDisplay", 68), ("ControlContentThemeFontSize", 14),
                ("TextControlThemeFontSize", 14), ("TitleBarThemeFontSize", 12),
                ("DefaultDataGridFontSize", 14), ("InfoBarTitleThemeFontSize", 14), ("InfoBarMessageThemeFontSize", 14),
            })
            {
                _app.Resources[key] = size * factor;
            }

            _assembly.GetType("Scribe.App.Infrastructure.TextScaleService", throwOnError: true)!
                .GetProperty("CurrentFactor")!.GetSetMethod(nonPublic: true)!.Invoke(null, [factor]);
        }

        public void Open(params string[] transcripts)
        {
            Func<IReadOnlyList<DictionaryEntry>> loadExisting = () => ReferencesFail
                ? throw new InvalidOperationException("Scripted vocabulary failure.")
                : _entries.ToArray();
            Func<DictionaryEntry, DictionaryEntry> persist = entry =>
            {
                PersistAttempts++;
                Order.Add("persist");
                if (PersistFails)
                {
                    throw new InvalidOperationException("Scripted save failure.");
                }

                var saved = entry.Id == 0 ? entry with { Id = _entries.Count + 1 } : entry;
                _entries.RemoveAll(existing => existing.Id == saved.Id);
                _entries.Add(saved);
                return saved;
            };
            Func<string, bool> copy = text =>
            {
                Order.Add("copy");
                Copies.Add(text);
                return CopySucceeds;
            };
            var optionsType = _type.GetNestedType("QuickAddWindowOptions")!;
            var options = Activator.CreateInstance(optionsType, [false, true, false, null, null, null, null, null, null, copy]);
            Window = Assert.IsAssignableFrom<Window>(
                Activator.CreateInstance(_type, [transcripts, loadExisting, persist, null, null, options]));
            var savedEvent = _type.GetEvent("Saved")!;
            var resultType = _type.GetNestedType("QuickAddResult")!;
            var method = typeof(WindowRig).GetMethod(nameof(OnSaved), BindingFlags.Instance | BindingFlags.NonPublic)!
                .MakeGenericMethod(resultType);
            savedEvent.AddEventHandler(Window, Delegate.CreateDelegate(savedEvent.EventHandlerType!, this, method));
            Window.ShowInTaskbar = false;
            Window.Show();
            PrivateDesktopTest.RenderCheckpoint("quick add initial render", Window.UpdateLayout);
            Assert.True(Window.IsLoaded);
        }

        private void OnSaved<T>(T result)
        {
            Order.Add("saved");
            var type = typeof(T);
            Saved.Add(new SavedCorrection(
                (string?)type.GetProperty("SourceTranscript")!.GetValue(result),
                (string?)type.GetProperty("CorrectedTranscript")!.GetValue(result),
                (bool)type.GetProperty("CloseAfterSaving")!.GetValue(result)!));
            AfterSaved?.Invoke();
        }

        public void Correction(string heard, string writes)
        {
            Control<TextBox>("HeardBox").Text = heard;
            Control<TextBox>("ShouldBeBox").Text = writes;
        }

        public T Control<T>(string name) where T : FrameworkElement =>
            Assert.IsAssignableFrom<T>(Window.FindName(name));

        public T Field<T>(string name) =>
            Assert.IsType<T>(_type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Window));

        public void Invoke(string name) =>
            _type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(Window, []);

        public void Click(string name)
        {
            PrivateDesktopTest.Step($"quick add: click {name}");
            Control<Button>(name).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            PrivateDesktopTest.RenderCheckpoint($"quick add after {name}", () =>
            {
                if (Window.IsVisible)
                {
                    Window.UpdateLayout();
                }
            });
            Assert.True(_dispatcherFailure is null, _dispatcherFailure?.ToString());
        }

        public void Close()
        {
            _type.GetField("_allowClose", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Window, true);
            Window.Close();
        }

        public void Reset()
        {
            _entries.Clear();
            Copies.Clear();
            Saved.Clear();
            Order.Clear();
            PersistAttempts = 0;
        }

        private Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
        {
            var path = Path.Combine(_appBin, name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        }

        public void Dispose()
        {
            foreach (Window window in _app.Windows.Cast<Window>().ToArray())
            {
                _type.GetField("_allowClose", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
                window.Close();
            }

            _app.Shutdown();
            AssemblyLoadContext.Default.Resolving -= Resolve;
            Assert.True(_dispatcherFailure is null, _dispatcherFailure?.ToString());
        }
    }
}
