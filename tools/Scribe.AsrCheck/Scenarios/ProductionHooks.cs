using System.Reflection;
using NAudio.Wave;
using Scribe.Core.Audio;
using Scribe.Core.Models;
using Scribe.Core.Transcription;

namespace Scribe.AsrCheck.Scenarios;

/// <summary>
/// Binds the production entry points the scenario suite drives but cannot name in source.
/// <para>
/// The same harness source has to compile against the 0.4.2 baseline and against later builds, and
/// the members it needs differ in visibility between them: the capture conversion is private in the
/// baseline, and the cancellable decode overload does not exist there at all. Reflection lets one
/// source tree call the REAL implementation on every build (never a private copy of it) and report
/// "not supported" precisely where a build lacks a member, instead of failing to compile.
/// </para>
/// </summary>
internal sealed class ProductionHooks
{
    internal delegate float PeakMeter(ReadOnlySpan<byte> buffer, WaveFormat format);

    private const BindingFlags StaticMembers = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private ProductionHooks()
    {
    }

    /// <summary><c>AudioCaptureService.ResampleToTarget</c>: the downmix and resample every capture takes.</summary>
    public Func<byte[], int, WaveFormat, float[]>? ResampleToTarget { get; private init; }

    /// <summary><c>AudioCaptureService.NormalizeFormat</c>: unwraps the extensible mix format WASAPI reports.</summary>
    public Func<WaveFormat, WaveFormat>? NormalizeFormat { get; private init; }

    /// <summary><c>AudioCaptureService.ComputePeak</c>: the level each capture buffer feeds the meter and auto-stop.</summary>
    public PeakMeter? ComputePeak { get; private init; }

    /// <summary><c>AudioCaptureService.SilentCapturePeak</c>: below this whole-capture peak the mic "recorded nothing".</summary>
    public float? SilentCapturePeak { get; private init; }

    /// <summary><c>TranscriptionChunker.Plan</c>: how Transcribe splits a long capture.</summary>
    public Func<float[], int, IReadOnlyList<(int Start, int Length)>>? PlanChunks { get; private init; }

    /// <summary><c>TranscriptionChunker.MaxChunkSeconds</c>.</summary>
    public int? MaxChunkSeconds { get; private init; }

    /// <summary><c>TranscriptionService.ResolveThreadCount</c>: what "0 = auto" becomes on this machine.</summary>
    public Func<int, int>? ResolveThreadCount { get; private init; }

    /// <summary><c>TranscriptionService.Transcribe(CapturedAudio, CancellationToken)</c>, when the build has it.</summary>
    public MethodInfo? TranscribeWithCancellation { get; private init; }

    /// <summary>The defaults the dictation controller relies on when it constructs the tracker.</summary>
    public SilenceTrackerDefaults? SilenceDefaults { get; private init; }

    /// <summary>
    /// <c>SilenceAutoStopTracker.VoiceThreshold</c>: the level a buffer must exceed right now to count as voice, on
    /// builds whose tracker adapts to the noise floor. Null on builds with one fixed threshold.
    /// </summary>
    public Func<SilenceAutoStopTracker, float>? VoiceThreshold { get; private init; }

    public static ProductionHooks Bind()
    {
        var capture = typeof(AudioCaptureService);
        var chunker = capture.Assembly.GetType("Scribe.Core.Transcription.TranscriptionChunker");

        return new ProductionHooks
        {
            ResampleToTarget = BindStatic<Func<byte[], int, WaveFormat, float[]>>(
                capture, "ResampleToTarget", typeof(byte[]), typeof(int), typeof(WaveFormat)),
            NormalizeFormat = BindStatic<Func<WaveFormat, WaveFormat>>(capture, "NormalizeFormat", typeof(WaveFormat)),
            ComputePeak = BindStatic<PeakMeter>(capture, "ComputePeak", typeof(ReadOnlySpan<byte>), typeof(WaveFormat)),
            SilentCapturePeak = ReadConstant<float>(capture, "SilentCapturePeak"),
            PlanChunks = chunker is null
                ? null
                : BindStatic<Func<float[], int, IReadOnlyList<(int Start, int Length)>>>(
                    chunker, "Plan", typeof(float[]), typeof(int)),
            MaxChunkSeconds = chunker is null ? null : ReadConstant<int>(chunker, "MaxChunkSeconds"),
            ResolveThreadCount = BindStatic<Func<int, int>>(typeof(TranscriptionService), "ResolveThreadCount", typeof(int)),
            TranscribeWithCancellation = typeof(TranscriptionService).GetMethod(
                nameof(TranscriptionService.Transcribe),
                BindingFlags.Instance | BindingFlags.Public,
                [typeof(CapturedAudio), typeof(CancellationToken)]),
            SilenceDefaults = SilenceTrackerDefaults.Read(),
            VoiceThreshold = BindVoiceThreshold(),
        };
    }

    /// <summary>A decode delegate that honours cancellation, bound to one service instance.</summary>
    public Func<CapturedAudio, CancellationToken, TranscriptionResult>? BindCancellableTranscribe(TranscriptionService service) =>
        TranscribeWithCancellation?.CreateDelegate<Func<CapturedAudio, CancellationToken, TranscriptionResult>>(service);

    public IReadOnlyDictionary<string, bool> Availability() => new SortedDictionary<string, bool>(StringComparer.Ordinal)
    {
        ["AudioCaptureService.ResampleToTarget"] = ResampleToTarget is not null,
        ["AudioCaptureService.NormalizeFormat"] = NormalizeFormat is not null,
        ["AudioCaptureService.ComputePeak"] = ComputePeak is not null,
        ["AudioCaptureService.SilentCapturePeak"] = SilentCapturePeak is not null,
        ["TranscriptionChunker.Plan"] = PlanChunks is not null,
        ["TranscriptionChunker.MaxChunkSeconds"] = MaxChunkSeconds is not null,
        ["TranscriptionService.ResolveThreadCount"] = ResolveThreadCount is not null,
        ["TranscriptionService.Transcribe(CapturedAudio, CancellationToken)"] = TranscribeWithCancellation is not null,
        ["SilenceAutoStopTracker constructor defaults"] = SilenceDefaults is not null,
        ["SilenceAutoStopTracker.VoiceThreshold"] = VoiceThreshold is not null,
    };

    private static Func<SilenceAutoStopTracker, float>? BindVoiceThreshold()
    {
        var getter = typeof(SilenceAutoStopTracker)
            .GetProperty("VoiceThreshold", BindingFlags.Instance | BindingFlags.Public)?
            .GetGetMethod();
        return getter?.ReturnType == typeof(float)
            ? getter.CreateDelegate<Func<SilenceAutoStopTracker, float>>()
            : null;
    }

    private static T? BindStatic<T>(Type owner, string name, params Type[] parameters)
        where T : Delegate
    {
        var method = owner.GetMethod(name, StaticMembers, parameters);
        if (method is null)
        {
            return null;
        }

        try
        {
            return method.CreateDelegate<T>();
        }
        catch (ArgumentException)
        {
            // Same name and parameters but a different return type: the member exists and is not
            // the one this suite knows how to drive, which is the same as not having it.
            return null;
        }
    }

    private static T? ReadConstant<T>(Type owner, string name)
        where T : struct =>
        owner.GetField(name, StaticMembers)?.GetRawConstantValue() is T value ? value : null;
}

/// <summary>
/// The silence thresholds <see cref="SilenceAutoStopTracker"/> applies when constructed the way
/// <c>DictationController</c> constructs it (start time only). Read from the constructor's own
/// default values so the report states the production numbers rather than a copy of them.
/// </summary>
/// <param name="SilenceThreshold">
/// The lowest level that can count as voice: the fixed threshold on builds before 0.4.3, and the absolute floor under
/// the adaptive noise-floor threshold from 0.4.3 on. Kept under one name so reports from both compare directly.
/// </param>
internal sealed record SilenceTrackerDefaults(float SilenceThreshold, long SilenceHoldMs, long LeadInLimitMs)
{
    public static SilenceTrackerDefaults? Read()
    {
        foreach (var constructor in typeof(SilenceAutoStopTracker).GetConstructors())
        {
            var parameters = constructor.GetParameters();
            var threshold = Default<float>(parameters, "absoluteFloor") ?? Default<float>(parameters, "silenceThreshold");
            var hold = Default<long>(parameters, "silenceHoldMs");
            var leadIn = Default<long>(parameters, "leadInLimitMs");
            if (threshold is not null && hold is not null && leadIn is not null)
            {
                return new SilenceTrackerDefaults(threshold.Value, hold.Value, leadIn.Value);
            }
        }

        return null;
    }

    private static T? Default<T>(ParameterInfo[] parameters, string name)
        where T : struct
    {
        var parameter = parameters.FirstOrDefault(p => p.Name == name);
        return parameter is { HasDefaultValue: true, DefaultValue: T value } ? value : null;
    }
}
