using System.Text.RegularExpressions;
using Scribe.Core.Settings;

namespace Scribe.Core.Tests;

public partial class AppDisplayNameMemoryTests
{
    public static TheoryData<string?> NamedCases => new()
    {
        null, string.Empty, " ", "\t", "\u00A0", ".exe", ".exe.exe", " .exe ", "exe", ".EXE",
        "OUTLOOK", "outlook", "OUTLOOK.EXE", "outlook.exe", "OUTLOOK.exe.exe", "OUTLOOK .exe", "  OUTLOOK  ",
        "\u3000OUTLOOK\u3000", "OUTLOOK\u0085.exe", "OUTLOOK\t.exe.exe", "olk", "OLK.exe", "olk .exe .exe",
        "ms-teams", "MS-TEAMS.EXE", "ms-teams .exe", "ms-teams\t.exe", "ms-teams  .exe", "msteams.exe.exe", "Teams",
        "teams.exe", "TEAMS", "Teams.exe.exe.exe", "WindowsTerminal", "windowsterminal.EXE", "wt", "wt.exe.exe.exe",
        "w t", "w  t", "winword", "WINWORD.exe", "word", "Word .exe", "notepad.exe.exe", "Code", "code.exe", "devenv",
        "Some App", "Some  App", "Some \t App.exe", "a\u00A0b", "a\u2028b", "a \u00A0b", "unknown-app.exe",
        "unknown-app.exe.exe", "x.EXE.exe", "\u0131ms-teams", "MS-TEAMS\u0130", "\u212Aey", "olk\r\n", "\uD83D\uDE00",
    };

    private static readonly (Oracle.Group Previous, AppProgramGroup Current)[] Groups = PairGroups();

    [Theory]
    [MemberData(nameof(NamedCases))]
    public void Named_cases_give_what_the_previous_implementation_gave(string? processName) =>
        AssertSameAsOracle(processName);

    [Fact]
    public void Every_code_unit_in_every_position_gives_what_the_previous_implementation_gave()
    {
        // Leading, trailing, inside, doubled inside, exposed by the ".exe" strip, and alone: every place where trimming,
        // the strip and the white-space collapse decide, with every UTF-16 code unit, lone surrogates included.
        for (var code = 0; code <= char.MaxValue; code++)
        {
            var c = (char)code;
            AssertSameAsOracle($"a{c}b");
            AssertSameAsOracle($"a{c}{c}b");
            AssertSameAsOracle($"{c}olk");
            AssertSameAsOracle($"olk{c}");
            AssertSameAsOracle($"olk{c}.exe");
            AssertSameAsOracle($"ms-teams{c}.exe.exe");
            AssertSameAsOracle(c.ToString());
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("OUTLOOK")]
    [InlineData(" olk .exe ")]
    public void A_group_without_a_program_list_fails_as_it_did(string? processName)
    {
        // Programs is non-nullable, but a record can still be built, or copied, with null.
        var expected = Assert.Throws<ArgumentNullException>(() => new Oracle.Group("k", "Name", null!).Contains(processName));
        var expectedCopy = Assert.Throws<ArgumentNullException>(
            () => (Oracle.AllGroups[0] with { Programs = null! }).Contains(processName));
        var constructed = Assert.Throws<ArgumentNullException>(
            () => new AppProgramGroup("k", "Name", null!).Contains(processName));
        var copied = Assert.Throws<ArgumentNullException>(
            () => (Groups[0].Current with { Programs = null! }).Contains(processName));

        Assert.Equal("source", expected.ParamName);
        foreach (var actual in new[] { expectedCopy, constructed, copied })
        {
            Assert.Equal(expected.ParamName, actual.ParamName);
            Assert.Equal(expected.Message, actual.Message);
        }
    }

    private static void AssertSameAsOracle(string? processName)
    {
        Assert.Equal(Oracle.NormalizeProcessName(processName), AppDisplayName.NormalizeProcessName(processName));
        Assert.Equal(Oracle.For(processName), AppDisplayName.For(processName));
        Assert.Equal(Oracle.GroupKeyFor(processName), AppDisplayName.GroupKeyFor(processName));
        Assert.Equal(Oracle.GroupMembersFor(processName), AppDisplayName.GroupMembersFor(processName));
        Assert.Equal(Oracle.GroupFor(processName)?.Key, AppDisplayName.GroupFor(processName)?.Key);

        foreach (var (previous, current) in Groups)
        {
            Assert.Equal(previous.Contains(processName), current.Contains(processName));
        }
    }

    private static (Oracle.Group Previous, AppProgramGroup Current)[] PairGroups()
    {
        var pairs = new (Oracle.Group Previous, AppProgramGroup Current)[Oracle.AllGroups.Count];
        for (var i = 0; i < pairs.Length; i++)
        {
            var previous = Oracle.AllGroups[i];
            var current = AppDisplayName.GroupFor(previous.Programs[0]);
            if (current is null || current.Key != previous.Key || current.DisplayName != previous.DisplayName ||
                !current.Programs.SequenceEqual(previous.Programs))
            {
                throw new InvalidOperationException($"The groups differ at {previous.Key}.");
            }

            pairs[i] = (previous, current);
        }

        return pairs;
    }

    // In the collection that runs alone: no other test allocates on this thread while it measures.
    [Collection(AllocationMeasurementCollection.Name)]
    public sealed class Allocations
    {
        private const int Rounds = 1_000;
        private static readonly string[] NormalNames = ["WINWORD", "ms-teams", "OUTLOOK", "wt", "unknown-app", "Code", "slack", "olk"];
        private static readonly string[] GroupMembers = ["ms-teams", "OUTLOOK", "wt", "olk", "Teams"];

        [Fact]
        public void Naming_a_process_whose_name_needs_no_normalizing_no_longer_allocates()
        {
            // Every history row, usage entry and profile chip is named this way; the previous implementation built LINQ
            // closures and enumerators for each group on every call.
            Pass();
            OraclePass();
            _ = RuntimeWork.Now().Since(RuntimeWork.Now());

            var oracleBefore = GC.GetAllocatedBytesForCurrentThread();
            OraclePass();
            var oracleBytes = GC.GetAllocatedBytesForCurrentThread() - oracleBefore;

            var work = RuntimeWork.Now();
            var before = GC.GetAllocatedBytesForCurrentThread();
            Pass();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            var during = RuntimeWork.Now().Since(work);

            // A bound, not zero: NormalizeProcessName's Regex.Replace holds its parsed replacement through a weak reference, so
            // a collection inside the window (another thread's) makes the next call parse it again, once.
            Assert.True(
                allocated <= 2048,
                $"{allocated} bytes for {Rounds} rounds of {(NormalNames.Length * 3) + (GroupMembers.Length * 2)} calls (the " +
                $"previous implementation: {oracleBytes}). During it: {during}.");
            Assert.True(oracleBytes > 100 * 2048, $"The previous implementation allocated only {oracleBytes} bytes.");
        }

        private static void Pass()
        {
            for (var round = 0; round < Rounds; round++)
            {
                foreach (var name in NormalNames)
                {
                    _ = AppDisplayName.For(name);
                    _ = AppDisplayName.GroupKeyFor(name);
                    _ = AppDisplayName.GroupFor(name);
                }

                foreach (var name in GroupMembers)
                {
                    _ = AppDisplayName.GroupMembersFor(name);
                    _ = AppDisplayName.GroupFor(name)!.Contains(name);
                }
            }
        }

        private static void OraclePass()
        {
            for (var round = 0; round < Rounds; round++)
            {
                foreach (var name in NormalNames)
                {
                    _ = Oracle.For(name);
                    _ = Oracle.GroupKeyFor(name);
                    _ = Oracle.GroupFor(name);
                }

                foreach (var name in GroupMembers)
                {
                    _ = Oracle.GroupMembersFor(name);
                    _ = Oracle.GroupFor(name)!.Contains(name);
                }
            }
        }
    }

    // AppDisplayName and AppProgramGroup as they were before the allocation fix, verbatim apart from the type names, so every
    // result above is compared with what the previous implementation returned for the same input.
    private static partial class Oracle
    {
        public sealed record Group(string Key, string DisplayName, IReadOnlyList<string> Programs)
        {
            public bool Contains(string? processName)
            {
                var normalized = NormalizeProcessName(processName);
                return Programs.Any(program => string.Equals(program, normalized, StringComparison.OrdinalIgnoreCase));
            }
        }

        private static readonly IReadOnlyList<Group> Groups =
        [
            new("outlook", "Outlook", ["OUTLOOK"]),
            new("new-outlook", "New Outlook", ["olk"]),
            new("teams", "Teams", ["ms-teams", "Teams", "msteams"]),
            new("terminal", "Terminal", ["WindowsTerminal", "wt"]),
        ];

        private static readonly IReadOnlyDictionary<string, string> Known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["winword"] = "Word",
            ["word"] = "Word",
            ["excel"] = "Excel",
            ["powerpnt"] = "PowerPoint",
            ["powerpoint"] = "PowerPoint",
            ["onenote"] = "OneNote",
            ["msedge"] = "Edge",
            ["edge"] = "Edge",
            ["chrome"] = "Chrome",
            ["firefox"] = "Firefox",
            ["code"] = "VS Code",
            ["devenv"] = "Visual Studio",
            ["notepad"] = "Notepad",
            ["slack"] = "Slack",
            ["discord"] = "Discord",
            ["zoom"] = "Zoom",
        };

        public static IReadOnlyList<Group> AllGroups => Groups;

        public static string For(string? processName)
        {
            var normalized = NormalizeProcessName(processName);
            if (normalized.Length == 0)
            {
                return string.Empty;
            }

            var group = GroupFor(normalized);
            if (group is not null)
            {
                return group.DisplayName;
            }

            return Known.TryGetValue(normalized, out var display) ? display : normalized;
        }

        public static Group? GroupFor(string? processName)
        {
            var normalized = NormalizeProcessName(processName);
            return normalized.Length == 0 ? null : Groups.FirstOrDefault(group => group.Contains(normalized));
        }

        public static string GroupKeyFor(string? processName)
        {
            var normalized = NormalizeProcessName(processName);
            return GroupFor(normalized)?.Key ?? normalized;
        }

        public static IReadOnlyList<string> GroupMembersFor(string? processName)
        {
            var normalized = NormalizeProcessName(processName);
            var group = GroupFor(normalized);
            return group is null ? [normalized] : group.Programs;
        }

        public static string NormalizeProcessName(string? processName)
        {
            var value = (processName ?? string.Empty).Trim();
            if (value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                value = value[..^4];
            }

            return MultipleWhitespace().Replace(value, " ");
        }

        [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
        private static partial Regex MultipleWhitespace();
    }
}
