using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Scribe.Core.Cleanup;
using Scribe.Core.Diagnostics;
using Scribe.Core.Infrastructure;
using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.Settings;
using Scribe.Core.Tests.Libraries.Integration;
using Scribe.Core.Tests.Vocabulary;
using Scribe.Core.Vocabulary;

namespace Scribe.Core.Tests;

public sealed class TryDictationWindowTests
{
    [Fact]
    public void Speech_sign_in_actions_work_with_cleanup_off_without_exposing_other_setup() =>
        PrivateDesktopTest.Run(typeof(TryDictationWindowTests),
            () => CheckSpeechSignIn(false));

    [Fact]
    public void Speech_sign_in_actions_do_not_replace_the_selected_cleanup_service() =>
        PrivateDesktopTest.Run(typeof(TryDictationWindowTests),
            () => CheckSpeechSignIn(true));

    [Fact]
    public void Speech_sign_in_keeps_custom_cleanup_setup_reachable() =>
        PrivateDesktopTest.Run(typeof(TryDictationWindowTests),
            () => CheckSpeechSignIn(true, CleanupProvider.OpenAiCompatible));

    private static void CheckSpeechSignIn(bool enabled, CleanupProvider provider = CleanupProvider.FoundryLocal)
    {
        using var rig = new WindowRig(configure: settings =>
        {
            settings.EnableAiCleanup = enabled;
            settings.AiCleanupProvider = provider;
        });
        rig.Open();
        foreach (var scale in new[] { 1.0, 2.25 })
        {
            rig.SetTextScale(scale);
            rig.Invoke("DictationMaiAuthLink_Click", rig.Control<Button>("DictationMaiAuthButton"), new RoutedEventArgs());
            WindowRig.PumpUntil(() => rig.Control<FrameworkElement>("AzureCliRadio").IsKeyboardFocusWithin);
            foreach (var (known, installed, expected) in new[]
            {
                (false, false, AiCleanupActionId.CheckSignIn),
                (true, false, AiCleanupActionId.InstallAzureCli),
                (true, true, AiCleanupActionId.SignIn),
            })
            {
                rig.SetField("_azureConnectionKnown", known);
                rig.SetField("_azureCliInstalled", installed);
                rig.Invoke("RefreshAiStatus");
                var row = rig.Control<FrameworkElement>("AzureSignInStatusRow");
                Assert.True(row.IsVisible);
                var action = Assert.IsType<AiCleanupAction>(
                    row.GetType().GetField("_primary", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(row));
                Assert.Equal(expected, action.Id);
            }
            Assert.Equal(enabled, rig.Control<FrameworkElement>(
                provider == CleanupProvider.FoundryLocal ? "FoundryPanel" : "CustomPanel").IsVisible);
            Assert.False(rig.Control<FrameworkElement>("AiOffHelperText").IsVisible);
            Assert.Equal(enabled, rig.Control<FrameworkElement>("AiProviderChoices").IsVisible);
            var draft = rig.Result<AppSettings>("CaptureDraftSettings");
            Assert.Equal(enabled, draft.EnableAiCleanup);
            Assert.Equal(provider, draft.AiCleanupProvider);
        }
        rig.Control<CheckBox>("AiCleanupCheck").IsChecked = !enabled;
        Assert.False(rig.Control<FrameworkElement>("AzurePanel").IsVisible);
        Assert.Equal(!enabled, rig.Control<FrameworkElement>(
            provider == CleanupProvider.FoundryLocal ? "FoundryPanel" : "CustomPanel").IsVisible);
    }

    [Fact]
    public void Speech_only_key_test_uses_default_instructions_on_Responses_and_its_fallback() =>
        PrivateDesktopTest.Run(typeof(TryDictationWindowTests),
            () => CheckAzureCredentialAction(false, CleanupProvider.FoundryLocal, false));

    [Fact]
    public void Disabled_Azure_cleanup_instructions_do_not_leave_during_the_speech_key_test() =>
        PrivateDesktopTest.Run(typeof(TryDictationWindowTests),
            () => CheckAzureCredentialAction(false, CleanupProvider.AzureFoundry, false));

    [Fact]
    public void Local_cleanup_instructions_do_not_leave_during_the_speech_key_test() =>
        PrivateDesktopTest.Run(typeof(TryDictationWindowTests),
            () => CheckAzureCredentialAction(true, CleanupProvider.FoundryLocal, false));

    [Fact]
    public void Enabled_Azure_cleanup_tests_its_authored_instructions() =>
        PrivateDesktopTest.Run(typeof(TryDictationWindowTests),
            () => CheckAzureCredentialAction(true, CleanupProvider.AzureFoundry, false));

    [Fact]
    public void Speech_service_principal_action_tests_and_publishes_the_Azure_candidate() =>
        PrivateDesktopTest.Run(typeof(TryDictationWindowTests),
            () => CheckAzureCredentialAction(false, CleanupProvider.FoundryLocal, true));

    private static void CheckAzureCredentialAction(bool enabled, CleanupProvider provider, bool servicePrincipal)
    {
        const string style = "private writing style canary";
        const string frontier = "private detailed instructions canary";
        const string local = "private short instructions canary";
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new VocabularyCleanupHarness(source: null);
        try
        {
            CleanupOptions? tested = null;
            if (servicePrincipal)
            {
                cleanup.Service.ProviderFactoryForTesting = async (candidate, ct) =>
                {
                    tested = candidate;
                    await release.Task.WaitAsync(ct);
                    return instructions => new ChatClientAgent(new ProbeClient(), instructions: instructions);
                };
            }
            else
            {
                cleanup.Network.Respond = async (request, ct) =>
                {
                    await release.Task.WaitAsync(ct);
                    return request.Path.EndsWith("/responses", StringComparison.Ordinal)
                        ? CanaryNetwork.Json(System.Net.HttpStatusCode.BadRequest,
                            "{\"error\":{\"message\":\"The requested operation is unsupported.\"}}")
                        : CanaryNetwork.Echo(request);
                };
            }

            using var rig = new WindowRig(configure: settings =>
            {
                settings.EnableAiCleanup = enabled;
                settings.AiCleanupProvider = provider;
                settings.AiCleanupAzureEndpoint = "https://example.openai.azure.com";
                settings.AiCleanupAzureDeployment = "test-model";
                settings.AiCleanupAzureApiKey = servicePrincipal ? null : "test-key";
                settings.AiCleanupAzureAuthMode = servicePrincipal ? AzureAuthMode.ServicePrincipal : AzureAuthMode.AzureCli;
                settings.AiCleanupAzureTenantId = "11111111-1111-1111-1111-111111111111";
                settings.AiCleanupAzureClientId = "22222222-2222-2222-2222-222222222222";
                settings.AiCleanupAzureClientSecret = "test-secret";
                settings.AiCleanupWritingStyle = style;
                settings.AiCleanupFrontierPrompt = frontier;
                settings.AiCleanupLocalPrompt = local;
                settings.AiCleanupPromptStyle = CleanupPromptStyle.Frontier;
            }, cleanup: cleanup.Service);
            rig.Open();
            rig.Invoke("RevealAzureSpeechCredentials", servicePrincipal ? "AzureClientIdBox" : "AzureApiKeyBox");
            rig.Control<RadioButton>(servicePrincipal ? "AzureServicePrincipalRadio" : "AzureApiKeyRadio").IsChecked = true;
            rig.Invoke("RefreshAiStatus");
            var row = rig.Control<UserControl>("AzureVerifyStatusRow");
            var button = Assert.IsAssignableFrom<Button>(row.FindName("PrimaryButton"));
            Assert.True(button.IsEnabled);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WindowRig.PumpUntil(() => rig.Result<AzureAiSetupState>("CurrentAzureSetup").Result == AzureSetupResult.Verifying);
            release.TrySetResult();
            var expected = servicePrincipal ? AzureSetupResult.ServicePrincipalVerified : AzureSetupResult.ApiKeyVerified;
            WindowRig.PumpUntil(() => rig.Result<AzureAiSetupState>("CurrentAzureSetup").Result == expected);

            Assert.Equal(provider, rig.Result<AppSettings>("CaptureDraftSettings").AiCleanupProvider);
            if (servicePrincipal)
            {
                Assert.NotNull(tested);
                Assert.Equal(CleanupProvider.AzureFoundry, tested.Provider);
                Assert.Equal(AzureAuthMode.ServicePrincipal, tested.AzureAuthMode);
                Assert.Equal("11111111-1111-1111-1111-111111111111", tested.AzureTenantId);
                Assert.Equal("22222222-2222-2222-2222-222222222222", tested.AzureClientId);
                Assert.Equal("test-secret", tested.AzureClientSecret);
                Assert.Equal(string.Empty, tested.WritingStyle);
                Assert.Equal(string.Empty, tested.FrontierPrompt);
                Assert.Equal(string.Empty, tested.LocalPrompt);
                Assert.Empty(cleanup.Network.Sent);
            }
            else
            {
                Assert.Collection(cleanup.Network.Sent,
                    request => Assert.EndsWith("/responses", request.Path, StringComparison.Ordinal),
                    request => Assert.EndsWith("/chat/completions", request.Path, StringComparison.Ordinal));
                Assert.All(cleanup.Network.Sent, request =>
                {
                    Assert.True(request.IsProbe);
                    if (enabled && provider == CleanupProvider.AzureFoundry)
                    {
                        Assert.True(request.Carries(style));
                        Assert.True(request.Carries(frontier));
                    }
                    else
                    {
                        Assert.False(request.Carries(style));
                        Assert.False(request.Carries(frontier));
                        Assert.False(request.Carries(local));
                    }
                });
            }
        }
        finally
        {
            release.TrySetResult();
            Task.Run(async () => await cleanup.DisposeAsync()).GetAwaiter().GetResult();
        }
    }

    private sealed class ProbeClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public void Speech_credentials_test_the_Azure_candidate_not_the_selected_cleanup_service() =>
        PrivateDesktopTest.Run(typeof(TryDictationWindowTests), () =>
        {
            using var rig = new WindowRig(configure: settings =>
            {
                settings.EnableAiCleanup = false;
                settings.AiCleanupProvider = CleanupProvider.FoundryLocal;
                settings.AiCleanupAzureEndpoint = "https://example.openai.azure.com";
                settings.AiCleanupAzureDeployment = "test-model";
                settings.AiCleanupAzureApiKey = "test-key";
            });
            rig.Open();
            rig.Invoke("RevealAzureSpeechCredentials", "AzureApiKeyBox");
            rig.Control<RadioButton>("AzureApiKeyRadio").IsChecked = true;
            rig.Invoke("RefreshAiStatus");
            var row = rig.Control<FrameworkElement>("AzureVerifyStatusRow");
            WindowRig.PumpUntil(() => row.IsVisible);
            var action = Assert.IsType<AiCleanupAction>(
                row.GetType().GetField("_primary", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(row));
            Assert.Equal(AiCleanupActionId.TestConnection, action.Id);
            Assert.True(action.IsEnabled);
            Assert.Equal(CleanupProvider.AzureFoundry,
                rig.Result<CleanupOptions>("BuildAzureCandidateOptions").Provider);
            Assert.Equal(CleanupProvider.FoundryLocal,
                rig.Result<AppSettings>("CaptureDraftSettings").AiCleanupProvider);
            Assert.False(rig.Control<FrameworkElement>("AzureDiscoveryPanel").IsVisible);

            var subscriptionReads = 0;
            Func<CancellationToken, Task<(bool, IReadOnlyList<AzureSubscription>, string)>> read = _ =>
            {
                subscriptionReads++;
                throw new InvalidOperationException("Non-CLI identities must not discover subscriptions.");
            };
            rig.SetProperty("AzureSubscriptionsForTesting", read);
            foreach (var radio in new[] { "AzureApiKeyRadio", "AzureServicePrincipalRadio" })
            {
                rig.Control<RadioButton>(radio).IsChecked = true;
                rig.SetField("_azureSignInStatus", new AzureSignInStatus(true, null));
                rig.Result<Task>("ListAzureDeploymentsAsync").GetAwaiter().GetResult();
                Assert.Equal(0, subscriptionReads);
                Assert.False(rig.Control<FrameworkElement>("AzureDiscoveryPanel").IsVisible);
            }
        });

    [Fact]
    public void Changing_subscription_during_first_discovery_rejects_the_older_deployments() =>
        PrivateDesktopTest.Run(typeof(TryDictationWindowTests), () => CheckSubscriptionChange(false));

    [Fact]
    public void Changing_subscription_after_first_discovery_fails_starts_a_new_listing() =>
        PrivateDesktopTest.Run(typeof(TryDictationWindowTests), () => CheckSubscriptionChange(true));

    [Fact]
    public void Choosing_all_subscriptions_during_first_discovery_preserves_that_explicit_choice() =>
        PrivateDesktopTest.Run(typeof(TryDictationWindowTests), () => CheckSubscriptionChange(false, true));

    [Fact]
    public void Choosing_all_subscriptions_after_first_discovery_fails_preserves_that_explicit_choice() =>
        PrivateDesktopTest.Run(typeof(TryDictationWindowTests), () => CheckSubscriptionChange(true, true));

    [Fact]
    public void Changing_tenant_discards_the_previous_tenants_explicit_all_selection() =>
        PrivateDesktopTest.Run(typeof(TryDictationWindowTests), () =>
        {
            var discovery = new HeldAzureDiscovery { AllowAll = true };
            using var rig = new WindowRig(configure: settings =>
            {
                settings.AiCleanupAzureSubscriptionId = "A";
                settings.AiCleanupAzureSubscriptionName = "A";
                settings.AiCleanupAzureSubscriptionTenantId = "tenant";
            }, azureDiscovery: discovery);
            rig.Open();
            IReadOnlyList<AzureSubscription> subscriptions =
                [new("A", "A", "tenant", IsDefault: true), new("B", "B", "tenant")];
            Func<CancellationToken, Task<(bool, IReadOnlyList<AzureSubscription>, string)>> read =
                _ => Task.FromResult((true, subscriptions, string.Empty));
            rig.SetProperty("AzureSubscriptionsForTesting", read);
            rig.SetField("_azureSignInStatus", new AzureSignInStatus(true, null));
            rig.Invoke("PopulateAzureSubscriptions", subscriptions, false);
            var box = rig.Control<ComboBox>("AzureSubscriptionBox");
            box.SelectedItem = Assert.IsType<string>(box.Items[0]);
            WindowRig.PumpUntil(() => rig.Control<TextBox>("AzureDeploymentBox").Text == "model-All");
            Assert.Equal(1, discovery.AllCalls);

            rig.Control<TextBox>("AzureTenantBox").Text = "another-tenant";
            rig.SetField("_azureSignInStatus", new AzureSignInStatus(true, null));
            discovery.ReleaseFirst.TrySetResult();
            var listing = rig.Result<Task>("ListAzureDeploymentsAsync");
            WindowRig.PumpUntil(() => listing.IsCompleted);
            listing.GetAwaiter().GetResult();

            Assert.Equal("A", box.SelectedItem);
            Assert.Equal(1, discovery.SelectedCalls);
            Assert.Equal(1, discovery.AllCalls);
        });

    private static void CheckSubscriptionChange(bool firstFails, bool chooseAll = false)
    {
        var discovery = new HeldAzureDiscovery { FailFirst = firstFails, AllowAll = chooseAll };
        using var rig = new WindowRig(configure: settings =>
        {
            settings.AiCleanupAzureSubscriptionId = "A";
            settings.AiCleanupAzureSubscriptionName = "A";
            settings.AiCleanupAzureSubscriptionTenantId = "tenant";
        }, azureDiscovery: discovery);
        rig.Open();
        IReadOnlyList<AzureSubscription> subscriptions =
            [new("A", "A", "tenant", IsDefault: true), new("B", "B", "tenant")];
        Func<CancellationToken, Task<(bool, IReadOnlyList<AzureSubscription>, string)>> read =
            _ => Task.FromResult((true, subscriptions, string.Empty));
        rig.SetProperty("AzureSubscriptionsForTesting", read);
        rig.SetField("_azureSignInStatus", new AzureSignInStatus(true, null));
        rig.Invoke("PopulateAzureSubscriptions", subscriptions, false);
        var first = rig.Result<Task>("ListAzureDeploymentsAsync");
        try
        {
            WindowRig.PumpUntil(() => discovery.FirstStarted.Task.IsCompleted);
            if (firstFails)
            {
                discovery.ReleaseFirst.TrySetResult();
                WindowRig.PumpUntil(() => first.IsCompleted);
                first.GetAwaiter().GetResult();
            }
            Assert.False(rig.Field<bool>("_azureAutoListed"));
            var box = rig.Control<ComboBox>("AzureSubscriptionBox");
            var chosen = chooseAll ? Assert.IsType<string>(box.Items[0]) : "B";
            box.SelectedItem = chosen;
            var expectedModel = chooseAll ? "model-All" : "model-B";
            WindowRig.PumpUntil(() => rig.Control<TextBox>("AzureDeploymentBox").Text == expectedModel);
            discovery.ReleaseFirst.TrySetResult();
            WindowRig.PumpUntil(() => first.IsCompleted);
            first.GetAwaiter().GetResult();
            Assert.Equal(chosen, box.SelectedItem);
            Assert.Equal(expectedModel, rig.Control<TextBox>("AzureDeploymentBox").Text);
            Assert.Equal(chooseAll ? "https://all.openai.azure.com" : "https://b.openai.azure.com",
                rig.Control<TextBox>("AzureEndpointBox").Text);
            Assert.DoesNotContain("model-A", rig.Control<ComboBox>("AzureModelBox").Items.Cast<string>());
        }
        finally
        {
            discovery.ReleaseFirst.TrySetResult();
            WindowRig.PumpUntil(() => first.IsCompleted);
        }
    }

    [Fact]
    public void Subscription_listing_failure_does_not_repeat_or_display_command_error_text() =>
        PrivateDesktopTest.Run(typeof(TryDictationWindowTests), () =>
        {
            using var rig = new WindowRig();
            rig.Open();
            Func<CancellationToken, Task<(bool, IReadOnlyList<AzureSubscription>, string)>> read =
                _ => Task.FromResult((false, (IReadOnlyList<AzureSubscription>)Array.Empty<AzureSubscription>(),
                    "Couldn't list Azure subscriptions (private command detail)."));
            rig.SetProperty("AzureSubscriptionsForTesting", read);
            rig.SetField("_azureSignInStatus", new AzureSignInStatus(true, null));
            rig.Result<Task>("ListAzureDeploymentsAsync").GetAwaiter().GetResult();
            Assert.Equal(AzureSetupResult.ListingFailed, rig.Field<AzureAiSetupState>("_azureModelListing").Result);
            var reason = rig.Field<AzureAiSetupState>("_azureModelListing").SafeReason!;
            Assert.DoesNotContain("private command detail", reason);
            Assert.DoesNotContain("Couldn't list", reason);
        });

    private sealed class HeldAzureDiscovery : IAzureFoundryDiscovery
    {
        public bool FailFirst { get; init; }
        public bool AllowAll { get; init; }
        public int AllCalls { get; private set; }
        public int SelectedCalls { get; private set; }
        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyList<AzureFoundryDeployment>> DiscoverAsync(
            string? tenantId = null, string? subscriptionId = null, CancellationToken cancellationToken = default,
            AzureServicePrincipal? servicePrincipal = null)
        {
            SelectedCalls++;
            if (subscriptionId == "A")
            {
                FirstStarted.TrySetResult();
                await ReleaseFirst.Task.WaitAsync(cancellationToken);
                if (FailFirst)
                {
                    throw new InvalidOperationException("The scripted first discovery failed.");
                }
            }
            return [new(subscriptionId!, subscriptionId!, "tenant", "group", "account", "OpenAI",
                $"https://{subscriptionId!.ToLowerInvariant()}.openai.azure.com", $"model-{subscriptionId}",
                $"model-{subscriptionId}", null, "location")];
        }

        public Task<IReadOnlyList<AzureFoundryDeployment>> DiscoverAcrossSubscriptionsAsync(
            string tenantId, IReadOnlyCollection<string> subscriptionIds, string credentialSubscriptionId,
            CancellationToken cancellationToken = default, AzureServicePrincipal? servicePrincipal = null)
        {
            Assert.True(AllowAll, "The selected subscription must be used.");
            AllCalls++;
            Assert.Equal(["A", "B"], subscriptionIds.Order(StringComparer.Ordinal));
            return Task.FromResult<IReadOnlyList<AzureFoundryDeployment>>(
                [new("B", "B", "tenant", "group", "account", "OpenAI",
                    "https://all.openai.azure.com", "model-All", "model-All", null, "location")]);
        }

        public Task<AzureSignInStatus> GetSignInStatusAsync(
            string? tenantId = null, string? subscriptionId = null, CancellationToken cancellationToken = default,
            AzureServicePrincipal? servicePrincipal = null) =>
            throw new InvalidOperationException("Changing the subscription must reuse the checked sign-in.");
    }

    [Fact]
    public void Subscription_changes_retire_unfinished_or_failed_initial_listings() =>
        PrivateDesktopTest.Run(typeof(TryDictationWindowTests), () =>
        {
            using var rig = new WindowRig();
            rig.Open();
            rig.SetField("_azureAutoListed", false);
            foreach (var state in new[] { AzureSetupResult.ListingModels, AzureSetupResult.ListingFailed })
            {
                rig.SetField("_azureDeploymentLoadVersion", 41);
                rig.SetField("_azureModelListing", new AzureAiSetupState(state));
                rig.Invoke("AzureSubscriptionBox_SelectionChanged", rig.Control<ComboBox>("AzureSubscriptionBox"),
                    new SelectionChangedEventArgs(Selector.SelectionChangedEvent, Array.Empty<object>(), Array.Empty<object>()));
                Assert.Equal(42, rig.Field<int>("_azureDeploymentLoadVersion"));
                Assert.Null(rig.NullableField("_azureModelListing"));
            }
        });

    [Fact]
    public void Missing_speech_key_reveals_credentials_with_cleanup_off() =>
        PrivateDesktopTest.Run(typeof(TryDictationWindowTests),
            () => CheckSpeechKeyFocus(false, CleanupProvider.AzureFoundry));

    [Fact]
    public void Missing_speech_key_reveals_credentials_without_changing_cleanup_provider() =>
        PrivateDesktopTest.Run(typeof(TryDictationWindowTests),
            () => CheckSpeechKeyFocus(true, CleanupProvider.FoundryLocal));

    private static void CheckSpeechKeyFocus(bool cleanupEnabled, CleanupProvider provider)
    {
        using var rig = new WindowRig(configure: settings =>
        {
            settings.TranscriptionModelId = Scribe.Core.Transcription.TranscriptionModelCatalog.MaiTranscribe2Id;
            settings.EnableAiCleanup = cleanupEnabled;
            settings.AiCleanupProvider = provider;
        });
        rig.Open();
        rig.Invoke("ShowPage", SettingsPage.AiCleanup, Type.Missing);
        rig.Control<RadioButton>("AzureApiKeyRadio").IsChecked = true;
        var before = rig.Result<AppSettings>("CaptureDraftSettings");
        Assert.False(rig.Control<FrameworkElement>("AzureApiKeyBox").IsVisible);

        var issue = Assert.Single(SettingsDraftValidator.Validate(new SettingsDraft(before,
            AzureApiKeySelected: true)), issue => issue.Code == ValidationCode.ApiKeyEmpty);
        foreach (var scale in new[] { 1.0, 2.25 })
        {
            rig.SetTextScale(scale);
            rig.Invoke("ShowPage", SettingsPage.Advanced, Type.Missing);
            rig.Invoke("ShowValidationIssue", issue);
            WindowRig.PumpUntil(() => rig.Control<FrameworkElement>("AzureApiKeyBox").IsVisible);
            WindowRig.PumpUntil(() => rig.Control<FrameworkElement>("AzureApiKeyBox").IsKeyboardFocusWithin);
        }
        var after = rig.Result<AppSettings>("CaptureDraftSettings");
        Assert.Equal(cleanupEnabled, after.EnableAiCleanup);
        Assert.Equal(provider, after.AiCleanupProvider);
    }

    [Fact]
    public void Diagnostics_shows_the_live_setup_failure_before_any_dictation_and_clears_it_after_recovery() =>
        PrivateDesktopTest.Run(typeof(TryDictationWindowTests), () =>
        {
            using var rig = new WindowRig();
            var cleanup = rig.Services.GetRequiredService<ITextCleanupService>();
            cleanup.Configure(CleanupOptions.Disabled with { Enabled = true, Provider = CleanupProvider.OpenAiCompatible });
            Assert.Equal(CleanupStatus.Unavailable, cleanup.Status);
            rig.Open();
            rig.Invoke("ShowPage", SettingsPage.Diagnostics, Type.Missing);

            foreach (var scale in new[] { 1.0, 2.25 })
            {
                rig.SetTextScale(scale);
                Assert.Equal(Visibility.Visible, rig.Control<FrameworkElement>("CleanupProblemCard").Visibility);
                Assert.Contains(cleanup.StatusDetail!, rig.Control<TextBlock>("CleanupProblemText").Text, StringComparison.Ordinal);
                Assert.Contains("Scribe types what it hears", rig.Control<TextBlock>("CleanupProblemText").Text, StringComparison.Ordinal);
            }

            var scroll = rig.Control<ScrollViewer>("DiagnosticsScrollViewer");
            scroll.ScrollToEnd();
            WindowRig.PumpUntil(() => scroll.VerticalOffset > 0);
            rig.Invoke("ShowPage", SettingsPage.Diagnostics, Type.Missing);
            WindowRig.PumpUntil(() => scroll.VerticalOffset == 0);
            Assert.Equal(Visibility.Visible, rig.Control<FrameworkElement>("CleanupProblemCard").Visibility);

            cleanup.Configure(CleanupOptions.Disabled);
            WindowRig.PumpUntil(() => rig.Control<FrameworkElement>("CleanupProblemCard").Visibility == Visibility.Collapsed);
            Assert.Empty(rig.Control<TextBlock>("CleanupProblemText").Text);
        });

    [Fact]
    public void Opening_Diagnostics_again_refreshes_the_problem_list_in_the_existing_window() =>
        PrivateDesktopTest.Run(typeof(TryDictationWindowTests), () =>
        {
            using var rig = new WindowRig();
            rig.Open();
            rig.Invoke("ShowPage", SettingsPage.Diagnostics, Type.Missing);
            WindowRig.PumpUntil(() => rig.Control<TextBlock>("NoFailuresText").Text != "Loading...");
            Assert.Empty(rig.Control<DataGrid>("FailuresGrid").Items);

            const string reason = "Ollama's model process crashed (500). Restart Ollama and try again.";
            rig.Services.GetRequiredService<ICleanupFailureLog>().Add(CleanupFailure.New(reason, model: "test-model"));
            rig.Invoke("ShowPage", SettingsPage.Diagnostics, Type.Missing);
            WindowRig.PumpUntil(() => rig.Control<DataGrid>("FailuresGrid").Items.Count == 1);
            var row = rig.Control<DataGrid>("FailuresGrid").Items[0];
            Assert.Equal(reason, row.GetType().GetProperty("Reason")!.GetValue(row));

            rig.Invoke("TryDictationOpenDiagnostics_Click", rig.Control<Button>("TryDictationDiagnosticsButton"), new RoutedEventArgs());
            Assert.Equal(Visibility.Visible, rig.Control<FrameworkElement>("SectionDiagnostics").Visibility);
        });

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

        public WindowRig(int idleMinutes = 10, Action<AppSettings>? configure = null,
            IAzureFoundryDiscovery? azureDiscovery = null, ITextCleanupService? cleanup = null)
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
            if (azureDiscovery is not null)
            {
                services.AddSingleton(azureDiscovery);
            }
            if (cleanup is not null)
            {
                services.AddSingleton(cleanup);
            }
            services.AddSingleton(assembly.GetType("Scribe.App.Infrastructure.AzureCliInstaller", throwOnError: true)!);
            services.AddSingleton<StartupRegistration>();
            Services = services.BuildServiceProvider();
            var settings = Services.GetRequiredService<ISettingsRepository>();
            var initial = AppSettings.CreateDefault();
            initial.ReleaseModelsAfterIdleMinutes = idleMinutes;
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

        public object? NullableField(string name) =>
            _type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_window);

        public void SetField(string name, object value) =>
            _type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_window, value);

        public void SetProperty(string name, object value) =>
            _type.GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_window, value);

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
