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
    string? ArchiveDirectory = null)
{
    public bool IsBundled => DownloadUri is null;
}

public static class TranscriptionModelCatalog
{
    public const string DefaultId = "parakeet-tdt-0.6b-v3-int8";

    private static readonly string[] TransducerFiles =
        ["encoder.int8.onnx", "decoder.int8.onnx", "joiner.int8.onnx", "tokens.txt"];

    private static readonly string[] MoonshineFiles =
        ["preprocess.onnx", "encode.int8.onnx", "uncached_decode.int8.onnx", "cached_decode.int8.onnx", "tokens.txt"];

    public static IReadOnlyList<TranscriptionModel> Curated { get; } =
    [
        new(
            DefaultId,
            "Parakeet TDT 0.6B v3 (recommended)",
            "Bundled multilingual model. Best general-purpose choice.",
            "25 European languages",
            0,
            TranscriptionModelArchitecture.NemoTransducer,
            TransducerFiles),
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
}
