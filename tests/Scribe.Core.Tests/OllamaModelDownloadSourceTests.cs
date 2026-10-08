using System.Text.RegularExpressions;
using System.Xml.Linq;
using Scribe.Core.Cleanup;

namespace Scribe.Core.Tests;

public sealed class OllamaModelDownloadSourceTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public void The_download_controls_are_below_the_model_picker_with_a_catalog_link_and_explicit_actions()
    {
        var xaml = XDocument.Load(Path.Combine(Root(), "src", "Scribe.App", "Settings", "SettingsWindow.xaml"));
        var panel = Named(xaml, "LocalAppPanel");
        var children = panel.Elements().ToList();
        var picker = Named(xaml, "LocalAppModelBox");
        var download = Named(xaml, "OllamaDownloadPanel");
        Assert.True(children.IndexOf(download) > children.IndexOf(picker));
        Assert.Equal("False", (string?)download.Attribute("IsExpanded"));
        Assert.Equal("Collapsed", (string?)download.Attribute("Visibility"));

        var name = Named(xaml, "OllamaDownloadModelBox");
        Assert.Equal(
            "{Binding ElementName=OllamaDownloadModelTitle}",
            (string?)name.Attribute("AutomationProperties.LabeledBy"));
        Assert.Equal("OllamaDownloadModelBox_KeyDown", (string?)name.Attribute("KeyDown"));
        Assert.Equal("OllamaDownloadButton_Click", (string?)Named(xaml, "OllamaDownloadButton").Attribute("Click"));
        Assert.Equal("OllamaDownloadCancelButton_Click", (string?)Named(xaml, "OllamaDownloadCancelButton").Attribute("Click"));
        Assert.Equal("Polite", (string?)Named(xaml, "OllamaDownloadStatusText").Attribute("AutomationProperties.LiveSetting"));
        var link = Assert.Single(download.Descendants(Presentation + "Hyperlink"));
        Assert.Equal("Hyperlink_RequestNavigate", (string?)link.Attribute("RequestNavigate"));
        Assert.Equal("{x:Static coresettings:OllamaModelDownload.CatalogUri}", (string?)link.Attribute("NavigateUri"));
    }

    [Fact]
    public void The_user_is_asked_before_switching_and_only_the_confirmed_choice_is_saved()
    {
        var source = Read("SettingsWindow.OllamaDownloads.cs");
        var download = Body(source, "private async void OllamaDownloadButton_Click(");
        Assert.Contains("await downloader.DownloadAsync(model, progress, cancellation.Token)", download, StringComparison.Ordinal);
        Assert.Contains("await RefreshLocalAppAsync(preserveEmptySelection: true)", download, StringComparison.Ordinal);
        Assert.Contains("var useModel = await ShowConfirmationAsync(", download, StringComparison.Ordinal);
        Assert.Contains("cancelIsDefault: true", download, StringComparison.Ordinal);
        Assert.Equal("await SaveDownloadedOllamaChoiceAsync(model);", Body(download, "if (useModel)")[1..^1].Trim());
        var save = Body(source, "private async Task SaveDownloadedOllamaChoiceAsync(");
        AssertNarrowPersistence(save);
        Assert.Contains("OllamaModelDownload.CopyChoice(stored, _committedSettings)", save, StringComparison.Ordinal);
        Assert.Contains("var shownBefore = SelectedLocalAppModel;", save, StringComparison.Ordinal);
        Assert.Contains("string.Equals(shownBefore, SelectedLocalAppModel, StringComparison.Ordinal)", save, StringComparison.Ordinal);
        Assert.Contains("StoredSettingsReapply.Reapply(_settingsRepository, _applySettings, _reloadVocabulary)", save, StringComparison.Ordinal);
        Assert.DoesNotContain("_applySettings(_settings)", save, StringComparison.Ordinal);
        Assert.True(
            download.IndexOf("var useModel = await ShowConfirmationAsync(", StringComparison.Ordinal) <
            download.IndexOf("if (useModel)", StringComparison.Ordinal));
    }

    [Fact]
    public void Closing_or_changing_the_app_cancels_the_download_and_late_progress_is_ignored()
    {
        var source = Read("SettingsWindow.OllamaDownloads.cs");
        AssertHiddenAppCancellation(source);
        var progress = Body(source, "var progress = new Progress<OllamaDownloadProgress>(value =>");
        Assert.Contains("_closed || !ReferenceEquals(_ollamaDownload, cancellation) || cancellation.IsCancellationRequested",
            progress, StringComparison.Ordinal);
        var cancel = Body(source, "private void CancelOllamaDownload()");
        Assert.Contains("_ollamaDownload?.Cancel();", Body(cancel, "if (!_ollamaModelSwitching)"), StringComparison.Ordinal);
        var window = Read("SettingsWindow.xaml.cs");
        Assert.Contains("CancelOllamaDownload();", Body(window, "private void OnClosed("), StringComparison.Ordinal);
        Assert.Contains("UpdateOllamaDownloadUi();", Body(window, "private void UpdateAiProviderPanels()"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_cancellation_guard_rejects_removal_from_the_hidden_app_branch()
    {
        var source = Read("SettingsWindow.OllamaDownloads.cs");
        var branch = Body(Body(source, "private void UpdateOllamaDownloadUi()"), "if (!shown)");
        var mutated = source.Replace(branch, "{ }", StringComparison.Ordinal);

        Assert.NotEqual(source, mutated);
        Assert.NotNull(Record.Exception(() => AssertHiddenAppCancellation(mutated)));
        Assert.Contains("CancelOllamaDownload();", mutated, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("_settingsRepository.Save(_settings);")]
    [InlineData("_settingsRepository.SaveBundle(_settings, []);")]
    public void The_persistence_guard_rejects_an_extra_whole_document_write(string write)
    {
        var save = Body(Read("SettingsWindow.OllamaDownloads.cs"), "private async Task SaveDownloadedOllamaChoiceAsync(");
        var mutated = save.Replace("saved = true;", write + "\n            saved = true;", StringComparison.Ordinal);

        Assert.NotEqual(save, mutated);
        Assert.NotNull(Record.Exception(() => AssertNarrowPersistence(mutated)));
    }

    [Fact]
    public void The_Ollama_service_cancels_window_observation_not_host_owned_actions()
    {
        var source = Read("SettingsWindow.OllamaService.cs");
        var action = Body(source, "private async void OllamaServiceButton_Click(");
        Assert.Contains("OllamaService.StopAsync().WaitAsync(_ollamaServiceReads.Token)", action, StringComparison.Ordinal);
        Assert.Contains("OllamaService.StartAsync().WaitAsync(_ollamaServiceReads.Token)", action, StringComparison.Ordinal);
        Assert.DoesNotContain("OllamaService.Dispose(", source, StringComparison.Ordinal);
        Assert.Empty(typeof(OllamaServiceController).GetMethod(nameof(OllamaServiceController.StartAsync))!.GetParameters());
        Assert.Empty(typeof(OllamaServiceController).GetMethod(nameof(OllamaServiceController.StopAsync))!.GetParameters());
        Assert.Contains("OllamaService.ReadAsync(_ollamaServiceReads.Token)",
            Body(source, "private async Task RefreshOllamaServiceAsync()"), StringComparison.Ordinal);
        Assert.Contains("_ollamaServiceReads.Cancel();", Body(Read("SettingsWindow.xaml.cs"), "private void OnClosed("),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Search_expands_the_download_panel_before_the_visible_focus_wait()
    {
        var source = Read("SettingsWindow.Search.cs");
        var expansion = Body(source, "private void ExpandSearchContainers(");
        Assert.Matches(
            "case\\s+\"OllamaDownloadModelBox\"\\s*:\\s*OllamaDownloadPanel\\.IsExpanded\\s*=\\s*true\\s*;",
            expansion);
        var open = Body(source, "private void OpenSettingsSearchResult(");
        Assert.True(open.IndexOf("ExpandSearchContainers(result.ControlName)", StringComparison.Ordinal) <
            open.IndexOf("FocusSearchTargetWhenVisibleAsync(result, generation)", StringComparison.Ordinal));
    }

    [Fact]
    public void A_download_checks_its_own_reading_not_the_mutable_display_state()
    {
        var download = Body(Read("SettingsWindow.OllamaDownloads.cs"), "private async void OllamaDownloadButton_Click(");
        Assert.Contains("var state = await RefreshLocalAppAsync(preserveEmptySelection: true);", download, StringComparison.Ordinal);
        Assert.Contains("OllamaModelDownload.CompletionProblem(state, model)", download, StringComparison.Ordinal);
        Assert.DoesNotContain("_localAppState", download, StringComparison.Ordinal);
        var refresh = Body(Read("SettingsWindow.LocalApps.cs"), "private async Task<LocalServerState?> RefreshLocalAppAsync(");
        Assert.Equal("return state;",
            Body(refresh, "if (_closed || version != _localAppReadVersion || SelectedLocalApp != app)")[1..^1].Trim());
        Assert.Matches(@"return\s+state\s*;\s*\}$", refresh);
        Assert.True(refresh.LastIndexOf("ShowLocalAppStatus();", StringComparison.Ordinal) <
            refresh.IndexOf("await RefreshOllamaServiceAsync();", StringComparison.Ordinal));
        Assert.Contains("preserveEmptySelection || (app == LocalServerApp.Ollama && _ollamaDownload is not null)", refresh,
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_window_admits_one_download_and_disposes_its_per_operation_client()
    {
        var download = Body(Read("SettingsWindow.OllamaDownloads.cs"), "private async void OllamaDownloadButton_Click(");
        Assert.Contains("_closed || _ollamaDownload is not null || SelectedLocalApp != LocalServerApp.Ollama", download,
            StringComparison.Ordinal);
        Assert.Contains("_ollamaDownload = cancellation;", download, StringComparison.Ordinal);
        Assert.Contains("using (var downloader = new OllamaModelDownloader(_log))", download, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(_ollamaDownload, cancellation)", Body(download, "finally"), StringComparison.Ordinal);
    }

    [Fact]
    public void Every_real_service_launch_uses_the_guarded_process_without_an_unguarded_fallback()
    {
        var source = File.ReadAllText(Path.Combine(Root(), "src", "Scribe.Core", "Cleanup", "OllamaServiceController.cs"));
        Assert.Contains("return OllamaOwnedProcess.Start(StartInfo(executable));", Body(source, "private static IOllamaOwnedProcess StartProcess()"),
            StringComparison.Ordinal);
        Assert.DoesNotMatch(@"\bProcess\s*\.\s*Start\s*\(", source);
    }

    [Fact]
    public void Fixture_identity_transfer_pins_the_native_adapters_and_their_failure_cleanup()
    {
        AssertFixtureProcessIdentity(ReadFixtureTests(), ReadFixture());
    }

    [Fact]
    public void A_transfer_keeps_its_identity_until_the_observation_has_bound_it()
    {
        var calls = new List<string>();
        var identity = new TransferIdentity(calls);
        var creatorRunning = true;

        var observed = OllamaOwnedProcessTests.WithTransferredIdentity(
            () =>
            {
                calls.Add("duplicate");
                creatorRunning = false;
                calls.Add("creator exited");
                return identity;
            },
            held =>
            {
                Assert.False(creatorRunning);
                Assert.Same(identity, held);
                Assert.False(held.Disposed);
                calls.Add("bind");
                return "original";
            });

        Assert.Equal("original", observed);
        Assert.Equal(["duplicate", "creator exited", "bind", "release"], calls);
        Assert.True(identity.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_failed_transfer_or_binding_publishes_no_cleanup_target_and_releases_only_an_acquired_identity(bool bindFails)
    {
        var calls = new List<string>();
        var identity = new TransferIdentity(calls);
        var failure = new InvalidOperationException(bindFails ? "Binding failed." : "The creator exited.");
        string? target = null;

        var actual = Record.Exception(() =>
            target = OllamaOwnedProcessTests.WithTransferredIdentity(
                () =>
                {
                    calls.Add("duplicate");
                    if (!bindFails)
                    {
                        throw failure;
                    }

                    return identity;
                },
                string (held) =>
                {
                    Assert.Same(identity, held);
                    Assert.False(held.Disposed);
                    calls.Add("bind");
                    throw failure;
                }));

        Assert.Same(failure, actual);
        Assert.Null(target);
        Assert.Equal(bindFails, identity.Disposed);
        Assert.Equal(bindFails ? ["duplicate", "bind", "release"] : ["duplicate"], calls);
    }

    [Fact]
    public void A_creator_exit_between_server_and_descendant_transfers_never_binds_the_replacement_pid()
    {
        var calls = new List<string>();
        var identity = new TransferIdentity(calls);
        var server = OllamaOwnedProcessTests.WithTransferredIdentity(() => identity, _ => "original server");
        var descendantCreatorRunning = false;
        var pidOccupant = "unrelated replacement";
        string? descendant = null;
        var pidLookups = 0;
        var ended = new List<string>();

        Assert.Throws<InvalidOperationException>(() =>
            descendant = OllamaOwnedProcessTests.WithTransferredIdentity(
                () =>
                {
                    if (!descendantCreatorRunning)
                    {
                        throw new InvalidOperationException("The descendant creator exited before transfer.");
                    }

                    return new TransferIdentity(calls);
                },
                _ =>
                {
                    pidLookups++;
                    return pidOccupant;
                }));

        ended.Add(server);
        if (descendant is not null)
        {
            ended.Add(descendant);
        }

        Assert.Equal(0, pidLookups);
        Assert.Null(descendant);
        Assert.Equal(["original server"], ended);
        Assert.DoesNotContain(pidOccupant, ended);
    }

    [Theory]
    [InlineData("_ = process.SafeHandle;", "process.Refresh();")]
    [InlineData("server = ReceiveFixtureProcess(owner.SafeHandle, view.ReadInt64(16));",
        "server = HoldFixtureProcess(view.ReadInt32(0));")]
    [InlineData("descendant = ReceiveFixtureProcess(server.SafeHandle, view.ReadInt64(24));",
        "descendant = HoldFixtureProcess(view.ReadInt32(4));")]
    [InlineData("server = HoldFixtureProcess(view.ReadInt32(0));",
        "owned.Dispose();\n            server = HoldFixtureProcess(view.ReadInt32(0));")]
    [InlineData("using var identity = duplicate();\n        return bind(identity);",
        "var identity = duplicate();\n        identity.Dispose();\n        return bind(identity);")]
    [InlineData("() => DuplicateFixtureHandle(creator, publishedHandle)",
        "() => new SafeProcessHandle(new IntPtr(publishedHandle), ownsHandle: false)")]
    [InlineData("throw new Win32Exception(error);", "return identity;")]
    [InlineData("process.Kill();", "process.Kill(entireProcessTree: true);")]
    [InlineData("End(owner, server, descendant, outside);", "End(owner);")]
    public void The_fixture_identity_guard_rejects_acquisition_and_failure_cleanup_bypasses(string binding, string unbound)
    {
        var source = ReadFixtureTests();
        var mutated = source.Replace(binding, unbound, StringComparison.Ordinal);

        Assert.NotEqual(source, mutated);
        Assert.NotNull(Record.Exception(() => AssertFixtureProcessIdentity(mutated, ReadFixture())));
    }

    [Theory]
    [InlineData("RetainForProcessLifetime(child.SafeHandle)", "child.SafeHandle.DangerousGetHandle()")]
    [InlineData("RetainForProcessLifetime(server.SafeHandle)", "server.SafeHandle.DangerousGetHandle()")]
    [InlineData("return retained;",
        "using var released = new SafeProcessHandle(retained, ownsHandle: true);\n        return retained;")]
    [InlineData("view.Write(24, RetainForProcessLifetime(child.SafeHandle).ToInt64());", "view.Write(24, 0L);")]
    [InlineData("throw new Win32Exception(Marshal.GetLastPInvokeError());", "return IntPtr.Zero;")]
    [InlineData("exit.WaitOne(TimeSpan.FromSeconds(45))", "exit.WaitOne()")]
    [InlineData("Environment.Exit(requested ? 0 : 3);", "Environment.Exit(0);")]
    public void The_fixture_identity_guard_rejects_creator_release_publication_and_timeout_bypasses(string safe, string bypass)
    {
        var fixture = ReadFixture();
        var mutated = fixture.Replace(safe, bypass, StringComparison.Ordinal);

        Assert.NotEqual(fixture, mutated);
        Assert.NotNull(Record.Exception(() => AssertFixtureProcessIdentity(ReadFixtureTests(), mutated)));
    }

    private static void AssertFixtureProcessIdentity(string source, string fixture)
    {
        Assert.Equal(1, Regex.Count(source, @"\bProcess\s*\.\s*GetProcessById\s*\("));
        Assert.Equal(3, Regex.Count(source, @"\bHoldFixtureProcess\s*\("));
        Assert.Equal(4, Regex.Count(source, @"\bReceiveFixtureProcess\s*\("));
        var bind = Body(source, "private static Process HoldFixtureProcess(");
        Assert.Contains("var process = Process.GetProcessById(processId);", bind, StringComparison.Ordinal);
        Assert.Matches(@"^\{\s*_ = process\.SafeHandle;\s*return process;\s*\}$", Body(bind, "try"));
        Assert.Matches(@"^\{\s*process\.Dispose\(\);\s*throw;\s*\}$", Body(bind, "catch"));

        Assert.Matches(@"^\{\s*using var identity = duplicate\(\);\s*return bind\(identity\);\s*\}$",
            Body(source, "internal static TProcess WithTransferredIdentity<"));
        var receive = Body(source, "private static Process ReceiveFixtureProcess(");
        AssertInOrder(receive,
            "return WithTransferredIdentity(",
            "() => DuplicateFixtureHandle(creator, publishedHandle)",
            "var processId = GetProcessId(identity);",
            "if (processId == 0)",
            "return HoldFixtureProcess(checked((int)processId));");
        Assert.Equal("throw new Win32Exception(Marshal.GetLastPInvokeError());",
            Body(receive, "if (processId == 0)")[1..^1].Trim());
        var duplicate = Body(source, "private static SafeProcessHandle DuplicateFixtureHandle(");
        Assert.Contains(
            "if (!DuplicateHandle(creator, new IntPtr(publishedHandle), new IntPtr(-1), out var identity, 0, false, 2))",
            duplicate, StringComparison.Ordinal);
        Assert.Matches(
            @"^\{\s*var error = Marshal.GetLastPInvokeError\(\);\s*identity.Dispose\(\);\s*throw new Win32Exception\(error\);\s*\}$",
            Body(duplicate, "if (!DuplicateHandle("));
        Assert.Matches(@"return identity;\s*\}$", duplicate);
        Assert.Contains("SafeProcessHandle sourceProcess, IntPtr source, IntPtr targetProcess, out SafeProcessHandle target",
            source, StringComparison.Ordinal);

        var owner = Body(source,
            "public async Task An_immediate_or_abrupt_owner_exit_stops_its_server_and_descendants_but_not_an_outside_instance(");
        AssertInOrder(owner,
            "MemoryMappedFile.CreateNew(mappingName, 32)",
            "new EventWaitHandle(false, EventResetMode.ManualReset, readyName + \".Exit\")",
            "using var owner = Process.Start(info)",
            "try",
            "outside = Process.Start(",
            "Assert.Equal(\"ready\", await owner.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));",
            "Assert.True(ready.WaitOne(TimeSpan.FromSeconds(10))",
            "server = ReceiveFixtureProcess(owner.SafeHandle, view.ReadInt64(16));",
            "descendant = ReceiveFixtureProcess(server.SafeHandle, view.ReadInt64(24));",
            "Assert.False(server.HasExited);",
            "Assert.False(descendant.HasExited);",
            "if (abrupt)",
            "owner.Kill();",
            "exit.Set();",
            "Assert.Equal(0, owner.ExitCode);",
            "Assert.False(outside.HasExited);");
        Assert.Equal("End(owner, server, descendant, outside);", Body(owner, "finally")[1..^1].Trim());

        var direct = Body(source, "public void Stop_or_disposal_after_the_server_exits_also_retires_its_descendants(");
        Assert.Equal(2, Regex.Count(direct, @"\bowned\.Dispose\(\);"));
        AssertInOrder(direct,
            "MemoryMappedFile.CreateNew(mappingName, 32)",
            "using var owned = OllamaOwnedProcess.Start(",
            "try",
            "outside = Process.Start(",
            "Assert.True(ready.WaitOne(TimeSpan.FromSeconds(10))",
            "server = HoldFixtureProcess(view.ReadInt32(0));",
            "descendant = ReceiveFixtureProcess(server.SafeHandle, view.ReadInt64(24));",
            "Assert.False(server.HasExited);",
            "Assert.False(descendant.HasExited);",
            "if (selfExited)",
            "owned.Dispose();");
        var cleanup = Body(direct, "finally");
        Assert.Equal("owned.Dispose();", Body(cleanup, "try")[1..^1].Trim());
        Assert.Equal("End(server, descendant, outside);", Body(cleanup, "finally")[1..^1].Trim());
        Assert.DoesNotContain("entireProcessTree", source, StringComparison.Ordinal);
        AssertInOrder(Body(source, "private static void End("),
            "foreach (var process in processes)",
            "if (process is null)",
            "continue;",
            "try",
            "using (process)",
            "process.Kill();",
            "Assert.True(process.WaitForExit(TimeSpan.FromSeconds(10)));",
            "catch (Exception failure)",
            "(failures ??= []).Add(failure);",
            "throw new AggregateException(");

        Assert.Equal(1, Regex.Count(fixture, @"\bProcess\s*\.\s*GetProcessById\s*\("));
        Assert.Equal(3, Regex.Count(fixture, @"\bRetainForProcessLifetime\s*\("));
        var run = Body(fixture, "internal static int Run(");
        Assert.Matches(@"^\{\s*Thread.Sleep\(TimeSpan.FromSeconds\(45\)\);\s*return 0;\s*\}$",
            Body(run, "if (role == \"leaf\")"));
        AssertInOrder(Body(run, "if (role == \"server\")"),
            "using var child = Process.Start(",
            "view.Write(0, Environment.ProcessId);",
            "view.Write(24, RetainForProcessLifetime(child.SafeHandle).ToInt64());",
            "ready.Set();",
            "Thread.Sleep(TimeSpan.FromSeconds(45));",
            "return 0;");
        AssertInOrder(run,
            "using var exit = EventWaitHandle.OpenExisting(readyEvent + \".Exit\");",
            "using var owned = (IDisposable)",
            "if (!serverReady.WaitOne(TimeSpan.FromSeconds(10)))",
            "throw new TimeoutException(",
            "using var server = Process.GetProcessById(ownerView.ReadInt32(0));",
            "ownerView.Write(16, RetainForProcessLifetime(server.SafeHandle).ToInt64());",
            "Console.WriteLine(\"ready\");",
            "var requested = exit.WaitOne(TimeSpan.FromSeconds(45));",
            "Environment.Exit(requested ? 0 : 3);");

        var retain = Regex.Replace(Body(fixture, "private static IntPtr RetainForProcessLifetime("), @"//[^\r\n]*", string.Empty);
        Assert.Matches(
            @"^\{\s*if \(!DuplicateHandle\(new IntPtr\(-1\), process, new IntPtr\(-1\), out var retained, 0, false, 2\)\)" +
            @"\s*\{\s*throw new Win32Exception\(Marshal.GetLastPInvokeError\(\)\);\s*\}\s*return retained;\s*\}$",
            retain);
        Assert.Contains("IntPtr sourceProcess, SafeProcessHandle source, IntPtr targetProcess, out IntPtr target",
            fixture, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"\b(?:CloseHandle|Dispose|ReadLine)\s*\(", fixture);
    }

    private sealed class TransferIdentity(List<string> calls) : IDisposable
    {
        internal bool Disposed { get; private set; }

        public void Dispose()
        {
            Assert.False(Disposed);
            Disposed = true;
            calls.Add("release");
        }
    }

    private static void AssertInOrder(string source, params string[] fragments)
    {
        var next = 0;
        foreach (var fragment in fragments)
        {
            var index = source.IndexOf(fragment, next, StringComparison.Ordinal);
            Assert.True(index >= next, $"{fragment} was missing or out of order.");
            next = index + fragment.Length;
        }
    }

    private static string ReadFixtureTests() =>
        File.ReadAllText(Path.Combine(Root(), "tests", "Scribe.Core.Tests", "OllamaOwnedProcessTests.cs")).ReplaceLineEndings("\n");

    private static string ReadFixture() =>
        File.ReadAllText(Path.Combine(Root(), "tests", "Scribe.LogAppendChild", "OllamaProcessFixture.cs")).ReplaceLineEndings("\n");

    private static void AssertHiddenAppCancellation(string source)
    {
        var update = Body(source, "private void UpdateOllamaDownloadUi()");
        Assert.Contains("SelectedLocalApp == LocalServerApp.Ollama", update, StringComparison.Ordinal);
        Assert.Equal("CancelOllamaDownload();", Body(update, "if (!shown)")[1..^1].Trim());
    }

    private static void AssertNarrowPersistence(string save)
    {
        var calls = Regex.Matches(save, @"\b_settingsRepository\s*\.\s*(?<call>\w+)\s*\(");
        Assert.Equal("Update", Assert.Single(calls).Groups["call"].Value);
        Assert.Contains("_settingsRepository.Update(settings => OllamaModelDownload.ApplyChoice(settings, model))", save,
            StringComparison.Ordinal);
    }

    private static string Body(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{signature} was not found.");
        var open = source.IndexOf('{', start);
        var depth = 0;
        for (var index = open; index < source.Length; index++)
        {
            depth += source[index] switch { '{' => 1, '}' => -1, _ => 0 };
            if (depth == 0)
            {
                return source[open..(index + 1)];
            }
        }

        throw new InvalidOperationException($"{signature} has no end.");
    }

    private static XElement Named(XDocument document, string name) =>
        Assert.Single(document.Descendants(), element => (string?)element.Attribute(Xaml + "Name") == name);

    private static string Read(string name) =>
        File.ReadAllText(Path.Combine(Root(), "src", "Scribe.App", "Settings", name));

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Scribe.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
