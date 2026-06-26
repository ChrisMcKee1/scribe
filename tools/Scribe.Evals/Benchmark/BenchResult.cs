namespace Scribe.Evals.Benchmark;

/// <summary>Per-dimension judge scores (0–100). Null when the model produced no gradable output.</summary>
internal sealed record BenchDimensions(int Mechanics, int Fidelity, int Disfluency, int Instruction);

/// <summary>
/// A single model's benchmark outcome. Serialized to <c>results.json</c> after every model so a long
/// run is always resumable and the markdown board can be regenerated from disk at any time.
/// </summary>
internal sealed record BenchResult
{
    public required string Group { get; init; }            // "Cloud" | "Local"
    public required string Id { get; init; }               // display id (model/alias)
    public required string Provider { get; init; }         // "AzureFoundry" | "FoundryLocal"
    public string? Endpoint { get; init; }
    public required string Target { get; init; }           // deployment or alias actually called
    public string? ModelName { get; init; }
    public string? Note { get; init; }

    /// <summary>ok | degraded | error | not-ready | skipped.</summary>
    public required string Status { get; init; }
    public string? Error { get; init; }

    public double MedianMs { get; init; }
    public double MinMs { get; init; }
    public double MaxMs { get; init; }
    public int Runs { get; init; }
    public double[] AllMs { get; init; } = [];

    public int? Quality { get; init; }                     // judge overall 0–100
    public string? Grade { get; init; }                    // derived from Quality
    public BenchDimensions? Dims { get; init; }
    public string[] Flags { get; init; } = [];
    public string? Rationale { get; init; }

    public bool Changed { get; init; }                     // output differed from the raw transcript
    public string? Output { get; init; }                   // cleaned text (stored verbatim for review)

    public required string LoadedAtUtc { get; init; }
    public double LoadSeconds { get; init; }               // time from Configure to Ready (download+load)

    /// <summary>Letter grade from a 0–100 score using a conventional US scale.</summary>
    public static string GradeFor(int score) => score switch
    {
        >= 97 => "A+",
        >= 93 => "A",
        >= 90 => "A-",
        >= 87 => "B+",
        >= 83 => "B",
        >= 80 => "B-",
        >= 77 => "C+",
        >= 73 => "C",
        >= 70 => "C-",
        >= 67 => "D+",
        >= 60 => "D",
        _ => "F",
    };
}
