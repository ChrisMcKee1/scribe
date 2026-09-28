using System.Globalization;
using BenchmarkDotNet.Attributes;
using Scribe.Core.Settings;

namespace Scribe.Benchmarks;

/// <summary>
/// One keystroke in Your words' Find a word (SettingsWindow.DictionaryFilter over every row, after memopt UI-7): the query
/// is a new string per keystroke, normalized once, and every row is matched. <see cref="Current"/> normalizes each row's
/// fields again, as TextFilter.Matches does; <see cref="CachedRowText"/> matches through TextFilter.MatchesCached over the
/// fields' kept normalized text (CachedRowSearchText), which rows make once and keep until edited.
/// </summary>
[MemoryDiagnoser]
public class YourWordsFilterBenchmarks
{
    [Params(1000, 5000)]
    public int Rows;

    private (string Pattern, string Replacement)[] _rows = null!;
    private CachedSearchText[] _patternText = null!;
    private CachedSearchText[] _replacementText = null!;
    private char[] _typed = null!;

    [GlobalSetup]
    public void Setup()
    {
        _rows = new (string, string)[Rows];
        _patternText = new CachedSearchText[Rows];
        _replacementText = new CachedSearchText[Rows];
        for (var i = 0; i < Rows; i++)
        {
            var n = i.ToString(CultureInfo.InvariantCulture);
            _rows[i] = ("spoken form caf\u00e9 " + n, "Written Form Caf\u00e9 " + n);
            _patternText[i].For(_rows[i].Pattern);
            _replacementText[i].For(_rows[i].Replacement);
        }

        _typed = "cafe 12".ToCharArray();
        if (Current() != CachedRowText())
        {
            throw new InvalidOperationException("The cached text must match exactly the rows the current filter matches.");
        }
    }

    [Benchmark(Baseline = true)]
    public int Current()
    {
        var query = new string(_typed);
        var matched = 0;
        foreach (var (pattern, replacement) in _rows)
        {
            if (TextFilter.Matches(query, pattern, replacement))
            {
                matched++;
            }
        }

        return matched;
    }

    [Benchmark]
    public int CachedRowText()
    {
        var query = new string(_typed);
        var matched = 0;
        for (var i = 0; i < _rows.Length; i++)
        {
            if (TextFilter.MatchesCached(query, ref _patternText[i], _rows[i].Pattern, ref _replacementText[i], _rows[i].Replacement))
            {
                matched++;
            }
        }

        return matched;
    }
}
