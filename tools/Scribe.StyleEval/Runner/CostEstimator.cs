namespace Scribe.StyleEval.Runner;

/// <summary>
/// A cost estimate before the run, refined from observed token counts once cells start landing.
/// </summary>
/// <remarks>
/// The up-front number is a character-count approximation, which is enough to answer the only
/// question it needs to: is this run pennies, or is it a hundred dollars. It is deliberately
/// replaced by observed usage after the first few cells rather than trusted for the whole run,
/// because a reasoning deployment's output token count is not predictable from its prompt length.
/// </remarks>
internal sealed class CostEstimator(double priceInputPerMillion, double priceOutputPerMillion)
{
    private const int CellsBeforeRefining = 8;

    private long _observedCells;
    private long _observedInput;
    private long _observedOutput;

    /// <summary>Rough tokens for a string. Four characters per token is close enough for a forecast.</summary>
    public static long EstimateTokens(string text) => Math.Max(1, text.Length / 4);

    /// <summary>Records a completed cell's real usage.</summary>
    public void Observe(long? inputTokens, long? outputTokens)
    {
        if (inputTokens is not { } input || outputTokens is not { } output)
        {
            return;
        }

        Interlocked.Increment(ref _observedCells);
        Interlocked.Add(ref _observedInput, input);
        Interlocked.Add(ref _observedOutput, output);
    }

    /// <summary>True once enough cells have landed for the observed average to beat the guess.</summary>
    public bool IsRefined => Interlocked.Read(ref _observedCells) >= CellsBeforeRefining;

    /// <summary>USD already spent, from observed usage only.</summary>
    public double SpentUsd =>
        (Interlocked.Read(ref _observedInput) / 1_000_000.0 * priceInputPerMillion) +
        (Interlocked.Read(ref _observedOutput) / 1_000_000.0 * priceOutputPerMillion);

    /// <summary>
    /// Projected total USD for <paramref name="totalCells"/>, using observed averages when they
    /// exist and the character approximation otherwise.
    /// </summary>
    public double Project(int totalCells, long estimatedInputTokensPerCell, long estimatedOutputTokensPerCell)
    {
        var cells = Interlocked.Read(ref _observedCells);
        var (input, output) = cells > 0
            ? (Interlocked.Read(ref _observedInput) / (double)cells, Interlocked.Read(ref _observedOutput) / (double)cells)
            : (estimatedInputTokensPerCell, (double)estimatedOutputTokensPerCell);

        return (totalCells * input / 1_000_000.0 * priceInputPerMillion) +
               (totalCells * output / 1_000_000.0 * priceOutputPerMillion);
    }

    /// <summary>One line describing where the projection came from.</summary>
    public string Basis => Interlocked.Read(ref _observedCells) switch
    {
        0 => "character-count approximation, no cells observed yet",
        var n when n < CellsBeforeRefining => $"character-count approximation, {n} cell(s) observed so far",
        var n => $"observed usage over {n} cell(s)",
    };
}
