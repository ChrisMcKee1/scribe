using System.Diagnostics;
using Scribe.StyleEval.Runner;

namespace Scribe.StyleEval.Judge;

/// <summary>
/// One-line, non-scrolling progress for the judge pass.
/// </summary>
/// <remarks>
/// Deliberately counts different things from the generation reporter. What matters while a judge
/// pass runs is how many cells came back with a missed opportunity, how many findings were thrown
/// away for quoting a span that is not in the text, and how many verdicts were served from cache,
/// because all three say whether the pass is worth finishing before it finishes.
/// </remarks>
internal sealed class JudgeProgress(int totalCells, bool interactive)
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Lock _gate = new();
    private int _done;
    private int _errors;
    private int _cached;
    private int _cellsWithMiss;
    private int _findings;
    private int _ungrounded;
    private int _lastLineLength;
    private TimeSpan _lastHeartbeat = TimeSpan.Zero;

    public int Done => Volatile.Read(ref _done);

    public int Errors => Volatile.Read(ref _errors);

    public int Cached => Volatile.Read(ref _cached);

    public int CellsWithMiss => Volatile.Read(ref _cellsWithMiss);

    public int Findings => Volatile.Read(ref _findings);

    public int Ungrounded => Volatile.Read(ref _ungrounded);

    public TimeSpan Elapsed => _clock.Elapsed;

    public void Record(JudgeCell cell, CostEstimator cost)
    {
        ArgumentNullException.ThrowIfNull(cell);

        lock (_gate)
        {
            _done++;

            if (cell.Cached)
            {
                _cached++;
            }

            if (cell.Error is not null || cell.Verdict is null)
            {
                _errors++;
            }
            else
            {
                _findings += cell.Verdict.FindingCount;
                _ungrounded += cell.Verdict.UngroundedCount;
                if (cell.Verdict.GroundedMisses.Count > 0)
                {
                    _cellsWithMiss++;
                }
            }

            var line = Compose(cost);

            if (interactive)
            {
                Console.Out.Write("\r" + line.PadRight(_lastLineLength));
                _lastLineLength = line.Length;
            }
            else if (_clock.Elapsed - _lastHeartbeat > TimeSpan.FromSeconds(20) || _done == totalCells)
            {
                _lastHeartbeat = _clock.Elapsed;
                Console.WriteLine(line);
            }
        }
    }

    /// <summary>Prints a permanent line above the live counter.</summary>
    public void Note(string message)
    {
        lock (_gate)
        {
            if (interactive && _lastLineLength > 0)
            {
                Console.Out.Write("\r" + new string(' ', _lastLineLength) + "\r");
                _lastLineLength = 0;
            }

            Console.WriteLine(message);
        }
    }

    /// <summary>Ends the live line so what follows starts on a clean row.</summary>
    public void Finish()
    {
        lock (_gate)
        {
            if (interactive && _lastLineLength > 0)
            {
                Console.WriteLine();
                _lastLineLength = 0;
            }
        }
    }

    private string Compose(CostEstimator cost)
    {
        var percent = totalCells == 0 ? 100 : _done * 100.0 / totalCells;
        var rate = _clock.Elapsed.TotalSeconds > 0 ? _done / _clock.Elapsed.TotalSeconds : 0;
        var remaining = rate > 0 ? TimeSpan.FromSeconds((totalCells - _done) / rate) : TimeSpan.Zero;

        return
            $"{_done}/{totalCells} ({percent,5:F1}%)  " +
            $"cells-with-miss {_cellsWithMiss}  findings {_findings}  ungrounded {_ungrounded}  " +
            $"cached {_cached}  errors {_errors}  " +
            $"{rate:F1}/s  eta {Format(remaining)}  ${cost.SpentUsd:F2}";
    }

    private static string Format(TimeSpan span) =>
        span.TotalHours >= 1 ? $"{(int)span.TotalHours}h{span.Minutes:D2}m" : $"{span.Minutes:D2}m{span.Seconds:D2}s";
}
