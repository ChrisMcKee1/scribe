using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Scribe.Core.Cleanup;
using Scribe.Core.Diagnostics;
using Scribe.Core.Infrastructure;
using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.Settings;
using Scribe.Core.Tests.Libraries.Integration;
using Scribe.Core.Vocabulary;

namespace Scribe.Core.Tests;

/// <summary>
/// The real Settings window, on a private desktop over an isolated database, saving while a newer choice arrives from the
/// tray. A Save captures its preflight, then waits (for a confirmation, for a Start with Windows change still saving, for
/// an earlier save to settle) before it freezes the document it stores. The tray's choice adopted in that wait is newer than
/// the preflight and lives in the window's editing document until the tray's own write settles it: copying the preflight
/// over that document before the Save freezes used to put the older choice back, so the editing document went stale for
/// everything that reads it, even when the Save stopped without storing anything (T14).
/// </summary>
public sealed class SettingsSavePreflightWindowTests
{
    [Fact]
    public void A_tray_choice_of_AI_cleanup_adopted_while_a_save_waits_stays_the_editing_documents_and_the_save_stores_what_it_captured() =>
        PrivateDesktopTest.Run(typeof(SettingsSavePreflightWindowTests), () =>
        {
            using var rig = new WindowRig(configure: settings =>
            {
                settings.EnableAiCleanup = false;
                settings.AiCleanupProvider = CleanupProvider.FoundryLocal;
            });
            rig.Open();
            Assert.False(rig.Services.GetRequiredService<ISettingsRepository>().Load().EnableAiCleanup);
            var preflightIntent = ExternalSwitchSync.NextRevision();
            rig.InvokePublic("AdoptExternalAiCleanup", true, preflightIntent);
            Assert.Equal(preflightIntent, rig.Field<ExternalSwitchSync>("_externalAiCleanup").NewestRevision);

            // An edit the Save stores, so a committed Save is told from one that stopped.
            rig.Control<CheckBox>("SpaceAfterDictationCheck").IsChecked = false;

            // A Start with Windows change is still saving, so the Save waits for it after its preflight captured the document.
            var startup = new TaskCompletionSource();
            rig.SetField("_startupApply", startup.Task);
            var save = rig.Result<Task<bool>>("TrySaveAsync");
            Assert.False(save.IsCompleted, "The Save did not wait after its preflight.");
            Assert.True(rig.Field<AppSettings>("_settings").EnableAiCleanup);

            // The tray turns AI cleanup off meanwhile, and the window takes it into its editing document at once.
            var revision = ExternalSwitchSync.NextRevision();
            rig.InvokePublic("AdoptExternalAiCleanup", false, revision);
            Assert.False(rig.Field<AppSettings>("_settings").EnableAiCleanup);

            startup.SetResult();
            WindowRig.PumpUntil(() => save.IsCompleted);

            // The Save committed what its preflight captured and never the tray's choice, whose own write is not this Save's.
            // It reports that the draft moved on, which the tray's change is, so the window stays open.
            var stored = rig.Services.GetRequiredService<ISettingsRepository>().Load();
            Assert.False(stored.AddSpaceAfterDictation);
            Assert.True(stored.EnableAiCleanup);
            Assert.False(save.Result);

            // The editing document keeps the newer choice until the tray's own write settles it.
            Assert.False(rig.Field<AppSettings>("_settings").EnableAiCleanup);
            Assert.Equal(revision, rig.Field<ExternalSwitchSync>("_externalAiCleanup").NewestRevision);
        });

    [Fact]
    public void A_tray_microphone_adopted_while_a_save_waits_survives_the_save_that_stops_because_the_word_pack_draft_moved() =>
        PrivateDesktopTest.Run(typeof(SettingsSavePreflightWindowTests), () =>
        {
            using var rig = new WindowRig(configure: settings => settings.EnableAiCleanup = false);
            rig.Open();
            var before = MicrophoneSelection.From(rig.Field<AppSettings>("_settings"));
            var startup = new TaskCompletionSource();
            rig.SetField("_startupApply", startup.Task);
            var save = rig.Result<Task<bool>>("TrySaveAsync");
            Assert.False(save.IsCompleted, "The Save did not wait after its preflight.");

            // The tray's microphone is adopted, and the word pack draft is edited, while the Save waits: the Save stops at its
            // check that the draft it validated is still the draft, before it freezes or stores anything.
            var yeti = new MicrophoneSelection("yeti", "Blue Yeti");
            Assert.NotEqual(before, yeti);
            rig.InvokePublic("AdoptExternalMicrophone", yeti, ExternalSwitchSync.NextRevision());
            var workspace = rig.Field<LibraryWorkspace>("_wordPackWorkspace");
            workspace.SetEnabled(workspace.Draft.LocalState.EnabledIds.First(), false);
            Assert.True(workspace.EditRevision > 0);

            startup.SetResult();
            WindowRig.PumpUntil(() => save.IsCompleted);

            Assert.False(save.Result);
            Assert.Equal(yeti, MicrophoneSelection.From(rig.Field<AppSettings>("_settings")));
            Assert.Equal(before, MicrophoneSelection.From(rig.Services.GetRequiredService<ISettingsRepository>().Load()));
        });

    [Fact]
    public void A_save_stores_the_microphone_its_preflight_captured_under_its_intent_and_not_a_newer_tray_choice_adopted_while_it_waited() =>
        PrivateDesktopTest.Run(typeof(SettingsSavePreflightWindowTests), () =>
        {
            using var rig = new WindowRig(configure: settings => settings.EnableAiCleanup = false);
            rig.Open();
            var yeti = new MicrophoneSelection("yeti", "Blue Yeti");
            var usb = new MicrophoneSelection("usb", "USB Microphone");

            // The window holds an intent for the microphone when the Save's preflight captures it: Yeti.
            rig.InvokePublic("AdoptExternalMicrophone", yeti, ExternalSwitchSync.NextRevision());
            var startup = new TaskCompletionSource();
            rig.SetField("_startupApply", startup.Task);
            var save = rig.Result<Task<bool>>("TrySaveAsync");
            Assert.False(save.IsCompleted, "The Save did not wait after its preflight.");

            // A newer tray choice is adopted while the Save waits, and its own write may still fail.
            var newer = ExternalSwitchSync.NextRevision();
            rig.InvokePublic("AdoptExternalMicrophone", usb, newer);
            Assert.Equal(usb, MicrophoneSelection.From(rig.Field<AppSettings>("_settings")));

            startup.SetResult();
            WindowRig.PumpUntil(() => save.IsCompleted);

            // What is stored is what the preflight captured under the intent it carried, never the unconfirmed newer choice,
            // which stays the editing document's until its own write settles it.
            Assert.Equal(yeti, MicrophoneSelection.From(rig.Services.GetRequiredService<ISettingsRepository>().Load()));
            Assert.Equal(usb, MicrophoneSelection.From(rig.Field<AppSettings>("_settings")));
            Assert.Equal(newer, rig.Field<ExternalChoiceSync<MicrophoneSelection>>("_externalMicrophone").NewestRevision);
        });

    private sealed class WindowRig : IDisposable
    {
        private readonly TempDirectory _temp = new();
        private readonly Application _app;
        private readonly string _appBin;
        private readonly Type _type;
        private Window? _window;
        private Exception? _dispatcherFailure;

        public WindowRig(Action<AppSettings>? configure = null)
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
            configure?.Invoke(initial);
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

        public ServiceProvider Services { get; }

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

        public T Field<T>(string name) =>
            Assert.IsType<T>(_type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_window));

        public T Control<T>(string name) where T : FrameworkElement =>
            Assert.IsAssignableFrom<T>(_window!.FindName(name));

        public void SetField(string name, object value) =>
            _type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_window, value);

        public void Invoke(string name, params object[] args) =>
            _type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_window, args);

        public void InvokePublic(string name, params object[] args) =>
            _type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public)!.Invoke(_window, args);

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
