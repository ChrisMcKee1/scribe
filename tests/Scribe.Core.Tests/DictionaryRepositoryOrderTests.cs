using Scribe.Core.Models;
using Scribe.Core.Persistence;

namespace Scribe.Core.Tests;

/// <summary>
/// Dictation compiles the dictionary in the order the repository reads it back, and rule order breaks ties between rules
/// that match the same text, so tests that model what dictation writes without a database per case sort with
/// <see cref="DictionaryReadOrder.Pattern"/>. This pins that comparer to SQLite's own order.
/// </summary>
public sealed class DictionaryRepositoryOrderTests
{
    [Fact]
    public void The_repository_reads_patterns_back_in_byte_order_which_is_not_the_ordinal_order()
    {
        // Letters the matcher folds differently from OrdinalIgnoreCase, accented and non-Latin ones, and a character past the
        // Basic Multilingual Plane beside one near its end: those two are where UTF-8 byte order and .NET's ordinal UTF-16
        // order disagree.
        string[] patterns =
        [
            "k", "K", "\u212A", "ΟΣ", "ος", "οσ", "straße", "STRA\u1E9EE", "strasse", "istanbul", "\u0130stanbul", "\u0131stanbul",
            "Zebra", "apple", "Äpfel", "get hub", "get-hub", "\U0001F600 smile", "\uFF21 wide", "123",
        ];
        using var database = ScribeDatabase.CreateInMemory();
        var repository = new DictionaryRepository(database);
        repository.AddRange([.. patterns.Reverse().Select((p, i) => new DictionaryEntry(0, p, "x" + i, WholeWord: true, Enabled: i % 3 != 0))]);

        var all = repository.GetAll().Select(e => e.Pattern).ToList();
        var enabled = repository.GetEnabled().Select(e => e.Pattern).ToList();

        Assert.Equal(patterns.OrderBy(p => p, DictionaryReadOrder.Pattern), all);
        Assert.Equal(all.Where(enabled.Contains), enabled);
        Assert.NotEqual(patterns.OrderBy(p => p, StringComparer.Ordinal), all);
    }
}

/// <summary>
/// The order <see cref="DictionaryRepository"/> returns entries in: <c>ORDER BY pattern</c> under SQLite's default BINARY
/// collation, which compares the UTF-8 bytes (so code point order, not .NET's ordinal UTF-16 order).
/// </summary>
internal static class DictionaryReadOrder
{
    public static IComparer<string> Pattern { get; } = Comparer<string>.Create(
        (a, b) => System.Text.Encoding.UTF8.GetBytes(a ?? string.Empty).AsSpan()
            .SequenceCompareTo(System.Text.Encoding.UTF8.GetBytes(b ?? string.Empty)));
}
