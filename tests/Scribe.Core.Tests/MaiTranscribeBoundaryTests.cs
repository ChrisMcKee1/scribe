using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Azure.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Cleanup;
using Scribe.Core.Diagnostics;
using Scribe.Core.Infrastructure;
using Scribe.Core.Libraries;
using Scribe.Core.Models;
using Scribe.Core.Persistence;
using Scribe.Core.PostProcessing;
using Scribe.Core.Settings;
using Scribe.Core.Tests.Concurrency;
using Scribe.Core.Tests.Vocabulary;
using Scribe.Core.Transcription;
using Scribe.Core.Vocabulary;
using static Scribe.Core.Tests.Vocabulary.TestVocabularies;

namespace Scribe.Core.Tests;

/// <summary>
/// The boundary of MAI-Transcribe-2 cloud speech: what may leave this PC, and what may stop a dictation. The user's words
/// travel only while the AI permission they were admitted with still holds, at each send; the audio, the phrases and the key
/// header go only to the configured address, because no redirect is followed; one deadline bounds the whole attempt, the
/// sign-in included, and the caller's cancellation reaches it; an answer that is not a transcript response is a failure to
/// decode on this PC and never an empty dictation; and the speech model on this PC never waits on a read that only cloud
/// speech needs. Every case runs over fakes (a recording network, a scripted sign-in, a loopback server): nothing leaves
/// the process.
/// </summary>
public sealed class MaiTranscribeBoundaryTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private const string Endpoint = "https://speech.cognitiveservices.azure.com";
    private const string Recognized = """{"combinedPhrases":[{"channel":0,"text":"Recognized by the cloud."}]}""";
    private const string EnhancedModeRefused =
        """{"code":"InvalidRequest","message":"Enhanced mode with model is currently not supported yet."}""";

    // Letters and spaces only, so JSON escaping in a request can never hide it.
    private const string TeamSpoken = "zeb ra quill";
    private const string TeamCanary = "Zebraquill";

    private static CapturedAudio Audio => new(new float[1600], 16_000);

    // --- F-09: the permission the words were admitted with is judged at every send -----------------------------------

    [Fact]
    public async Task Library_words_travel_while_the_permission_they_were_admitted_with_holds()
    {
        var admitted = Of(1, new Library("team", H1, true, Entry(TeamSpoken, TeamCanary)));
        var source = new TestVocabularySource(admitted);
        var bodies = new ConcurrentQueue<string>();
        using var network = new RecordingNetwork(async (request, ct) =>
        {
            bodies.Enqueue(await request.Content!.ReadAsStringAsync(ct));
            return JsonOk(Recognized);
        });
        using var client = ClientOver(network);

        var result = await client.TryTranscribeAsync(Audio, LibraryOptions(admitted, source, apiKey: "test-key"));

        Assert.True(result.Succeeded);
        Assert.Equal("Recognized by the cloud.", result.Text);
        Assert.Contains(TeamCanary, Assert.Single(bodies), StringComparison.Ordinal);
        var handOff = Assert.Single(source.HandOffs);
        Assert.True(handOff.HandedOver);
        Assert.Same(admitted.AiScope, handOff.Admitted);
    }

    [Fact]
    public async Task A_permission_withdrawn_while_credentials_are_acquired_sends_nothing_and_leaves_the_dictation_to_this_pc()
    {
        var admitted = Of(1, new Library("team", H1, true, Entry(TeamSpoken, TeamCanary)));
        var source = new TestVocabularySource(admitted);
        using var network = new RecordingNetwork((_, _) => Task.FromResult(JsonOk(Recognized)));
        using var client = ClientOver(network, SignedIn((_, _) =>
        {
            // The words were copied into the request before the sign-in began; the permission goes while it is acquired.
            source.Publish(Of(2, new Library("team", H1, false, Entry(TeamSpoken, TeamCanary))));
            return ValueTask.FromResult(Token());
        }));

        var result = await client.TryTranscribeAsync(Audio, LibraryOptions(admitted, source, apiKey: null));

        Assert.False(result.Succeeded);
        Assert.Equal("library-scope", result.DiagnosticReason);
        Assert.Equal(0, network.Requests);
        var handOff = Assert.Single(source.HandOffs);
        Assert.False(handOff.HandedOver);
        Assert.Same(admitted.AiScope, handOff.Admitted);
    }

    [Fact]
    public async Task A_permission_withdrawn_before_the_retry_without_enhanced_mode_holds_the_retry_back()
    {
        var admitted = Of(1, new Library("team", H1, true, Entry(TeamSpoken, TeamCanary)));
        var source = new TestVocabularySource(admitted);
        var bodies = new ConcurrentQueue<string>();
        using var network = new RecordingNetwork(async (request, ct) =>
        {
            bodies.Enqueue(await request.Content!.ReadAsStringAsync(ct));

            // This region does not offer enhanced mode, which buys the retry, and the permission goes before it is made.
            source.Publish(Of(2, new Library("team", H1, false, Entry(TeamSpoken, TeamCanary))));
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(EnhancedModeRefused, Encoding.UTF8, "application/json"),
            };
        });
        using var client = ClientOver(network);

        var result = await client.TryTranscribeAsync(Audio, LibraryOptions(admitted, source, apiKey: "test-key"));

        Assert.False(result.Succeeded);
        Assert.Equal("library-scope", result.DiagnosticReason);

        // The first send went under the permission it was admitted with, words and all; the retry never left.
        Assert.Contains(TeamCanary, Assert.Single(bodies), StringComparison.Ordinal);
        Assert.Equal(1, network.Requests);
        IEnumerable<bool> handedOver = source.HandOffs.Select(handOff => handOff.HandedOver);
        Assert.Equal([true, false], handedOver);
    }

    // --- F-09, F-10: the words and the scope come from what the recording was admitted with --------------------------

    [Fact]
    public void The_words_and_their_scope_come_from_the_admitted_vocabulary_never_from_a_fresh_read()
    {
        var admittedLibraries = Of(1, new Library("team", H1, true, Entry(TeamSpoken, TeamCanary)));
        var liveLibraries = Of(2, new Library("other", H2, true, Entry("oth er", "Othercanary")));
        var source = new TestVocabularySource(liveLibraries);
        var admission = new TranscriptionAdmission(
            SettingsFor(TranscriptionModelCatalog.MaiTranscribe2Id),
            GenerationOf(admittedLibraries, Entry("harbour lantern", "Harbour Lantern")));

        var options = MaiTranscribeOptions.FromAdmission(
            admission,
            isPackaged: false,
            perfFlags: null,
            librarySource: () => source);

        Assert.True(options.Enabled);
        Assert.Equal(new[] { "Harbour Lantern", TeamCanary }, options.PhraseList);
        var phraseAdmission = Assert.IsType<MaiPhraseAdmission>(options.PhraseAdmission);
        Assert.Same(admittedLibraries.AiScope, phraseAdmission.Scope);
        Assert.Same(source, phraseAdmission.Source);
        Assert.Equal(0, source.CurrentReads);
    }

    // --- F-10: nothing is read for cloud speech until it is chosen, and nothing is read from the stored state --------------

    [Fact]
    public void A_dictation_that_stays_on_this_pc_looks_up_nothing_for_cloud_speech()
    {
        var admission = new TranscriptionAdmission(
            SettingsFor(TranscriptionModelCatalog.DefaultId),
            GenerationOf(Of(1, new Library("team", H1, true, Entry(TeamSpoken, TeamCanary)))));

        var options = MaiTranscribeOptions.FromAdmission(
            admission,
            isPackaged: false,
            perfFlags: null,
            librarySource: Unread<ILibraryVocabularySource?>("library source"));

        Assert.False(options.Enabled);
    }

    [Fact]
    public void The_admitted_settings_alone_decide_whether_the_audio_may_leave_this_pc()
    {
        var generation = GenerationOf(Of(1));

        var stays = MaiTranscribeOptions.FromAdmission(
            new TranscriptionAdmission(SettingsFor(TranscriptionModelCatalog.DefaultId), generation),
            isPackaged: false,
            perfFlags: null,
            librarySource: () => new TestVocabularySource(Of(1)));
        Assert.False(stays.Enabled);

        var goes = MaiTranscribeOptions.FromAdmission(
            new TranscriptionAdmission(SettingsFor(TranscriptionModelCatalog.MaiTranscribe2Id), generation),
            isPackaged: false,
            perfFlags: null,
            librarySource: () => new TestVocabularySource(Of(1)));
        Assert.True(goes.Enabled);
        Assert.Equal(Endpoint, goes.MaiEndpointOverride);
        Assert.Equal("test-key", goes.ApiKey);
    }

    [Fact]
    public void With_post_processing_off_no_words_are_sent_and_nothing_is_looked_up_for_them()
    {
        var admission = new TranscriptionAdmission(
            SettingsFor(TranscriptionModelCatalog.MaiTranscribe2Id, postProcessing: false),
            GenerationOf(Of(1, new Library("team", H1, true, Entry(TeamSpoken, TeamCanary)))));

        var options = MaiTranscribeOptions.FromAdmission(
            admission,
            isPackaged: false,
            perfFlags: null,
            librarySource: Unread<ILibraryVocabularySource?>("library source"));

        Assert.True(options.Enabled);
        Assert.Empty(options.PhraseList!);
        Assert.Null(options.PhraseAdmission);
    }

    [Fact]
    public void Library_words_with_no_source_to_judge_them_at_the_send_are_left_out()
    {
        var admission = new TranscriptionAdmission(
            SettingsFor(TranscriptionModelCatalog.MaiTranscribe2Id),
            GenerationOf(Of(1, new Library("team", H1, true, Entry(TeamSpoken, TeamCanary))), Entry("harbour lantern", "Harbour Lantern")));

        var options = MaiTranscribeOptions.FromAdmission(
            admission,
            isPackaged: false,
            perfFlags: null,
            librarySource: () => null);

        Assert.Equal(new[] { "Harbour Lantern" }, options.PhraseList);
        Assert.Null(options.PhraseAdmission);
    }

    [Fact]
    public void The_shared_factory_leaves_library_words_out_when_given_no_source_to_judge_them_and_keeps_the_personal_ones()
    {
        var libraries = Of(1, new Library("team", H1, true, Entry(TeamSpoken, TeamCanary)));

        // Libraries and no source: the optional arguments a caller of the public factory may leave out.
        var options = MaiTranscribeOptions.FromSettings(
            SettingsFor(TranscriptionModelCatalog.MaiTranscribe2Id),
            isPackaged: false,
            personalDictionary: new[] { Entry("harbour lantern", "Harbour Lantern") },
            libraries: libraries);

        Assert.True(options.Enabled);
        Assert.Equal(new[] { "Harbour Lantern" }, options.PhraseList);
        Assert.Null(options.PhraseAdmission);
    }

    [Fact]
    public void The_shared_factory_carries_library_words_only_together_with_the_source_that_judges_them()
    {
        var libraries = Of(1, new Library("team", H1, true, Entry(TeamSpoken, TeamCanary)));
        var source = new TestVocabularySource(libraries);

        var options = MaiTranscribeOptions.FromSettings(
            SettingsFor(TranscriptionModelCatalog.MaiTranscribe2Id),
            isPackaged: false,
            personalDictionary: new[] { Entry("harbour lantern", "Harbour Lantern") },
            libraries: libraries,
            librarySource: source);

        Assert.Equal(new[] { "Harbour Lantern", TeamCanary }, options.PhraseList);
        var phraseAdmission = Assert.IsType<MaiPhraseAdmission>(options.PhraseAdmission);
        Assert.Same(libraries.AiScope, phraseAdmission.Scope);
        Assert.Same(source, phraseAdmission.Source);
        Assert.Equal(0, source.CurrentReads);
    }

    [Fact]
    public void A_store_package_stays_on_this_pc_unless_its_flag_is_set_though_the_settings_choose_cloud_speech()
    {
        var admission = new TranscriptionAdmission(SettingsFor(TranscriptionModelCatalog.MaiTranscribe2Id), GenerationOf(Of(1)));

        var withoutFlag = MaiTranscribeOptions.FromAdmission(
            admission,
            isPackaged: true,
            perfFlags: null,
            librarySource: Unread<ILibraryVocabularySource?>("library source"));
        Assert.False(withoutFlag.Enabled);

        var withFlag = MaiTranscribeOptions.FromAdmission(
            admission,
            isPackaged: true,
            perfFlags: PerfFlags.Parse(PerfFlags.MaiTranscribeSpeech),
            librarySource: () => new TestVocabularySource(Of(1)));
        Assert.True(withFlag.Enabled);
    }

    [Fact]
    public void A_failure_preparing_cloud_speech_never_stops_the_speech_model_on_this_pc()
    {
        var logger = new CapturingLogger<TranscriptionService>();
        var mai = new StubMai((_, _) => throw new InvalidOperationException("The cloud client must not be reached."));
        var localDecodes = 0;
        using var service = new TranscriptionService(
            () => new StubRecognizer(() =>
            {
                localDecodes++;
                return "Decoded on this PC.";
            }),
            logger,
            mai,
            _ => throw new IOException("The library vocabulary could not be looked up."));

        var result = service.Transcribe(Audio, AdmissionFor(Of(1)), CancellationToken.None);

        Assert.Equal("Decoded on this PC.", result.Text);
        Assert.Equal(TranscriptionModelCatalog.DefaultId, result.ModelId);
        Assert.Equal(1, localDecodes);
        Assert.Equal(0, mai.Calls);

        // Logged by its shape, never by the failure's own text.
        var warning = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains("IOException", warning.Message, StringComparison.Ordinal);
        Assert.False(warning.Mentions("could not be looked up"));
    }

    [Fact]
    public void A_cloud_client_that_throws_never_stops_the_speech_model_on_this_pc()
    {
        var mai = new StubMai((_, _) => throw new InvalidOperationException("The cloud client failed."));
        using var service = new TranscriptionService(
            () => new StubRecognizer(() => "Decoded on this PC."),
            NullLogger<TranscriptionService>.Instance,
            mai,
            _ => Options(apiKey: "test-key"));

        var result = service.Transcribe(Audio, AdmissionFor(Of(1)), CancellationToken.None);

        Assert.Equal("Decoded on this PC.", result.Text);
        Assert.Equal(1, mai.Calls);
    }

    [Fact]
    public void Only_the_callers_own_cancellation_leaves_the_cloud_attempt()
    {
        using var cancel = new CancellationTokenSource();
        var localDecodes = 0;
        var mai = new StubMai((_, token) =>
        {
            cancel.Cancel();
            throw new OperationCanceledException(token);
        });
        using var service = new TranscriptionService(
            () => new StubRecognizer(() =>
            {
                localDecodes++;
                return "Decoded on this PC.";
            }),
            NullLogger<TranscriptionService>.Instance,
            mai,
            _ => Options(apiKey: "test-key"));

        Assert.ThrowsAny<OperationCanceledException>(() => service.Transcribe(Audio, AdmissionFor(Of(1)), cancel.Token));
        Assert.Equal(0, localDecodes);
    }

    [Fact]
    public void Cloud_speech_is_reachable_only_with_the_admission_a_recording_was_admitted_with()
    {
        TranscriptionAdmission? seen = null;
        var decisions = 0;
        var mai = new StubMai((_, _) => MaiTranscribeAttemptResult.Fallback("http-503"));
        using var service = new TranscriptionService(
            () => new StubRecognizer(() => "Decoded on this PC."),
            NullLogger<TranscriptionService>.Instance,
            mai,
            admission =>
            {
                seen = admission;
                decisions++;
                return Options(apiKey: "test-key");
            });
        var admitted = AdmissionFor(Of(1));

        // With the admission, the decision is made from it and the cloud is asked.
        service.Transcribe(Audio, admitted, CancellationToken.None);
        Assert.Same(admitted, seen);
        Assert.Equal(1, decisions);
        Assert.Equal(1, mai.Calls);

        // Without one the capture is decoded on this PC and nothing is decided or asked.
        var local = service.Transcribe(Audio, CancellationToken.None);
        Assert.Equal("Decoded on this PC.", local.Text);
        Assert.Equal(1, decisions);
        Assert.Equal(1, mai.Calls);
    }

    // --- F-10: the registration decides from the admission and reads nothing it does not need ------------------------

    [Fact]
    public async Task The_registered_decision_follows_the_admission_not_the_stored_settings_and_reads_no_dictionary()
    {
        using var temp = new TempDirectory();
        var dictionary = new FailingDictionary();
        var libraries = Of(1, new Library("team", H1, true, Entry(TeamSpoken, TeamCanary)));
        var source = new TestVocabularySource(libraries);
        var optionsSeen = new ConcurrentQueue<MaiTranscribeOptions>();
        var mai = new StubMai((sent, _) =>
        {
            optionsSeen.Enqueue(sent);
            return MaiTranscribeAttemptResult.Success("Cloud text.", TimeSpan.FromMilliseconds(5));
        });

        await using var provider = ProviderWith(temp, mai, dictionary, source);

        // What is stored chooses the speech model on this PC; the recording was admitted for the cloud, and it goes
        // where it was admitted to go.
        Assert.Equal(TranscriptionModelCatalog.DefaultId, provider.GetRequiredService<ISettingsRepository>().Load().TranscriptionModelId);

        var transcription = provider.GetRequiredService<ITranscriptionService>();
        var admission = new TranscriptionAdmission(
            SettingsFor(TranscriptionModelCatalog.MaiTranscribe2Id),
            GenerationOf(libraries, Entry("harbour lantern", "Harbour Lantern")));

        var result = transcription.Transcribe(Audio, admission, CancellationToken.None);

        Assert.Equal("Cloud text.", result.Text);
        Assert.Equal(TranscriptionModelCatalog.MaiTranscribe2Id, result.ModelId);
        var options = Assert.Single(optionsSeen);
        Assert.Equal(new[] { "Harbour Lantern", TeamCanary }, options.PhraseList);
        var phraseAdmission = Assert.IsType<MaiPhraseAdmission>(options.PhraseAdmission);
        Assert.Same(libraries.AiScope, phraseAdmission.Scope);
        Assert.Same(source, phraseAdmission.Source);
        Assert.Equal(0, dictionary.Reads);
        Assert.Equal(0, source.CurrentReads);
    }

    [Fact]
    public void The_controller_hands_speech_recognition_the_admission_its_recording_was_admitted_with()
    {
        var controller = ReadSource("src", "Scribe.App", "Dictation", "DictationController.cs");

        // The one recognition call carries the admitted settings and the admitted vocabulary generation: a call without
        // them would read the stored settings and the dictionary afresh, after the recording was admitted.
        Assert.Single(Regex.Matches(controller, @"_transcription\s*\.\s*Transcribe\("));
        Assert.Matches(
            @"_transcription\s*\.\s*Transcribe\(\s*audio\s*,\s*new\s+TranscriptionAdmission\(\s*settings\s*,\s*session\.Vocabulary\s*\)\s*,\s*cancellationToken\s*\)",
            controller);
    }

    // --- F-22: no redirect is followed ------------------------------------------------------------------------------

    [Fact]
    public void The_transport_follows_no_redirect()
    {
        using var handler = Assert.IsType<HttpClientHandler>(MaiTranscribeClient.CreateNetworkHandler());

        Assert.False(handler.AllowAutoRedirect);
    }

    [Fact]
    public async Task A_redirect_is_never_followed_so_the_audio_and_the_key_stay_with_the_configured_address()
    {
        await using var elsewhere = new LoopbackServer(() => JsonResponse(Recognized));
        await using var configured = new LoopbackServer(() => Redirect(elsewhere.Origin + MaiTranscribeEndpointResolver.TranscribePath));
        using var client = new MaiTranscribeClient(NullLogger<MaiTranscribeClient>.Instance);

        var result = await client.TryTranscribeAsync(
            Audio,
            Options(apiKey: "test-key", endpoint: configured.Origin)).WaitAsync(Bound);

        // The redirect comes back as the answer, which is a failure: the dictation is decoded on this PC.
        Assert.False(result.Succeeded);
        Assert.Equal("http-307", result.DiagnosticReason);

        // The configured address saw the request, key header and all; the address it named never heard of it.
        var request = Assert.Single(configured.Requests);
        Assert.Contains("api-key: test-key", request, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(elsewhere.Requests);
    }

    // --- F-23: one deadline, and the caller's cancellation, across the credential and every send --------------------
    // The credential cases script the TokenCredential beneath the client's own sign-in step (SignedIn), so the step the app
    // ships runs above it: the scope it asks for, the token it hands down and the cancellation it passes on.

    [Fact]
    public async Task Sign_in_asks_the_credential_for_the_cognitive_services_scope_and_sends_the_token_it_is_given()
    {
        var identities = new ConcurrentQueue<AzureCredentialRequest>();
        var contexts = new ConcurrentQueue<TokenRequestContext>();
        string? authorization = null;
        using var network = new RecordingNetwork((request, _) =>
        {
            authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(JsonOk(Recognized));
        });
        using var client = ClientOver(network, identity =>
        {
            identities.Enqueue(identity);
            return new ScriptedCredential((context, _) =>
            {
                contexts.Enqueue(context);
                return ValueTask.FromResult(Token("scripted-token"));
            });
        });

        var result = await client.TryTranscribeAsync(
            Audio,
            Options(apiKey: null) with
            {
                AuthMode = AzureAuthMode.ServicePrincipal,
                TenantId = "tenant-id",
                SubscriptionId = "subscription-id",
                ClientId = "client-id",
                ClientSecret = "client-secret",
            });

        Assert.True(result.Succeeded);
        Assert.Equal("Bearer scripted-token", authorization);

        // The identity the options name is the one the credential is built for.
        Assert.Equal(
            new AzureCredentialRequest(AzureAuthMode.ServicePrincipal, "tenant-id", "subscription-id", "client-id", "client-secret"),
            Assert.Single(identities));

        // Pinned by its text, not by the constant that holds it: this is the audience the speech service authorizes.
        Assert.Equal(new[] { "https://cognitiveservices.azure.com/.default" }, Assert.Single(contexts).Scopes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_credential_wait_that_never_ends_is_cut_off_by_the_attempts_one_deadline_and_decodes_on_this_pc(
        bool credentialReportsItAsAnotherFailure)
    {
        var time = new ManualTimeProvider();
        var inCredential = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tokens = new ConcurrentQueue<CancellationToken>();
        using var network = new RecordingNetwork((_, _) => Task.FromResult(JsonOk(Recognized)));
        using var client = ClientOver(
            network,
            SignedIn(async (_, ct) =>
            {
                // A sign-in queued behind another one: only a cancellation ends it.
                tokens.Enqueue(ct);
                inCredential.SetResult();
                await WaitForCancellation(ct, credentialReportsItAsAnotherFailure);
                return Token("never");
            }),
            time);

        var attempt = client.TryTranscribeAsync(Audio, Options(apiKey: null));
        await inCredential.Task.WaitAsync(Bound);
        Assert.False(attempt.IsCompleted);

        // The deadline passes while the credential is still being acquired.
        time.Timers.Single().Fire();

        // The credential was handed the attempt's own token, so the deadline reaches the wait inside it.
        Assert.True(Assert.Single(tokens).IsCancellationRequested);
        var result = await attempt.WaitAsync(Bound);

        Assert.False(result.Succeeded);
        Assert.Equal("timeout", result.DiagnosticReason);
        Assert.Equal(0, network.Requests);
    }

    [Fact]
    public async Task The_deadline_covers_the_retry_too_so_a_stalled_second_send_is_cut_off()
    {
        var time = new ManualTimeProvider();
        var secondSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var network = new RecordingNetwork(async (request, ct) =>
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            if (body.Contains("enhancedMode", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(EnhancedModeRefused, Encoding.UTF8, "application/json"),
                };
            }

            // The retry without enhanced mode, which never answers.
            secondSend.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return JsonOk(Recognized);
        });
        using var client = ClientOver(network, time: time);

        var attempt = client.TryTranscribeAsync(Audio, Options(apiKey: "test-key"));
        await secondSend.Task.WaitAsync(Bound);

        time.Timers.Single().Fire();
        var result = await attempt.WaitAsync(Bound);

        Assert.False(result.Succeeded);
        Assert.Equal("timeout", result.DiagnosticReason);
        Assert.Equal(2, network.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancelling_the_dictation_cancels_the_credential_wait_instead_of_falling_back(
        bool credentialReportsItAsAnotherFailure)
    {
        using var cancel = new CancellationTokenSource();
        var inCredential = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tokens = new ConcurrentQueue<CancellationToken>();
        using var network = new RecordingNetwork((_, _) => Task.FromResult(JsonOk(Recognized)));
        using var client = ClientOver(network, SignedIn(async (_, ct) =>
        {
            tokens.Enqueue(ct);
            inCredential.SetResult();
            await WaitForCancellation(ct, credentialReportsItAsAnotherFailure);
            return Token("never");
        }));

        var attempt = client.TryTranscribeAsync(Audio, Options(apiKey: null), cancel.Token);
        await inCredential.Task.WaitAsync(Bound);
        await cancel.CancelAsync();

        // The credential was handed the dictation's cancellation, and whatever it makes of it, the dictation is cancelled
        // rather than decoded as a fallback.
        Assert.True(Assert.Single(tokens).IsCancellationRequested);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attempt.WaitAsync(Bound));
        Assert.Equal(0, network.Requests);
    }

    // --- F-24: an answer that is not a transcript response is a failure, not an empty dictation ----------------------

    public static TheoryData<string> MalformedAnswers() =>
    [
        "",
        "   ",
        "<html><body>Bad gateway</body></html>",
        "{}",
        "[]",
        "null",
        "\"text\"",
        """{"durationMilliseconds":1000}""",
        """{"error":{"code":"InternalServerError"}}""",
        """{"combinedPhrases":"text"}""",
        """{"combinedPhrases":[1,2]}""",
        """{"combinedPhrases":[{"channel":0}]}""",
        """{"phrases":{"text":"hello"}}""",
        """{"text":42}""",
        """{"combinedPhrases":[{"text":"cut off""",
    ];

    [Theory]
    [MemberData(nameof(MalformedAnswers))]
    public async Task A_success_status_with_a_body_that_is_not_a_transcript_response_is_a_failure_to_decode_on_this_pc(string body)
    {
        using var network = new RecordingNetwork((_, _) => Task.FromResult(JsonOk(body)));
        using var client = ClientOver(network);

        var result = await client.TryTranscribeAsync(Audio, Options(apiKey: "test-key"));

        Assert.False(result.Succeeded);
        Assert.Equal("malformed-response", result.DiagnosticReason);
        Assert.Equal(1, network.Requests);
        Assert.False(MaiTranscribeClient.TryParseTranscriptResponse(body, out _));
    }

    public static TheoryData<string> RecognizedAnswersThatHeardNothing() =>
    [
        """{"durationMilliseconds":1000,"combinedPhrases":[{"channel":0,"text":""}],"phrases":[]}""",
        """{"combinedPhrases":[],"phrases":[]}""",
        """{"phrases":[{"text":"  "}]}""",
        """{"text":""}""",
        """{"DisplayText":""}""",
    ];

    [Theory]
    [MemberData(nameof(RecognizedAnswersThatHeardNothing))]
    public async Task A_recognized_response_that_heard_nothing_is_still_a_success_with_no_text(string body)
    {
        using var network = new RecordingNetwork((_, _) => Task.FromResult(JsonOk(body)));
        using var client = ClientOver(network);

        var result = await client.TryTranscribeAsync(Audio, Options(apiKey: "test-key"));

        Assert.True(result.Succeeded);
        Assert.Equal(string.Empty, result.Text);
        Assert.Equal(1, network.Requests);
    }

    [Theory]
    [InlineData("""{"combinedPhrases":[{"channel":0,"text":"Hello there."},{"channel":1,"text":"General Kenobi."}]}""", "Hello there. General Kenobi.")]
    [InlineData("""{"combinedPhrases":[{"text":""}],"phrases":[{"text":"From phrases."}]}""", "From phrases.")]
    [InlineData("""{"phrases":[{"text":"One"},{"text":"two"}]}""", "One two")]
    [InlineData("""{"text":"  Plain text.  "}""", "Plain text.")]
    [InlineData("""{"DisplayText":"Display text."}""", "Display text.")]
    public void Every_response_shape_the_client_reads_still_yields_its_transcript(string body, string expected)
    {
        Assert.True(MaiTranscribeClient.TryParseTranscriptResponse(body, out var transcript));
        Assert.Equal(expected, transcript);
    }

    [Fact]
    public void A_malformed_success_answer_is_decoded_on_this_pc_instead_of_being_reported_as_silence()
    {
        var logger = new CapturingLogger<TranscriptionService>();
        var localDecodes = 0;
        using var network = new RecordingNetwork((_, _) => Task.FromResult(JsonOk("<html><body>Bad gateway</body></html>")));
        using var client = ClientOver(network);
        using var service = new TranscriptionService(
            () => new StubRecognizer(() =>
            {
                localDecodes++;
                return "Decoded on this PC.";
            }),
            logger,
            client,
            _ => Options(apiKey: "test-key"));

        var result = service.Transcribe(Audio, AdmissionFor(Of(1)), CancellationToken.None);

        Assert.Equal("Decoded on this PC.", result.Text);
        Assert.Equal(TranscriptionModelCatalog.DefaultId, result.ModelId);
        Assert.Equal(1, localDecodes);
        Assert.Equal(1, network.Requests);
        Assert.Contains(
            logger.Entries,
            entry => entry.Level == LogLevel.Warning && entry.Message.Contains("malformed-response", StringComparison.Ordinal));
    }

    // --- helpers ----------------------------------------------------------------------------------------------------

    private static AppSettings SettingsFor(string modelId, bool postProcessing = true)
    {
        var settings = AppSettings.CreateDefault();
        settings.TranscriptionModelId = modelId;
        settings.ApplyPostProcessing = postProcessing;
        settings.MaiTranscribeEndpoint = Endpoint;
        settings.AiCleanupAzureApiKey = "test-key";
        return settings;
    }

    private static VocabularyGeneration GenerationOf(LibraryVocabulary libraries, params DictionaryEntry[] dictionary) =>
        new(libraries.Generation, dictionary, libraries, CompiledDictionaryRules.Empty);

    private static TranscriptionAdmission AdmissionFor(LibraryVocabulary libraries) =>
        new(SettingsFor(TranscriptionModelCatalog.MaiTranscribe2Id), GenerationOf(libraries));

    private static MaiTranscribeOptions Options(
        string? apiKey,
        string endpoint = Endpoint,
        MaiPhraseAdmission? admission = null,
        IReadOnlyList<string>? phrases = null) => new(
            Enabled: true,
            MaiEndpointOverride: endpoint,
            FoundryEndpoint: null,
            ApiKey: apiKey,
            PhraseList: phrases,
            PhraseAdmission: admission);

    // The options of a dictation whose phrases are the admitted vocabulary's AI entries, judged at each send by the source.
    private static MaiTranscribeOptions LibraryOptions(LibraryVocabulary admitted, ILibraryVocabularySource source, string? apiKey) =>
        Options(
            apiKey,
            admission: new MaiPhraseAdmission(admitted.AiScope, source),
            phrases: MaiTranscribePhraseList.Build(null, admitted.AiEntries));

    // The client over a fake network. A case that signs in names its credential (SignedIn); one that does not gets a factory
    // that fails the case, so the production credential factory, and with it the Azure CLI, is never reached from here.
    private static MaiTranscribeClient ClientOver(
        HttpMessageHandler network,
        Func<AzureCredentialRequest, TokenCredential>? credentialFactory = null,
        TimeProvider? time = null) =>
        new(NullLogger<MaiTranscribeClient>.Instance, null, network, credentialFactory ?? NoSignIn, null, time);

    private static readonly Func<AzureCredentialRequest, TokenCredential> NoSignIn =
        _ => throw new InvalidOperationException("The production credential factory must not run in these cases.");

    // A sign-in whose token acquisition the case scripts, beneath the client's own sign-in step.
    private static Func<AzureCredentialRequest, TokenCredential> SignedIn(
        Func<TokenRequestContext, CancellationToken, ValueTask<AccessToken>> acquire) =>
        _ => new ScriptedCredential(acquire);

    private static AccessToken Token(string value = "token") => new(value, DateTimeOffset.UtcNow.AddHours(1));

    // Holds until the token is cancelled, as a sign-in queued behind another one does. Some credentials then report the
    // cancellation as another failure, and the attempt must still read it as what ended the wait.
    private static async Task WaitForCancellation(CancellationToken token, bool reportAsAnotherFailure)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, token);
        }
        catch (OperationCanceledException) when (reportAsAnotherFailure)
        {
            throw new InvalidOperationException("The sign-in was interrupted.");
        }
    }

    // A read that must not happen: the case fails with it if it does.
    private static Func<T> Unread<T>(string what) =>
        () => throw new InvalidOperationException($"The {what} was read.");

    private static HttpResponseMessage JsonOk(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static string JsonResponse(string body) =>
        $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";

    private static string Redirect(string to) =>
        $"HTTP/1.1 307 Temporary Redirect\r\nLocation: {to}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";

    // The container the app builds, with the fakes this file needs where the real cloud client, dictionary and library
    // vocabulary would be, over a data folder of its own.
    private static ServiceProvider ProviderWith(
        TempDirectory temp, IMaiTranscribeClient mai, IDictionaryRepository dictionary, ILibraryVocabularySource source)
    {
        var paths = new AppPaths(temp.Combine("data"));
        paths.EnsureCreated();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScribeCore();
        services.AddSingleton(paths);
        services.AddSingleton(mai);
        services.AddSingleton(dictionary);
        services.AddSingleton(source);
        return services.BuildServiceProvider();
    }

    private static string ReadSource(params string[] relative)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Scribe.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root.FullName, Path.Combine(relative)));
    }

    // The network under the client: counts every request that reaches it and answers as the case scripts.
    private sealed class RecordingNetwork(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        private int _requests;

        public int Requests => Volatile.Read(ref _requests);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            return respond(request, cancellationToken);
        }
    }

    // A credential the case scripts. Only what it does inside GetTokenAsync is the case's: the scope it is asked for, the
    // cancellation it is handed and the token it returns are what the client's own sign-in step makes of them.
    private sealed class ScriptedCredential(Func<TokenRequestContext, CancellationToken, ValueTask<AccessToken>> acquire)
        : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            acquire(requestContext, cancellationToken);
    }

    private sealed class StubMai(Func<MaiTranscribeOptions, CancellationToken, MaiTranscribeAttemptResult> attempt)
        : IMaiTranscribeClient
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<MaiTranscribeAttemptResult> TryTranscribeAsync(
            CapturedAudio audio,
            MaiTranscribeOptions options,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(attempt(options, cancellationToken));
        }
    }

    private sealed class StubRecognizer(Func<string> decode) : ISpeechRecognizer
    {
        public string ModelId => TranscriptionModelCatalog.DefaultId;

        public string Decode(float[] samples, int sampleRate) => decode();

        public void Dispose()
        {
        }
    }

    // A dictionary that fails every read and counts how often it was asked.
    private sealed class FailingDictionary : IDictionaryRepository
    {
        private int _reads;

        public int Reads => Volatile.Read(ref _reads);

        public IReadOnlyList<DictionaryEntry> GetAll() => Fail();

        public IReadOnlyList<DictionaryEntry> GetEnabled() => Fail();

        public DictionaryEntry Add(DictionaryEntry entry) => throw new NotSupportedException();

        public IReadOnlyList<DictionaryEntry> AddRange(IReadOnlyList<DictionaryEntry> entries) => throw new NotSupportedException();

        public void Update(DictionaryEntry entry) => throw new NotSupportedException();

        public void Delete(long id) => throw new NotSupportedException();

        public void SaveAll(IReadOnlyList<DictionaryEntry> entries) => throw new NotSupportedException();

        public int SeedIfEmpty(IEnumerable<DictionaryEntry> entries) => throw new NotSupportedException();

        public int DisableUnmodifiedEntries(IEnumerable<DictionaryEntry> entries) => throw new NotSupportedException();

        private IReadOnlyList<DictionaryEntry> Fail()
        {
            Interlocked.Increment(ref _reads);
            throw new InvalidOperationException("The dictionary could not be read.");
        }
    }

    // A loopback HTTP/1.1 server that answers every request with one canned response and keeps the head of each request that
    // reached it. It reads a request whole, headers and then the body its Content-Length names, so a client that sent a body
    // is never reset.
    private sealed class LoopbackServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentQueue<string> _heads = new();
        private readonly ConcurrentBag<Task> _serving = [];
        private readonly Func<string> _answer;
        private readonly Task _accepting;

        internal LoopbackServer(Func<string> answer)
        {
            _answer = answer;
            _listener.Start();
            _accepting = AcceptAsync();
        }

        internal string Origin => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

        internal string[] Requests => [.. _heads];

        private async Task AcceptAsync()
        {
            try
            {
                while (true)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _serving.Add(ServeAsync(client));
                }
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var head = await ReadHeadAsync(stream);
                    if (head is null)
                    {
                        return;
                    }

                    var remaining = ContentLengthOf(head);
                    var buffer = new byte[4096];
                    while (remaining > 0)
                    {
                        var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), _stop.Token);
                        if (read == 0)
                        {
                            break;
                        }

                        remaining -= read;
                    }

                    _heads.Enqueue(head);
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(_answer()), _stop.Token);
                }
                catch (Exception) when (_stop.IsCancellationRequested)
                {
                }
                catch (IOException)
                {
                    // The client closed the connection.
                }
            }
        }

        private async Task<string?> ReadHeadAsync(NetworkStream stream)
        {
            var head = new StringBuilder();
            var one = new byte[1];
            while (head.Length < 4 || head.ToString(head.Length - 4, 4) != "\r\n\r\n")
            {
                if (await stream.ReadAsync(one, _stop.Token) == 0)
                {
                    return null;
                }

                head.Append((char)one[0]);
            }

            return head.ToString();
        }

        private static int ContentLengthOf(string head)
        {
            const string Name = "Content-Length:";
            foreach (var line in head.Split("\r\n"))
            {
                if (line.StartsWith(Name, StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(line[Name.Length..].Trim(), out var length))
                {
                    return length;
                }
            }

            return 0;
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            await _accepting;
            await Task.WhenAll(_serving);
            _stop.Dispose();
        }
    }
}
