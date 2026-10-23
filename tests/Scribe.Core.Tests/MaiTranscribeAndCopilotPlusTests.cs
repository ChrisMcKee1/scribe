using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Cleanup;
using Scribe.Core.Diagnostics;
using Scribe.Core.Infrastructure;
using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.Settings;
using Scribe.Core.Tests.Libraries.Integration;
using Scribe.Core.Transcription;
using Scribe.Core.Vocabulary;

namespace Scribe.Core.Tests;

public sealed class MaiTranscribeAndCopilotPlusTests
{
    [Theory]
    [InlineData("phi-4-mini-instruct-qnn-npu:1", FoundryModelExecutionBuild.Npu, "phi-4-mini-instruct-generic-cpu:2")]
    [InlineData("phi-4-mini-instruct-openvino-npu:1", FoundryModelExecutionBuild.Npu, "phi-4-mini-instruct-generic-cpu:2")]
    [InlineData("qwen2.5-1.5b-instruct-vitis-npu:1", FoundryModelExecutionBuild.Npu, "qwen2.5-1.5b-instruct-generic-cpu:4")]
    [InlineData("phi-4-npu", FoundryModelExecutionBuild.Npu, "phi-4-cpu")]
    public void Copilot_plus_npu_variants_classify_as_npu_and_demote_to_cpu_counterparts(
        string npuAlias,
        FoundryModelExecutionBuild expectedBuild,
        string expectedCpuAlias)
    {
        string[] catalog =
        [
            "phi-4-mini-instruct-qnn-npu:1",
            "phi-4-mini-instruct-openvino-npu:1",
            "phi-4-mini-instruct-generic-cpu:2",
            "qwen2.5-1.5b-instruct-vitis-npu:1",
            "qwen2.5-1.5b-instruct-generic-cpu:4",
            "phi-4-npu",
            "phi-4-cpu",
            "phi-generic-cpu",
        ];

        Assert.Equal(expectedBuild, FoundryModelVariant.Classify(npuAlias));
        Assert.True(FoundryModelVariant.IsNpuAlias(npuAlias));
        Assert.True(FoundryModelVariant.IsAcceleratedAlias(npuAlias));
        Assert.False(FoundryModelVariant.IsGpuAlias(npuAlias));
        Assert.Equal(expectedCpuAlias, FoundryModelVariant.ResolveCpuCounterpartAlias(npuAlias, catalog));
    }

    [Theory]
    [InlineData("Qualcomm(R) Hexagon(TM) NPU", AcceleratorVendor.Qualcomm, CleanupModelCatalog.CopilotPlusNpuAlias)]
    [InlineData("Intel(R) AI Boost", AcceleratorVendor.Intel, CleanupModelCatalog.CopilotPlusNpuAlias)]
    [InlineData("AMD XDNA accelerator", AcceleratorVendor.Amd, CleanupModelCatalog.CopilotPlusNpuAlias)]
    [InlineData("Some Unlabelled Accelerator", AcceleratorVendor.Unknown, CleanupModelCatalog.DefaultAlias)]
    public void First_setup_selects_phi_4_mini_on_a_copilot_plus_npu_without_rtx_gpu(
        string npuName,
        AcceleratorVendor vendor,
        string expectedAlias)
    {
        var adapters = new[] { new GraphicsAdapter("Qualcomm(R) Adreno(TM) X1-85 GPU", 0) };
        var accelerators = new[] { new NeuralAccelerator(npuName, vendor) };

        Assert.Equal(expectedAlias, CleanupModelCatalog.DefaultAliasFor(adapters, accelerators));
    }

    [Fact]
    public void Rtx_gpu_with_8gb_still_wins_over_npu_for_default_foundry_local_model()
    {
        var adapters = new[] { new GraphicsAdapter("NVIDIA GeForce RTX 4070 Laptop GPU", 8L * 1024 * 1024 * 1024) };
        var accelerators = new[] { new NeuralAccelerator("Intel(R) AI Boost", AcceleratorVendor.Intel) };

        Assert.Equal(CleanupModelCatalog.LargeGpuAlias, CleanupModelCatalog.DefaultAliasFor(adapters, accelerators));
    }

    [Fact]
    public void Compute_capability_report_identifies_native_copilot_plus_npu()
    {
        var nativeArmNpu = ComputeCapabilityReport.Create(
            Architecture.Arm64,
            Architecture.Arm64,
            [new NeuralAccelerator("Qualcomm(R) Hexagon(TM) NPU", AcceleratorVendor.Qualcomm)]);
        var emulatedArmNpu = ComputeCapabilityReport.Create(
            Architecture.X64,
            Architecture.Arm64,
            [new NeuralAccelerator("Qualcomm(R) Hexagon(TM) NPU", AcceleratorVendor.Qualcomm)]);

        Assert.True(nativeArmNpu.IsCopilotPlusNpu);
        Assert.False(emulatedArmNpu.IsCopilotPlusNpu);
    }

    [Theory]
    [InlineData(false, null, true)]
    [InlineData(true, null, false)]
    [InlineData(true, "MaiTranscribeSpeech", true)]
    public void Installer_feature_gate_restricts_mai_transcribe_in_store_packages_unless_flagged(
        bool isPackaged,
        string? flagText,
        bool expected)
    {
        var flags = PerfFlags.Parse(flagText);
        Assert.Equal(expected, InstallerFeatureGate.CanUseMaiTranscribe(isPackaged, flags));
    }

    [Fact]
    public void Experimental_copilot_plus_npu_speech_is_blocked_on_store_packages_even_with_flag()
    {
        var npuReport = ComputeCapabilityReport.Create(
            Architecture.Arm64,
            Architecture.Arm64,
            [new NeuralAccelerator("Qualcomm(R) Hexagon(TM) NPU", AcceleratorVendor.Qualcomm)]);
        var flags = PerfFlags.Parse(PerfFlags.CopilotPlusNpuSpeech);

        Assert.False(InstallerFeatureGate.CanUseExperimentalCopilotPlusNpuSpeech(isPackaged: true, flags, npuReport));
        Assert.True(InstallerFeatureGate.CanUseExperimentalCopilotPlusNpuSpeech(isPackaged: false, flags, npuReport));
        Assert.False(InstallerFeatureGate.CanUseExperimentalCopilotPlusNpuSpeech(isPackaged: false, PerfFlags.None, npuReport));
    }

    [Theory]
    [InlineData(
        "https://my-foundry.services.ai.azure.com/api/projects/proj1",
        null,
        "https://my-foundry.services.ai.azure.com/speechtotext/transcriptions:transcribe?api-version=2025-10-15")]
    [InlineData(
        "https://my-aoai.openai.azure.com/",
        null,
        "https://my-aoai.cognitiveservices.azure.com/speechtotext/transcriptions:transcribe?api-version=2025-10-15")]
    [InlineData(
        "https://my-aoai.openai.azure.com/",
        "https://override-speech.cognitiveservices.azure.com",
        "https://override-speech.cognitiveservices.azure.com/speechtotext/transcriptions:transcribe?api-version=2025-10-15")]
    [InlineData(
        null,
        "http://127.0.0.1:18080/speechtotext/transcriptions:transcribe?api-version=2025-10-15",
        "http://127.0.0.1:18080/speechtotext/transcriptions:transcribe?api-version=2025-10-15")]
    public void Mai_transcribe_endpoint_resolver_normalizes_foundry_and_speech_urls(
        string? foundryEndpoint,
        string? overrideEndpoint,
        string expectedUri)
    {
        Assert.True(MaiTranscribeEndpointResolver.TryResolve(overrideEndpoint, foundryEndpoint, out var uri, out var error));
        Assert.Null(error);
        Assert.Equal(expectedUri, uri!.AbsoluteUri);
    }

    [Fact]
    public void Mai_transcribe_endpoint_resolver_rejects_remote_http()
    {
        Assert.False(MaiTranscribeEndpointResolver.TryResolve(
            "http://remote.example.com",
            null,
            out var uri,
            out var error));
        Assert.Null(uri);
        Assert.Equal("endpoint-scheme", error);
    }

    [Fact]
    public void Mai_wav_encoder_writes_valid_riff_pcm16_mono_header_and_samples()
    {
        float[] samples = [0f, 0.5f, -0.5f, 1.0f, -1.0f];
        var wav = MaiWavEncoder.EncodePcm16Wav(samples, 16_000);

        Assert.Equal(44 + samples.Length * 2, wav.Length);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal("fmt ", Encoding.ASCII.GetString(wav, 12, 4));
        Assert.Equal("data", Encoding.ASCII.GetString(wav, 36, 4));
        Assert.Equal(16_000, BitConverter.ToInt32(wav, 24));
        Assert.Equal(short.MaxValue, BitConverter.ToInt16(wav, 44 + 6));
    }

    [Fact]
    public async Task Mai_transcribe_client_sends_multipart_audio_and_definition_and_parses_combined_phrases()
    {
        string? capturedDefinition = null;
        string? capturedKeyHeader = null;
        int capturedAudioBytes = 0;

        using var handler = new DelegateHttpMessageHandler(async (request, ct) =>
        {
            capturedKeyHeader = request.Headers.TryGetValues("Ocp-Apim-Subscription-Key", out var values)
                ? values.Single()
                : null;
            var multipart = Assert.IsType<MultipartFormDataContent>(request.Content);
            foreach (var part in multipart)
            {
                var name = part.Headers.ContentDisposition?.Name?.Trim('"');
                if (name == "definition")
                {
                    capturedDefinition = await part.ReadAsStringAsync(ct);
                }
                else if (name == "audio")
                {
                    var bytes = await part.ReadAsByteArrayAsync(ct);
                    capturedAudioBytes = bytes.Length;
                }
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "combinedPhrases": [
                        { "channel": 0, "text": "MAI-Transcribe-2 recognized this clearly." }
                      ],
                      "durationMilliseconds": 1000
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"),
            };
        });

        using var client = new MaiTranscribeClient(NullLogger<MaiTranscribeClient>.Instance, handler: handler);
        var audio = new CapturedAudio(new float[1600], 16_000);
        var options = new MaiTranscribeOptions(
            Enabled: true,
            MaiEndpointOverride: "https://speech.cognitiveservices.azure.com",
            FoundryEndpoint: null,
            ApiKey: "test-speech-key",
            Style: MaiTranscribeStyle.Clean,
            PhraseList: ["Scribe", "Parakeet TDT"]);

        var result = await client.TryTranscribeAsync(audio, options);

        Assert.True(result.Succeeded);
        Assert.Equal("MAI-Transcribe-2 recognized this clearly.", result.Text);
        Assert.Equal("test-speech-key", capturedKeyHeader);
        Assert.Equal(44 + 1600 * 2, capturedAudioBytes);
        Assert.NotNull(capturedDefinition);

        using var doc = JsonDocument.Parse(capturedDefinition!);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("enhancedMode").GetProperty("enabled").GetBoolean());
        Assert.Equal("MAI-Transcribe-2", root.GetProperty("enhancedMode").GetProperty("model").GetString());
        Assert.Equal("clean", root.GetProperty("enhancedMode").GetProperty("modelOptions").GetProperty("transcribeStyle").GetString());
        var phrases = root.GetProperty("phraseList").GetProperty("phrases").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(["Scribe", "Parakeet TDT"], phrases);
    }

    [Fact]
    public async Task Mai_transcribe_client_automatically_falls_back_to_fast_transcription_when_region_lacks_enhanced_mode()
    {
        var definitions = new List<string>();
        using var handler = new DelegateHttpMessageHandler(async (request, ct) =>
        {
            var multipart = Assert.IsType<MultipartFormDataContent>(request.Content);
            var definitionPart = multipart.Single(p => p.Headers.ContentDisposition?.Name?.Trim('"') == "definition");
            var defJson = await definitionPart.ReadAsStringAsync(ct);
            definitions.Add(defJson);

            if (defJson.Contains("enhancedMode", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(
                        """{"code":"InvalidRequest","message":"Enhanced mode with model is currently not supported yet."}""",
                        Encoding.UTF8,
                        "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"combinedPhrases":[{"text":"Recognized via regional fast transcription fallback."}]}""",
                    Encoding.UTF8,
                    "application/json"),
            };
        });

        using var client = new MaiTranscribeClient(NullLogger<MaiTranscribeClient>.Instance, handler: handler);
        var audio = new CapturedAudio(new float[1600], 16_000);
        var options = new MaiTranscribeOptions(
            Enabled: true,
            MaiEndpointOverride: "https://foundry-cmckee.cognitiveservices.azure.com",
            FoundryEndpoint: null,
            ApiKey: "test-key",
            PhraseList: ["Azure DevOps", "Kubernetes"]);

        var first = await client.TryTranscribeAsync(audio, options);
        Assert.True(first.Succeeded);
        Assert.Equal("Recognized via regional fast transcription fallback.", first.Text);
        Assert.Equal(2, definitions.Count);
        Assert.Contains("enhancedMode", definitions[0], StringComparison.Ordinal);
        Assert.DoesNotContain("enhancedMode", definitions[1], StringComparison.Ordinal);
        Assert.Contains("Kubernetes", definitions[1], StringComparison.Ordinal);

        // Subsequent call to the same host skips enhancedMode directly (1 request).
        var second = await client.TryTranscribeAsync(audio, options);
        Assert.True(second.Succeeded);
        Assert.Equal(3, definitions.Count);
        Assert.DoesNotContain("enhancedMode", definitions[2], StringComparison.Ordinal);
    }

    [Fact]
    public void Transcription_service_uses_mai_transcribe_when_enabled_and_falls_back_to_local_recognizer_on_failure()
    {
        var shouldSucceed = true;
        var fakeMai = new StubMaiClient(() => shouldSucceed
            ? MaiTranscribeAttemptResult.Success("Cloud transcript from MAI-Transcribe-2.", TimeSpan.FromMilliseconds(120))
            : MaiTranscribeAttemptResult.Fallback("http-503", TimeSpan.FromMilliseconds(45)));

        var localCalls = 0;
        using var service = new TranscriptionService(
            () => new StubSpeechRecognizer(() =>
            {
                localCalls++;
                return "Offline Parakeet fallback transcript.";
            }),
            NullLogger<TranscriptionService>.Instance,
            fakeMai,
            () => new MaiTranscribeOptions(
                Enabled: true,
                MaiEndpointOverride: "https://speech.cognitiveservices.azure.com",
                FoundryEndpoint: null,
                ApiKey: "key"));

        var audio = new CapturedAudio([0.1f, -0.1f, 0.2f], 16_000);

        var cloudResult = service.Transcribe(audio);
        Assert.Equal("Cloud transcript from MAI-Transcribe-2.", cloudResult.Text);
        Assert.Equal(TranscriptionModelCatalog.MaiTranscribe2Id, cloudResult.ModelId);
        Assert.Equal(0, localCalls);

        shouldSucceed = false;
        var fallbackResult = service.Transcribe(audio);
        Assert.Equal("Offline Parakeet fallback transcript.", fallbackResult.Text);
        Assert.Equal(TranscriptionModelCatalog.DefaultId, fallbackResult.ModelId);
        Assert.Equal(1, localCalls);
    }

    [Fact]
    public void Speech_model_setup_status_describes_azure_cli_service_principal_and_api_key_paths()
    {
        var offline = TranscriptionModelChoices.DescribeSetupStatus(
            TranscriptionModelCatalog.DefaultId,
            maiEndpointOverride: null,
            foundryEndpoint: null,
            AzureAuthMode.AzureCli,
            apiKey: null,
            tenantId: null,
            clientId: null,
            clientSecret: null);
        Assert.False(offline.IsMaiSelected);
        Assert.True(offline.IsReady);
        Assert.Contains("Parakeet", offline.CurrentModelLine, StringComparison.Ordinal);

        var missingEndpoint = TranscriptionModelChoices.DescribeSetupStatus(
            TranscriptionModelCatalog.MaiTranscribe2Id,
            maiEndpointOverride: null,
            foundryEndpoint: null,
            AzureAuthMode.AzureCli,
            apiKey: null,
            tenantId: null,
            clientId: null,
            clientSecret: null);
        Assert.True(missingEndpoint.IsMaiSelected);
        Assert.False(missingEndpoint.IsReady);
        Assert.Contains("Setup needed", missingEndpoint.AuthStatusLine, StringComparison.Ordinal);

        var cliReady = TranscriptionModelChoices.DescribeSetupStatus(
            TranscriptionModelCatalog.MaiTranscribe2Id,
            maiEndpointOverride: null,
            foundryEndpoint: "https://my-foundry.services.ai.azure.com/",
            AzureAuthMode.AzureCli,
            apiKey: null,
            tenantId: null,
            clientId: null,
            clientSecret: null,
            azureCliSignedIn: true);
        Assert.True(cliReady.IsReady);
        Assert.Contains("Azure CLI sign-in", cliReady.AuthStatusLine, StringComparison.Ordinal);

        var spIncomplete = TranscriptionModelChoices.DescribeSetupStatus(
            TranscriptionModelCatalog.MaiTranscribe2Id,
            maiEndpointOverride: "https://my-speech.cognitiveservices.azure.com/",
            foundryEndpoint: null,
            AzureAuthMode.ServicePrincipal,
            apiKey: null,
            tenantId: "11111111-1111-1111-1111-111111111111",
            clientId: null,
            clientSecret: null);
        Assert.False(spIncomplete.IsReady);
        Assert.Contains("Application (client) ID", spIncomplete.AuthStatusLine, StringComparison.Ordinal);

        var spReady = TranscriptionModelChoices.DescribeSetupStatus(
            TranscriptionModelCatalog.MaiTranscribe2Id,
            maiEndpointOverride: "https://my-speech.cognitiveservices.azure.com/",
            foundryEndpoint: null,
            AzureAuthMode.ServicePrincipal,
            apiKey: null,
            tenantId: "11111111-1111-1111-1111-111111111111",
            clientId: "22222222-2222-2222-2222-222222222222",
            clientSecret: "secret-value");
        Assert.True(spReady.IsReady);
        Assert.Contains("app registration", spReady.AuthStatusLine, StringComparison.Ordinal);

        var keyReady = TranscriptionModelChoices.DescribeSetupStatus(
            TranscriptionModelCatalog.MaiTranscribe2Id,
            maiEndpointOverride: null,
            foundryEndpoint: "https://my-foundry.services.ai.azure.com/",
            AzureAuthMode.AzureCli,
            apiKey: "my-api-key",
            tenantId: null,
            clientId: null,
            clientSecret: null,
            azureCliSignedIn: false);
        Assert.True(keyReady.IsReady);
        Assert.Contains("saved API key", keyReady.AuthStatusLine, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_window_wires_dictation_and_advanced_mai_transcribe_setup_flows() =>
        PrivateDesktopTest.Run(typeof(MaiTranscribeAndCopilotPlusTests), () =>
        {
            using var rig = new MaiSettingsWindowRig();
            rig.Open();

            // Step 1: Dictation page shows Speech model summary and navigation buttons
            var statusText = rig.Control<TextBlock>("DictationSpeechModelStatusText");
            var guidanceText = rig.Control<TextBlock>("DictationSpeechModelGuidanceText");
            Assert.Contains("Parakeet", statusText.Text, StringComparison.Ordinal);
            Assert.Contains("MAI-Transcribe-2", guidanceText.Text, StringComparison.Ordinal);
            rig.CaptureScreenshotIfRequested("mai-step1-dictation.png");

            // Navigate from Dictation page to AI cleanup > Microsoft Foundry (Azure CLI)
            rig.Invoke("DictationMaiAuthLink_Click", rig.Control<Button>("DictationMaiAuthButton"), new RoutedEventArgs());
            Assert.Equal(Visibility.Visible, rig.Control<FrameworkElement>("SectionAi").Visibility);
            rig.Control<CheckBox>("AiCleanupCheck").IsChecked = true;
            rig.Control<RadioButton>("AiProviderFoundryRadio").IsChecked = true;
            rig.Control<RadioButton>("AzureCliRadio").IsChecked = true;
            rig.Invoke("AzureManualToggleButton_Click", rig.Control<Button>("AzureManualToggleButton"), new RoutedEventArgs());
            rig.Control<TextBox>("AzureEndpointBox").Text = "https://contoso-foundry.services.ai.azure.com/";
            rig.Control<TextBox>("AzureDeploymentBox").Text = "gpt-5.6-sol";
            rig.Control<TextBox>("AzureEndpointBox").BringIntoView();
            rig.CaptureScreenshotIfRequested("mai-step2-ai-cleanup-cli.png");

            // Switch to Service Principal on AI cleanup > Microsoft Foundry
            rig.Control<RadioButton>("AzureServicePrincipalRadio").IsChecked = true;
            rig.Control<TextBox>("SpTenantBox").Text = "11111111-2222-3333-4444-555555555555";
            rig.Control<TextBox>("SpClientIdBox").Text = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
            rig.Control<TextBox>("AzureEndpointBox").BringIntoView();
            rig.CaptureScreenshotIfRequested("mai-step2-ai-cleanup-sp.png");

            // Switch back to Azure CLI and navigate to Advanced > Speech model
            rig.Control<RadioButton>("AzureCliRadio").IsChecked = true;
            rig.Invoke("ShowPage", SettingsPage.Advanced, Type.Missing);
            var combo = rig.Control<ComboBox>("TranscriptionModelCombo");
            var maiChoice = combo.Items.Cast<TranscriptionModelChoice>()
                .Single(c => c.Id == TranscriptionModelCatalog.MaiTranscribe2Id);
            combo.SelectedItem = maiChoice;

            var maiPanel = rig.Control<FrameworkElement>("MaiTranscribePanel");
            Assert.Equal(Visibility.Visible, maiPanel.Visibility);
            Assert.Contains(
                "MAI-Transcribe-2 automatically",
                rig.Control<TextBlock>("MaiTranscribeModelNameNoteText").Text,
                StringComparison.Ordinal);

            var endpointBox = rig.Control<TextBox>("MaiTranscribeEndpointBox");
            endpointBox.Text = "https://contoso-speech.cognitiveservices.azure.com";
            var styleCombo = rig.Control<ComboBox>("MaiTranscribeStyleCombo");
            Assert.Equal(2, styleCombo.Items.Count);
            rig.CaptureScreenshotIfRequested("mai-step3-advanced-model.png");

            foreach (var scale in new[] { 1.0, 2.25 })
            {
                rig.SetTextScale(scale);
                Assert.Equal(Visibility.Visible, maiPanel.Visibility);
            }
        });

    private sealed class MaiSettingsWindowRig : IDisposable
    {
        private readonly TempDirectory _temp = new();
        private readonly Application _app;
        private readonly string _appBin;
        private readonly string _repoRoot;
        private readonly Type _type;
        private Window? _window;
        private Exception? _dispatcherFailure;

        public ServiceProvider Services { get; }

        public MaiSettingsWindowRig()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "Scribe.slnx")))
            {
                root = root.Parent;
            }

            Assert.NotNull(root);
            _repoRoot = root.FullName;
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
            services.AddSingleton(PerfFlags.Parse(PerfFlags.MaiTranscribeSpeech));
            services.AddSingleton(assembly.GetType("Scribe.App.Infrastructure.AzureCliInstaller", throwOnError: true)!);
            services.AddSingleton<StartupRegistration>();
            Services = services.BuildServiceProvider();
            var settings = Services.GetRequiredService<ISettingsRepository>();
            settings.Save(AppSettings.CreateDefault());
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
            _window.Width = 1040;
            _window.Height = 760;
            _window.ShowInTaskbar = false;
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

        public void Invoke(string name, params object[] args) =>
            _type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_window, args);

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
            PrivateDesktopTest.RenderCheckpoint("MAI setup choices at text scale", _window!.UpdateLayout);
        }

        public void CaptureScreenshotIfRequested(string fileName)
        {
            if (Environment.GetEnvironmentVariable("SCRIBE_CAPTURE_MAI_SCREENSHOTS") != "1" ||
                _window?.Content is not FrameworkElement rootContent)
            {
                return;
            }

            _window.UpdateLayout();
            var width = (int)Math.Max(1000, rootContent.ActualWidth > 0 ? rootContent.ActualWidth : 1020);
            var height = (int)Math.Max(700, rootContent.ActualHeight > 0 ? rootContent.ActualHeight : 720);
            rootContent.Measure(new Size(width, height));
            rootContent.Arrange(new Rect(0, 0, width, height));
            rootContent.UpdateLayout();

            var drawingVisual = new DrawingVisual();
            using (var context = drawingVisual.RenderOpen())
            {
                context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)), null, new Rect(0, 0, width, height));
                context.DrawRectangle(new VisualBrush(rootContent) { Stretch = Stretch.None }, null, new Rect(0, 0, width, height));
            }

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(drawingVisual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var dir = Path.Combine(_repoRoot, "docs", "screenshots");
            Directory.CreateDirectory(dir);
            using var stream = File.Create(Path.Combine(dir, fileName));
            encoder.Save(stream);
        }

        private static void PumpUntil(Func<bool> ready)
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

    private sealed class DelegateHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request, cancellationToken);
    }

    private sealed class StubMaiClient(Func<MaiTranscribeAttemptResult> handler) : IMaiTranscribeClient
    {
        public Task<MaiTranscribeAttemptResult> TryTranscribeAsync(
            CapturedAudio audio,
            MaiTranscribeOptions options,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(handler());
    }

    private sealed class StubSpeechRecognizer(Func<string> decode) : ISpeechRecognizer
    {
        public string ModelId => TranscriptionModelCatalog.DefaultId;
        public string Decode(float[] samples, int sampleRate) => decode();
        public void Dispose() { }
    }
}
