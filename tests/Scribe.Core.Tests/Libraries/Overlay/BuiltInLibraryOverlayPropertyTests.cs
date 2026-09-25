using Scribe.Core.Libraries;
using Scribe.Core.PostProcessing;
using static Scribe.Core.Tests.Libraries.Overlay.OverlayTestData;

namespace Scribe.Core.Tests.Libraries.Overlay;

/// <summary>
/// O-6: any sequence of the row commands, across saves, loads and upgrades to other shipped versions, then collect,
/// write, read back and apply, reproduces exactly the rows the user saw; and restoring everything leaves no document and
/// the shipped library. Each command is also checked as it runs: an edit gives exactly the values asked for and never a
/// question about a field it changed.
/// </summary>
public sealed class BuiltInLibraryOverlayPropertyTests
{
    private static readonly string[] Pool =
    [
        "get hub", "git hub", "copilot", "octo cat", "gh cli", "kube", "k8s", "dot net", "c sharp", "azure", "entra",
        "zulu", "hub  spot",
    ];

    private static readonly string[] Writings =
    [
        "GitHub", "GitHub, Inc.", "Copilot", "GitHub Copilot", "Octocat", "GitHub CLI", "Kubernetes", "K8s", ".NET",
        "C#", "Azure", "Microsoft Entra", "Zulu", "", "multi\r\nline", "caf\u00E9 \uD83D\uDE00", "  padded  ",
    ];

    [Fact]
    public void Any_sequence_of_row_commands_saves_and_loads_back_to_the_rows_the_user_saw()
    {
        var random = new Random(20260925);
        var commands = new Dictionary<string, int>();
        for (var sequence = 0; sequence < 400; sequence++)
        {
            var world = new World(random, commands);
            for (var step = 0; step < 45; step++)
            {
                world.Step();
            }

            world.Save();
            world.RestoreEverything();
        }

        // Every command ran, and ran often, so a green run means something.
        foreach (var command in new[] { "edit", "typed back", "enabled", "restore", "add", "delete", "keep mine", "use updated", "save", "upgrade" })
        {
            Assert.True(commands.GetValueOrDefault(command) > 200, $"{command} ran {commands.GetValueOrDefault(command)} times");
        }
    }

    private sealed class World(Random random, Dictionary<string, int> commands)
    {
        private DictionaryLibrary _shipped = RandomVersion(random);
        private BuiltInLibraryEdits? _committed;
        private IReadOnlyList<LibraryRow>? _rows;

        private IReadOnlyList<LibraryRow> Rows => _rows ??= BuiltInOverlay.Apply(_shipped, _committed);

        internal void Step()
        {
            var roll = random.Next(100);
            if (roll < 30)
            {
                Edit();
            }
            else if (roll < 42)
            {
                SetEnabled();
            }
            else if (roll < 50)
            {
                Restore();
            }
            else if (roll < 60)
            {
                Add();
            }
            else if (roll < 66)
            {
                Delete();
            }
            else if (roll < 78)
            {
                Resolve();
            }
            else if (roll < 90)
            {
                Save();
            }
            else
            {
                Upgrade();
            }
        }

        internal void Save()
        {
            Count("save");
            var (stored, reloaded) = SaveAndReload(_shipped, _committed, Rows);
            AssertSameRows(Rows, reloaded, "saved and loaded back");
            if (stored is not null)
            {
                // A document Scribe wrote is collected back unchanged from its own rows.
                AssertSameDocument(stored, BuiltInOverlay.Collect(_shipped, stored, reloaded));
            }

            _committed = stored;
            _rows = reloaded;
        }

        internal void RestoreEverything()
        {
            var restored = Rows.Select(row => BuiltInOverlay.RestoreShipped(row)).Where(row => row is not null).Select(row => row!).ToArray();
            var shippedKeys = BuiltInOverlay.Apply(_shipped, null).Select(row => row.Key).ToHashSet();

            var collected = BuiltInOverlay.Collect(_shipped, _committed, restored);

            // What stays is exactly the off intents whose rows this version does not ship; they show nothing.
            var inert = _committed?.Terms.Where(term => term.Intent == BuiltInTermIntent.Off && !shippedKeys.Contains(term.Key)).ToArray() ?? [];
            if (inert.Length == 0)
            {
                Assert.Null(collected);
            }
            else
            {
                AssertSameDocument(new BuiltInLibraryEdits(_shipped.Id, inert), collected);
            }

            var asShipped = BuiltInOverlay.Apply(_shipped, null);
            AssertSameRows(asShipped, BuiltInOverlay.Apply(_shipped, RoundTrip(collected, _shipped.Id)));
            AssertSameRows(asShipped, restored);
        }

        private void Edit()
        {
            var row = Pick();
            TermValues values;
            if (row.Shipped is not null && random.Next(6) == 0)
            {
                Count("typed back");
                values = row.Shipped;
            }
            else
            {
                Count("edit");
                values = new TermValues(
                    random.Next(3) == 0 ? RandomSpoken(row) : row.Values.Spoken,
                    random.Next(2) == 0 ? Writings[random.Next(Writings.Length)] : row.Values.Written,
                    random.Next(5) == 0 ? !row.Values.WholeWord : row.Values.WholeWord,
                    random.Next(6) == 0 ? !row.Values.Enabled : row.Values.Enabled);
            }

            var edited = BuiltInOverlay.Edit(row, values);

            Assert.Equal(values, edited.Values);
            Assert.Equal(row.Key, edited.Key);
            var changed = Changed(row.Values, values);
            Assert.Equal((row.Review?.Differing ?? TermFields.None) & ~changed, edited.Review?.Differing ?? TermFields.None);
            if (edited.Origin == TermOrigin.Shipped && values != row.Values)
            {
                // Only turning an off row back on makes a row shipped again; any other change is the user's.
                Assert.Equal(BuiltInTermIntent.Off, row.Edit?.Intent);
                Assert.Equal(row.Shipped, values);
            }

            Replace(row, edited);
        }

        private void SetEnabled()
        {
            Count("enabled");
            var row = Pick();
            var enabled = random.Next(2) == 0;

            var changed = BuiltInOverlay.SetEnabled(row, enabled);

            Assert.Equal(row.Values with { Enabled = enabled }, changed.Values);
            Replace(row, changed);
        }

        private void Restore()
        {
            Count("restore");
            var row = Pick();

            var restored = BuiltInOverlay.RestoreShipped(row);

            if (restored is null)
            {
                Assert.Null(row.Shipped);
                Remove(row);
            }
            else
            {
                Assert.Equal(TermOrigin.Shipped, restored.Origin);
                Assert.Equal(row.Shipped, restored.Values);
                Replace(row, restored);
            }
        }

        private void Add()
        {
            Count("add");
            var keys = Rows.Select(row => row.Key).ToHashSet();
            var spoken = random.Next(3) == 0 ? RandomText() : Pool[random.Next(Pool.Length)];
            if (K(spoken).IsEmpty || keys.Contains(K(spoken)))
            {
                return;
            }

            var added = BuiltInOverlay.Add(new TermValues(spoken, Writings[random.Next(Writings.Length)], random.Next(2) == 0, random.Next(4) != 0));

            _rows = [.. Rows, added];
        }

        private void Delete()
        {
            // Delete term exists for rows this version does not ship (added, no longer shipped); an addition the version
            // ships is taken back with Restore built-in values instead.
            var deletable = Rows.Where(row => row.Shipped is null).ToArray();
            if (deletable.Length == 0)
            {
                return;
            }

            Count("delete");
            Remove(deletable[random.Next(deletable.Length)]);
        }

        private void Resolve()
        {
            var asking = Rows.Where(row => row.Review is not null).ToArray();
            if (asking.Length == 0)
            {
                // Make one: an upgrade that changes what the user wrote.
                Upgrade();
                return;
            }

            var row = asking[random.Next(asking.Length)];
            var choice = random.Next(2) == 0 ? TermReviewChoice.KeepMine : TermReviewChoice.UseUpdated;
            Count(choice == TermReviewChoice.KeepMine ? "keep mine" : "use updated");

            var resolved = BuiltInOverlay.ResolveReview(row, choice);

            Assert.Null(resolved.Review);
            Assert.Equal(choice == TermReviewChoice.KeepMine ? row.Values : row.Shipped, resolved.Values);
            Replace(row, resolved);
        }

        private void Upgrade()
        {
            Save();
            Count("upgrade");
            _shipped = RandomVersion(random, _shipped);
            _rows = null;
        }

        private LibraryRow Pick() => Rows[random.Next(Rows.Count)];

        private void Replace(LibraryRow before, LibraryRow after) => _rows = OverlayTestData.Replace(Rows, before, after);

        private void Remove(LibraryRow row) => _rows = OverlayTestData.Replace(Rows, row, null);

        private void Count(string command) => commands[command] = commands.GetValueOrDefault(command) + 1;

        private string RandomSpoken(LibraryRow row) => random.Next(4) switch
        {
            0 => row.Key.Value.ToUpperInvariant(),
            1 => Pool[random.Next(Pool.Length)],
            2 => RandomText(),
            _ => row.Values.Spoken + " x",
        };

        private string RandomText()
        {
            string[] pieces = ["a", "b", " ", "  ", "\t", "\u00A0", "\u00E9", "\uD83D\uDE00", "\"", ",", "#", "Z"];
            var length = random.Next(1, 8);
            var text = string.Empty;
            while (text.Length < length)
            {
                text += pieces[random.Next(pieces.Length)];
            }

            return text;
        }

        private static TermFields Changed(TermValues before, TermValues after) =>
            (before.Spoken == after.Spoken ? TermFields.None : TermFields.Spoken) |
            (before.Written == after.Written ? TermFields.None : TermFields.Written) |
            (before.WholeWord == after.WholeWord ? TermFields.None : TermFields.WholeWord) |
            (before.Enabled == after.Enabled ? TermFields.None : TermFields.Enabled);

        // A shipped version: most of the pool, in pool order, each row's values varying between versions the way real
        // updates do (a correction to Written, WholeWord flipped, a spoken form recased, rarely a row shipped off).
        private static DictionaryLibrary RandomVersion(Random random, DictionaryLibrary? previous = null)
        {
            var rows = new List<TermValues>();
            foreach (var spoken in Pool)
            {
                var before = previous?.Entries.FirstOrDefault(entry => K(entry.Pattern) == K(spoken));
                if (random.Next(10) < 3)
                {
                    continue;
                }

                if (before is not null && random.Next(3) != 0)
                {
                    rows.Add(TermValues.FromEntry(before));
                    continue;
                }

                rows.Add(new TermValues(
                    random.Next(5) == 0 ? spoken.ToUpperInvariant() : spoken,
                    Writings[random.Next(Writings.Length)],
                    random.Next(4) != 0,
                    random.Next(20) != 0));
            }

            return Shipped("github", [.. rows]);
        }
    }
}
