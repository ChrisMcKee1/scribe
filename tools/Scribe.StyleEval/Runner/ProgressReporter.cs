using System.Diagnostics;

namespace Scribe.StyleEval.Runner;

/// <summary>
/// Progress that fits on one line and does not scroll.
/// </summary>
/// <remarks>
/// Ten thousand cells printing a line each is ten thousand lines of console, which buries the
/// failures worth looking at. The live counter overwrites itself on a terminal, a periodic heartbeat
/// takes its place when output is redirected to a file or a CI log, and the only thing that ever
/// gets its own permanent line is a cell that failed a check or errored.
/// </remarks>
internal sealed class ProgressReporter(int totalCells, bool interactive)
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Lock _gate = new();
    private int _done;
    private int _errors;
    private int _sanitizerRejects;
    private int _negativeFailCells;
    private int _positiveFailCells;
    private int _lastLineLength;
    private TimeSpan _lastHeartbeat = TimeSpan.Zero;

    public int Done => Volatile.Read(ref _done);

    public int Errors => Volatile.Read(ref _errors);

    public int SanitizerRejects => Volatile.Read(ref _sanitizerRejects);

    public int NegativeFailCells => Volatile.Read(ref _negativeFailCells);

    public int PositiveFailCells => Volatile.Read(ref _positiveFailCells);

    public TimeSpan Elapsed => _clock.Elapsed;

    public void Record(CellResult result, CostEstimator cost)
    {
        lock (_gate)
        {
            _done++;

            if (result.Error is not null)
            {
                _errors++;
            }
            else
            {
                if (!result.SanitizerAccepted)
                {
                    _sanitizerRejects++;
                }

                if (result.NegativeFailures > 0)
                {
                    _negativeFailCells++;
                }

                if (result.PositiveFailures > 0)
                {
                    _positiveFailCells++;
                }
            }

            var line = Compose(cost);

            if (interactive)
            {
                Write("\r" + line.PadRight(_lastLineLength));
                _lastLineLength = line.Length;
            }
            else if (_clock.Elapsed - _lastHeartbeat > TimeSpan.FromSeconds(20) || _done == totalCells)
            {
                _lastHeartbeat = _clock.Elapsed;
                Console.WriteLine(line);
            }
        }
    }

    /// <summary>Prints a permanent line above the live counter, for something worth keeping.</summary>
    public void Note(string message)
    {
        lock (_gate)
        {
            if (interactive && _lastLineLength > 0)
            {
                Write("\r" + new string(' ', _lastLineLength) + "\r");
                _lastLineLength = 0;
            }

            Console.WriteLine(message);
        }
    }

    /// <summary>Ends the live line so the summary starts on a clean row.</summary>
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
            $"rule-fails {_negativeFailCells}  missed-structure {_positiveFailCells}  " +
            $"rejected {_sanitizerRejects}  errors {_errors}  " +
            $"{rate:F1}/s  eta {Format(remaining)}  ${cost.SpentUsd:F2}";
    }

    private static string Format(TimeSpan span) =>
        span.TotalHours >= 1 ? $"{(int)span.TotalHours}h{span.Minutes:D2}m" : $"{span.Minutes:D2}m{span.Seconds:D2}s";

    private static void Write(string text) => Console.Out.Write(text);
}
