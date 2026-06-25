namespace Scribe.Core.Infrastructure;

/// <summary>The set of model files Scribe needs, rooted at a single directory.</summary>
public sealed class ModelSet
{
    public const string EncoderFile = "encoder.int8.onnx";
    public const string DecoderFile = "decoder.int8.onnx";
    public const string JoinerFile = "joiner.int8.onnx";
    public const string TokensFile = "tokens.txt";
    public const string SileroVadFile = "silero_vad_v5.onnx";

    private ModelSet(string directory)
    {
        Directory = directory;
        EncoderPath = Path.Combine(directory, EncoderFile);
        DecoderPath = Path.Combine(directory, DecoderFile);
        JoinerPath = Path.Combine(directory, JoinerFile);
        TokensPath = Path.Combine(directory, TokensFile);
        SileroVadPath = Path.Combine(directory, SileroVadFile);
    }

    public string Directory { get; }
    public string EncoderPath { get; }
    public string DecoderPath { get; }
    public string JoinerPath { get; }
    public string TokensPath { get; }
    public string SileroVadPath { get; }

    /// <summary>True when every ASR file required to construct the recognizer is present.</summary>
    public bool AsrComplete =>
        File.Exists(EncoderPath) &&
        File.Exists(DecoderPath) &&
        File.Exists(JoinerPath) &&
        File.Exists(TokensPath);

    /// <summary>True when the Silero VAD model is available.</summary>
    public bool VadAvailable => File.Exists(SileroVadPath);

    /// <summary>Names of any missing ASR files (for diagnostics / error messages).</summary>
    public IReadOnlyList<string> MissingAsrFiles()
    {
        var missing = new List<string>(4);
        if (!File.Exists(EncoderPath)) missing.Add(EncoderFile);
        if (!File.Exists(DecoderPath)) missing.Add(DecoderFile);
        if (!File.Exists(JoinerPath)) missing.Add(JoinerFile);
        if (!File.Exists(TokensPath)) missing.Add(TokensFile);
        return missing;
    }

    public static ModelSet ForDirectory(string directory) => new(directory);
}
