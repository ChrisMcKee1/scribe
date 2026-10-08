using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Scribe.Core.Diagnostics;
using Scribe.Core.Infrastructure;
using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.Settings;
using Scribe.Core.Tests.Libraries.Integration;
using Scribe.Core.Vocabulary;

namespace Scribe.Core.Tests;

public sealed class TryDictationWindowTests
{
    [Fact]
    public void AI_cleanup_and_Advanced_share_every_idle_choice_and_preserve_custom_values_and_Save() =>
        PrivateDesktopTest.Run(typeof(TryDictationWindowTests), () =>
        {
            using var rig = new WindowRig(idleMinutes: 3);
            rig.Open();
            rig.Invoke("ShowPage", SettingsPage.AiCleanup, Type.Missing);
            var advanced = rig.Control<ComboBox>("IdleReleaseCombo");
            var ai = rig.Control<ComboBox>("AiIdleReleaseCombo");
            var custom = rig.Control<FrameworkElement>("AiIdleReleaseCustomBox");
            Assert.True(Assert.IsType<DurationChoice>(ai.SelectedItem).IsCustom);
            Assert.Equal(Visibility.Visible, custom.Visibility);

            foreach (var scale in new[] { 1.0, 2.25 })
            {
                rig.SetTextScale(scale);
                foreach (var minutes in new[] { 0, 5, 10, 15, 20, 25, 30, 35, 40, 45, 50, 55, 60 })
                {
                    ai.SelectedItem = ai.Items.Cast<DurationChoice>().Single(choice => choice.Value == minutes);
                    WindowRig.PumpUntil(() => ReferenceEquals(ai.SelectedItem, advanced.SelectedItem));
                    Assert.Equal(minutes, Assert.IsType<DurationChoice>(advanced.SelectedItem).Value);
                    Assert.Equal(Visibility.Collapsed, custom.Visibility);
                }

                foreach (var minutes in new[] { 3, 30, 75, 0 })
                {
                    var loaded = rig.Services.GetRequiredService<ISettingsRepository>().Load();
                    loaded.ReleaseModelsAfterIdleMinutes = minutes;
                    rig.Invoke("LoadAdvancedControls", loaded);
                    WindowRig.PumpUntil(() => ReferenceEquals(ai.SelectedItem, advanced.SelectedItem));
                    var selected = Assert.IsType<DurationChoice>(ai.SelectedItem);
                    Assert.Equal(minutes is 3 or 75, selected.IsCustom);
                    if (selected.IsCustom)
                    {
                        var field = Assert.Single(rig.Result<IReadOnlyList<DurationDraftField>>("DurationDraftFields"),
                            field => field.ControlName == "IdleReleaseCustomBox");
                        var range = DurationChoices.Build(DurationChoiceKind.IdleRelease, minutes);
                        Assert.Equal(range.Minimum, field.Minimum);
                        Assert.Equal(range.Maximum, field.Maximum);
                    }
                    else
                    {
                        Assert.Equal(minutes, selected.Value);
                    }
                }
            }

            ai.SelectedItem = ai.Items.Cast<DurationChoice>().Single(choice => choice.Value == 30);
            WindowRig.PumpUntil(() => ReferenceEquals(ai.SelectedItem, advanced.SelectedItem));
            rig.Invoke("RefreshFooterNow");
            rig.Invoke("TryDictationSaveNow_Click", rig.Control<Button>("SaveButton"), new RoutedEventArgs());
            WindowRig.PumpUntil(() => !rig.Field<bool>("_saveInProgress"));
            Assert.Equal(30, rig.Services.GetRequiredService<ISettingsRepository>().Load().ReleaseModelsAfterIdleMinutes);
            rig.Invoke("RefreshFooterNow");
            Assert.Equal(SettingsChangeTracker.AllChangesSaved, rig.Control<TextBlock>("FooterStatusText").Text);
        });

    [Fact]
    public void A_clean_window_keeps_the_notice_and_Save_now_hidden_with_a_word_pack_kept_from_AI() =>
        PrivateDesktopTest.Run(typeof(TryDictationWindowTests), () =>
        {
            using var rig = new WindowRig();
            rig.Open();
            var workspace = rig.Field<LibraryWorkspace>("_wordPackWorkspace");
            Assert.False(workspace.HasUnsavedChanges);
            Assert.NotEmpty(workspace.Draft.LocalState.EnabledIds);
            Assert.Empty(rig.Services.GetRequiredService<ISettingsRepository>().Load().EnabledDictionaryLibraryIds);

            rig.Invoke("ShowPage", SettingsPage.TryDictation, Type.Missing);
            Assert.Equal(Visibility.Collapsed, rig.Control<FrameworkElement>("TryDictationUnsavedHost").Visibility);
            Assert.Equal(Visibility.Collapsed, rig.Control<Button>("TryDictationSaveNowButton").Visibility);
            Assert.Equal(SettingsChangeTracker.AllChangesSaved, rig.Control<TextBlock>("FooterStatusText").Text);

            var space = rig.Control<CheckBox>("SpaceAfterDictationCheck");
            space.IsChecked = false;
            rig.Invoke("RefreshFooterNow");
            Assert.Equal(Visibility.Visible, rig.Control<FrameworkElement>("TryDictationUnsavedHost").Visibility);
            Assert.Equal(Visibility.Visible, rig.Control<Button>("TryDictationSaveNowButton").Visibility);
            space.IsChecked = true;
            rig.Invoke("RefreshFooterNow");
            Assert.Equal(Visibility.Collapsed, rig.Control<FrameworkElement>("TryDictationUnsavedHost").Visibility);
            Assert.Equal(Visibility.Collapsed, rig.Control<Button>("TryDictationSaveNowButton").Visibility);

            space.IsChecked = false;
            rig.Invoke("RefreshFooterNow");
            rig.Invoke("TryDictationSaveNow_Click", rig.Control<Button>("TryDictationSaveNowButton"), new RoutedEventArgs());
            WindowRig.PumpUntil(() => !rig.Field<bool>("_saveInProgress"));
            rig.Invoke("RefreshFooterNow");
            Assert.False(rig.Services.GetRequiredService<ISettingsRepository>().Load().AddSpaceAfterDictation);
            Assert.Equal(Visibility.Collapsed, rig.Control<FrameworkElement>("TryDictationUnsavedHost").Visibility);
            Assert.Equal(Visibility.Collapsed, rig.Control<Button>("TryDictationSaveNowButton").Visibility);
            Assert.Equal(SettingsChangeTracker.AllChangesSaved, rig.Control<TextBlock>("FooterStatusText").Text);
        });

    private sealed class WindowRig : IDisposable
    {
        private readonly TempDirectory _temp = new();
        private readonly Application _app;
        private readonly string _appBin;
        private readonly Type _type;
        private Window? _window;
        private Exception? _dispatcherFailure;

        public ServiceProvider Services { get; }

        public WindowRig(int idleMinutes = 10)
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
            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(_appBin, "Scribe.dll"));
            _app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            _app.DispatcherUnhandledException += (_, e) =>
            {
                _dispatcherFailure ??= e.Exception;
                e.Handled = true;
            };
            _type = assembly.GetType("Scribe.App.Settings.SettingsWindow", throwOnError: true)!;
            var controls = _type.BaseType!.Assembly;
            var themeType = controls.GetType("Wpf.Ui.Markup.ThemesDictionary", throwOnError: true)!;
            var theme = Assert.IsAssignableFrom<ResourceDictionary>(Activator.CreateInstance(themeType));
            var themeProperty = themeType.GetProperty("Theme")!;
            themeProperty.SetValue(theme, Enum.Parse(themeProperty.PropertyType, "Dark"));
            _app.Resources.MergedDictionaries.Add(theme);
            _app.Resources.MergedDictionaries.Add(Assert.IsAssignableFrom<ResourceDictionary>(
                Activator.CreateInstance(controls.GetType("Wpf.Ui.Markup.ControlsDictionary", throwOnError: true)!)));
            foreach (var (key, size) in new[]
            {
                ("ScribeFontCaption", 12), ("ScribeFontBody", 14), ("ScribeFontBodyLarge", 16),
                ("ScribeFontSubtitle", 20), ("ScribeFontTitle", 26), ("ScribeFontIconLarge", 22),
                ("ScribeFontDisplay", 68),
            })
            {
                _app.Resources[key] = (double)size;
            }

            var paths = new AppPaths(_temp.Combine("data"));
            paths.EnsureCreated();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddScribeCore();
            services.AddSingleton(paths);
            services.AddSingleton(PerfFlags.None);
            services.AddSingleton(assembly.GetType("Scribe.App.Infrastructure.AzureCliInstaller", throwOnError: true)!);
            services.AddSingleton<StartupRegistration>();
            Services = services.BuildServiceProvider();
            var settings = Services.GetRequiredService<ISettingsRepository>();
            var initial = AppSettings.CreateDefault();
            initial.ReleaseModelsAfterIdleMinutes = idleMinutes;
            settings.Save(initial);
            var store = Services.GetRequiredService<ILibraryCatalogStore>();
            var workspace = RealLibraries.Workspace(store.LoadCatalog());
            foreach (var id in workspace.Draft.LocalState.EnabledIds.ToArray())
            {
                workspace.SetAiPermission(id, false);
            }

            var change = workspace.CaptureChangeSet().ChangeSet!;
            var prepared = store.PrepareSave(change);
            Assert.Equal(LibraryPrepareStatus.Prepared, prepared.Status);
            settings.SaveBundle(settings.Load(), null, null, default, prepared.Save!.Payload);
            var outcome = store.CompleteSave(prepared.Save);
            Assert.True(RealLibraries.MarksSaved(outcome.Status));
        }

        public void Open()
        {
            Action<OverlayPosition> preview = _ => { };
            Func<AppSettings, Task<VocabularyRefresh>> apply =
                _ => Task.FromResult(new VocabularyRefresh(VocabularyRefreshOutcome.Applied, VocabularyGeneration.Empty));
            Func<Task<VocabularyRefresh>> reload =
                () => Task.FromResult(new VocabularyRefresh(VocabularyRefreshOutcome.Applied, VocabularyGeneration.Empty));
            _window = Assert.IsAssignableFrom<Window>(
                ActivatorUtilities.CreateInstance(Services, _type, preview, apply, reload));
            _window.ShowInTaskbar = false;
            // AutoSuggestBox needs a real handle before its template is applied.
            _window.Show();
            PumpUntil(() =>
            {
                Assert.True(_dispatcherFailure is null, _dispatcherFailure?.ToString());
                return _type.GetField("_wordPackWorkspace", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(_window) is not null;
            });
            Invoke("RefreshFooterNow");
        }

        public T Control<T>(string name) where T : FrameworkElement =>
            Assert.IsAssignableFrom<T>(_window!.FindName(name));

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

            _type.Assembly.GetType("Scribe.App.Infrastructure.TextScaleService", throwOnError: true)!
                .GetProperty("CurrentFactor")!.GetSetMethod(nonPublic: true)!.Invoke(null, [factor]);
            PrivateDesktopTest.RenderCheckpoint("idle choices at text scale", _window!.UpdateLayout);
        }

        public T Field<T>(string name) =>
            Assert.IsType<T>(_type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_window));

        public void Invoke(string name, params object[] args) =>
            _type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_window, args);

        public T Result<T>(string name) where T : class =>
            Assert.IsAssignableFrom<T>(_type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_window, []));

        public static void PumpUntil(Func<bool> ready)
        {
            var timer = Stopwatch.StartNew();
            while (!ready())
            {
                Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10), "The Settings window did not finish its pending operation.");
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
                Dispatcher.PushFrame(frame);
                Thread.Sleep(10);
            }
        }

        private Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
        {
            var path = Path.Combine(_appBin, name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        }

        public void Dispose()
        {
            _window?.Close();
            Task.Run(async () => await Services.DisposeAsync()).GetAwaiter().GetResult();
            _app.Shutdown();
            AssemblyLoadContext.Default.Resolving -= Resolve;
            _temp.Dispose();
            Assert.True(_dispatcherFailure is null, _dispatcherFailure?.ToString());
        }
    }
}
