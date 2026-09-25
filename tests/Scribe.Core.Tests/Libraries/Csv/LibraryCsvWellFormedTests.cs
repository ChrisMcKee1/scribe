using System.Text;
using Scribe.Core.Libraries;

namespace Scribe.Core.Tests.Libraries.Csv;

/// <summary>
/// Well-formed UTF-16 (acceptance X-11, amended after the review of sub-stream O): the writers encode strictly, so a
/// value or metadata string holding an unpaired surrogate is an <see cref="ArgumentException"/> and never reaches a file
/// as U+FFFD; a surrogate pair (an emoji, any character outside the Basic Multilingual Plane) is ordinary text that
/// round-trips; and the readers never produce an unpaired surrogate, whatever bytes they are given.
/// </summary>
public sealed class LibraryCsvWellFormedTests
{
    private const string Emoji = "\uD83D\uDE00";
    private const string Clef = "\uD834\uDD1E";

    private static readonly string[] Fields = ["spoken", "written", "name", "category", "description", "basedOn"];

    private static readonly (string Label, string Value)[] Malformed =
    [
        ("a lone high surrogate", "\uD83D"),
        ("a lone low surrogate", "\uDE00"),
        ("a high surrogate at the end", "team\uD83D"),
        ("a low surrogate at the start", "\uDE00team"),
        ("a pair in the wrong order", "\uDE00\uD83D"),
        ("a high surrogate before an ordinary letter", "\uD83Dx"),
        ("a pair and then a lone high surrogate", Emoji + "\uD83D"),
    ];

    private static readonly LibraryCsvCodec Codec = CsvTestData.Codec;

    private static readonly LibraryCsvCodec ShiftJisCodec = new(932);

    public static TheoryData<string, string> MalformedInEveryField()
    {
        var data = new TheoryData<string, string>();
        foreach (var field in Fields)
        {
            foreach (var (label, _) in Malformed)
            {
                data.Add(field, label);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(MalformedInEveryField))]
    public void Both_writers_refuse_a_string_that_is_not_well_formed_and_return_no_bytes(string field, string label)
    {
        var content = With(field, Malformed.Single(m => m.Label == label).Value);
        byte[]? managed = null;
        byte[]? export = null;

        var managedError = Assert.Throws<ArgumentException>(() => managed = Codec.WriteManaged(content));
        var exportError = Assert.Throws<ArgumentException>(() => export = Codec.WriteExport(content));

        // Nothing comes back to be written, and the refusal has the shape of every other content refusal: an
        // ArgumentException for the content, the strict encoder's own exception kept inside it.
        Assert.Null(managed);
        Assert.Null(export);
        Assert.Equal("content", managedError.ParamName);
        Assert.Equal("content", exportError.ParamName);
        Assert.IsType<EncoderFallbackException>(managedError.InnerException);
        Assert.IsType<EncoderFallbackException>(exportError.InnerException);
    }

    [Fact]
    public void A_surrogate_pair_round_trips_in_every_field()
    {
        TermValues[] rows = [new($"smile {Emoji}", Emoji), new(Clef, $"G clef {Clef}", WholeWord: false), new("plain", $"{Emoji}{Clef}")];
        var content = CsvTestData.Content($"Team {Emoji}", $"Music {Clef}", $"Faces {Emoji}, clefs {Clef}", rows, $"team-{Emoji}");

        var managed = Codec.WriteManaged(content);
        var export = Codec.WriteExport(content);
        var readManaged = Codec.ReadManaged(managed);
        var readExport = Codec.ReadImport(export);
        var legacy = Legacy043LibraryCsv.Parse(CsvTestData.ReadAllTextOf(managed));

        foreach (var read in new[] { readManaged, readExport })
        {
            Assert.Equal(content.Name, read.Name);
            Assert.Equal(content.Category, read.Category);
            Assert.Equal(content.Description, read.Description);
            Assert.Equal(content.BasedOn, read.BasedOn);
            Assert.Equal(rows, read.Terms);
            Assert.Empty(read.Errors);
        }

        Assert.Equal(content.Name, legacy.Name);
        Assert.Equal(rows, legacy.Entries.Select(TermValues.FromEntry));
        Assert.False(HoldsReplacementCharacter(managed), "the managed file holds U+FFFD");
        Assert.False(HoldsReplacementCharacter(export), "the export holds U+FFFD");
    }

    [Fact]
    public void A_write_succeeds_exactly_when_every_string_is_well_formed()
    {
        // Seeded property test. Each string is built from ordinary characters and whole surrogate pairs, and in about half
        // the cases one string then gets a lone high or low surrogate inserted somewhere, which can never pair up (the
        // counts of high and low surrogates then differ); the oracle is the definition in 2.2 either way.
        var random = new Random(20261005);
        var outcomes = new int[2];
        for (var i = 0; i < 3000; i++)
        {
            var strings = Enumerable.Range(0, 6).Select(_ => WellFormedString(random)).ToArray();
            if (random.Next(2) == 0)
            {
                var target = random.Next(strings.Length);
                var lone = random.Next(2) == 0 ? "\uD83D" : "\uDE00";
                strings[target] = strings[target].Insert(random.Next(strings[target].Length + 1), lone);
            }

            var content = CsvTestData.Content(strings[0], strings[1], strings[2], [new TermValues(strings[3], strings[4])], strings[5]);
            var wellFormed = strings.All(IsWellFormed);

            if (wellFormed)
            {
                var managed = Codec.ReadManaged(Codec.WriteManaged(content));
                var export = Codec.ReadImport(Codec.WriteExport(content));
                Assert.True(managed.Terms.SequenceEqual(content.Rows.Select(r => r.Values)), $"case {i}: managed rows");
                Assert.True(export.Terms.SequenceEqual(content.Rows.Select(r => r.Values)), $"case {i}: exported rows");
                Assert.True(managed.Name == content.Name.Trim() && export.Name == content.Name.Trim(), $"case {i}: name");
                Assert.True(managed.BasedOn == content.BasedOn!.Trim() && export.BasedOn == content.BasedOn.Trim(), $"case {i}: based-on");
            }
            else
            {
                Assert.Throws<ArgumentException>(() => Codec.WriteManaged(content));
                Assert.Throws<ArgumentException>(() => Codec.WriteExport(content));
            }

            outcomes[wellFormed ? 1 : 0]++;
        }

        Assert.True(outcomes[0] > 1000 && outcomes[1] > 1000, $"both outcomes reached: {outcomes[0]} refused, {outcomes[1]} written");
    }

    [Fact]
    public void The_readers_never_produce_an_unpaired_surrogate()
    {
        // Bytes that would decode to surrogate code units if a decoder let them through: UTF-16 with a lone high or low
        // surrogate, in both byte orders; UTF-32 with a surrogate value; UTF-8's encoding of a surrogate; and random bytes.
        byte[][] table =
        [
            [0xFF, 0xFE, 0x3D, 0xD8, 0x2C, 0x00, 0x61, 0x00, 0x0A, 0x00],
            [0xFF, 0xFE, 0x00, 0xDE, 0x2C, 0x00, 0x61, 0x00, 0x0A, 0x00],
            [0xFF, 0xFE, 0x61, 0x00, 0x2C, 0x00, 0x3D, 0xD8],
            [0xFE, 0xFF, 0xD8, 0x3D, 0x00, 0x2C, 0x00, 0x61],
            [0xFE, 0xFF, 0x00, 0x23, 0x00, 0x20, 0xDE, 0x00, 0x00, 0x0A, 0x00, 0x61, 0x00, 0x2C, 0xDE, 0x00],
            [0xFF, 0xFE, 0x00, 0x00, 0x00, 0xD8, 0x00, 0x00, 0x2C, 0x00, 0x00, 0x00, 0x61, 0x00, 0x00, 0x00],
            [0xED, 0xA0, 0xBD, 0x2C, 0x61, 0x0A],
            [0x23, 0x20, 0x6E, 0x61, 0x6D, 0x65, 0x3A, 0x20, 0xED, 0xB8, 0x80, 0x0A, 0x61, 0x2C, 0x62],
        ];
        const string alphabet = "\u0000\u000A\u0022\u0023\u002C\u003A\u0061\u00A0\u00B8\u00BD\u00D8\u00DC\u00DE\u00ED\u00FE\u00FF\u003D";
        var random = new Random(20261006);
        var cases = table.Concat(Enumerable.Range(0, 3000).Select(_ =>
        {
            var body = CsvTestData.Random(random, alphabet, 24).Select(c => (byte)c).ToArray();
            return random.Next(3) switch
            {
                0 => [0xFF, 0xFE, .. body],
                1 => [0xFE, 0xFF, .. body],
                _ => body,
            };
        }));

        foreach (var bytes in cases)
        {
            foreach (var document in new[] { Codec.ReadManaged(bytes), Codec.ReadImport(bytes), ShiftJisCodec.ReadImport(bytes) })
            {
                var strings = new[] { document.Name, document.Category, document.Description, document.BasedOn }
                    .Concat(document.Terms.SelectMany(t => new[] { t.Spoken, t.Written }))
                    .Concat(document.Errors.Select(e => e.Field));
                Assert.True(strings.All(s => s is null || IsWellFormed(s)), BitConverter.ToString(bytes));
            }
        }
    }

    private static LibraryContent With(string field, string value) =>
        CsvTestData.Content(
            field == "name" ? value : "Team",
            field == "category" ? value : "Custom",
            field == "description" ? value : "Words",
            [new TermValues(field == "spoken" ? value : "get hub", field == "written" ? value : "GitHub"), new TermValues("kube", "K8s")],
            field == "basedOn" ? value : "github");

    // Ordinary characters and whole surrogate pairs, never blank.
    private static string WellFormedString(Random random)
    {
        var value = string.Concat(Enumerable.Range(0, random.Next(1, 8)).Select(_ => random.Next(8) switch
        {
            0 => Emoji,
            1 => Clef,
            2 => "\uDBFF\uDFFF",
            _ => "ab ,#"[random.Next(5)].ToString(),
        }));
        return string.IsNullOrWhiteSpace(value) ? "x" + value : value;
    }

    // Every high surrogate immediately followed by a low one, and every low one immediately preceded by a high one.
    private static bool IsWellFormed(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]))
            {
                if (i + 1 == value.Length || !char.IsLowSurrogate(value[i + 1]))
                {
                    return false;
                }

                i++;
            }
            else if (char.IsLowSurrogate(value[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HoldsReplacementCharacter(byte[] bytes) =>
        bytes.AsSpan().IndexOf(new byte[] { 0xEF, 0xBF, 0xBD }) >= 0 || Encoding.UTF8.GetString(bytes).Contains('\uFFFD');
}
