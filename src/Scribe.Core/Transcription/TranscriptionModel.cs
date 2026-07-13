namespace Scribe.Core.Transcription;

public enum TranscriptionModelArchitecture
{
    NemoTransducer,
    Moonshine,
}

public sealed record TranscriptionModel(
    string Id,
    string DisplayName,
    string Description,
    string Languages,
    long DownloadSize,
    TranscriptionModelArchitecture Architecture,
    IReadOnlyList<string> RequiredFiles,
    Uri? DownloadUri = null,
    string? ArchiveSha256 = null,
    string? ArchiveDirectory = null,
    IReadOnlyList<TranscriptionModelFile>? DownloadFiles = null)
{
    public bool IsBundled => DownloadUri is null && DownloadFiles is null;
}

public sealed record TranscriptionModelFile(
    string FileName,
    Uri DownloadUri,
    long Size,
    string Sha256);

public static class TranscriptionModelCatalog
{
    public const string DefaultId = "parakeet-tdt-0.6b-v3-int8";
    private const string ParakeetBaseUrl =
        "https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8/resolve/main/";

    private static readonly string[] TransducerFiles =
        ["encoder.int8.onnx", "decoder.int8.onnx", "joiner.int8.onnx", "tokens.txt"];

    private static readonly string[] MoonshineFiles =
        ["preprocess.onnx", "encode.int8.onnx", "uncached_decode.int8.onnx", "cached_decode.int8.onnx", "tokens.txt"];

    public static IReadOnlyList<TranscriptionModel> Curated { get; } =
    [
        new(
            DefaultId,
            "Parakeet TDT 0.6B v3 (recommended)",
            "Multilingual model. Best general-purpose choice.",
            "25 European languages",
            670_478_772,
            TranscriptionModelArchitecture.NemoTransducer,
            TransducerFiles,
            DownloadFiles:
            [
                File(
                    "encoder.int8.onnx",
                    652_184_281,
                    "acfc2b4456377e15d04f0243af540b7fe7c992f8d898d751cf134c3a55fd2247"),
                File(
                    "decoder.int8.onnx",
                    11_845_275,
                    "179e50c43d1a9de79c8a24149a2f9bac6eb5981823f2a2ed88d655b24248db4e"),
                File(
                    "joiner.int8.onnx",
                    6_355_277,
                    "3164c13fc2821009440d20fcb5fdc78bff28b4db2f8d0f0b329101719c0948b3"),
                File(
                    "tokens.txt",
                    93_939,
                    "d58544679ea4bc6ac563d1f545eb7d474bd6cfa467f0a6e2c1dc1c7d37e3c35d"),
            ]),
        new(
            "moonshine-base-en-int8",
            "Moonshine Base INT8",
            "Fast, accurate English-only model for CPU dictation.",
            "English",
            250_807_309,
            TranscriptionModelArchitecture.Moonshine,
            MoonshineFiles,
            new Uri("https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-moonshine-base-en-int8.tar.bz2"),
            "21870cecaa2e44e4e2bf63e02d1072bed183ccd10284871353bd9d24dad14e5e",
            "sherpa-onnx-moonshine-base-en-int8"),
        new(
            "moonshine-tiny-en-int8",
            "Moonshine Tiny INT8",
            "Smallest and fastest option for English-only dictation.",
            "English",
            107_600_538,
            TranscriptionModelArchitecture.Moonshine,
            MoonshineFiles,
            new Uri("https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-moonshine-tiny-en-int8.tar.bz2"),
            "d5fe6ec4334fef36255b2a4010412cad4c007e33103fec62fb5d17cad88086f2",
            "sherpa-onnx-moonshine-tiny-en-int8"),
    ];

    public static TranscriptionModel Resolve(string? id) =>
        Curated.FirstOrDefault(model => string.Equals(model.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? Curated[0];

    private static TranscriptionModelFile File(string name, long size, string sha256) =>
        new(name, new Uri(ParakeetBaseUrl + name), size, sha256);
}
