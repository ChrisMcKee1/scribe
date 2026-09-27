using Scribe.Core.Overlay;

namespace Scribe.Core.Tests;

/// <summary>
/// Scripted orders of a position preview racing the engine. The <see cref="Pill"/> driver queues commands
/// the way the overlay client does (engine commands supersede first, then queue; preview steps carry their
/// generation) and takes them in queue order the way its consumer does, tracking what the helper was told
/// and which anchor it ends up showing.
/// </summary>
public sealed class OverlayPreviewGateTests
{
    [Fact]
    public void A_preview_that_runs_to_its_end_is_delivered_whole_and_needs_no_restore()
    {
        var pill = new Pill();
        var preview = pill.StartPreview("TopLeft");
        pill.Step(preview, "METER 500");
        pill.End(preview);
        pill.TakeAll();

        Assert.Equal(["POSITION TopLeft", "RECORDING", "METER 500", "HIDE", "POSITION applied"], pill.Written);
        Assert.False(pill.ShowsCandidate);

        pill.Engine("RECORDING");
        pill.TakeAll();
        Assert.Equal("RECORDING", pill.Written[^1]);
        Assert.Equal(6, pill.Written.Count);
    }

    [Fact]
    public void An_end_queued_after_a_newer_recording_is_dropped_and_the_recording_restores_the_anchor()
    {
        // The race: the sweep passes its last generation check, a recording starts and queues RECORDING,
        // then the sweep queues its end. Delivered, that end would hide the new recording's pill.
        var pill = new Pill();
        var preview = pill.StartPreview("TopLeft");
        pill.TakeAll();

        Assert.True(pill.Gate.IsCurrent(preview)); // the sweep's last check passes
        pill.Engine("RECORDING");
        pill.End(preview);
        pill.TakeAll();

        Assert.Equal(["POSITION TopLeft", "RECORDING", "POSITION applied", "RECORDING"], pill.Written);
        Assert.Equal(1, pill.Dropped);
        Assert.False(pill.ShowsCandidate);
        Assert.False(pill.Hidden);
    }

    [Fact]
    public void A_preview_end_queued_first_but_taken_after_the_supersede_is_dropped_too()
    {
        // Staleness is judged when the consumer takes a step, not when it was queued.
        var pill = new Pill();
        var preview = pill.StartPreview("TopLeft");
        pill.TakeAll();

        pill.End(preview);
        pill.Engine("RECORDING");
        pill.TakeAll();

        Assert.Equal(["POSITION TopLeft", "RECORDING", "POSITION applied", "RECORDING"], pill.Written);
        Assert.False(pill.Hidden);
    }

    [Fact]
    public void A_preview_end_taken_before_the_recording_starts_is_delivered_normally()
    {
        var pill = new Pill();
        var preview = pill.StartPreview("TopLeft");
        pill.End(preview);
        pill.TakeAll();

        pill.Engine("RECORDING");
        pill.TakeAll();

        Assert.Equal(["POSITION TopLeft", "RECORDING", "HIDE", "POSITION applied", "RECORDING"], pill.Written);
        Assert.False(pill.Hidden);
    }

    [Fact]
    public void A_preview_superseded_mid_sweep_is_undone_by_the_next_engine_command()
    {
        // The sweep sees the newer generation at its next check and queues nothing more.
        var pill = new Pill();
        var preview = pill.StartPreview("BottomRight");
        pill.Step(preview, "METER 300");
        pill.TakeAll();

        pill.Engine("PROCESSING 0");
        Assert.False(pill.Gate.IsCurrent(preview));
        pill.TakeAll();

        Assert.Equal(["POSITION BottomRight", "RECORDING", "METER 300", "POSITION applied", "PROCESSING 0"], pill.Written);
        Assert.False(pill.ShowsCandidate);
    }

    [Fact]
    public void Steps_queued_before_a_superseding_command_and_still_queued_are_dropped()
    {
        var pill = new Pill();
        var preview = pill.StartPreview("TopLeft");
        pill.Step(preview, "METER 200");
        pill.Step(preview, "METER 400");
        pill.Engine("HIDE"); // the engine hides before the consumer reached the preview at all
        pill.TakeAll();

        Assert.Equal(["HIDE"], pill.Written);
        Assert.Equal(4, pill.Dropped);
        Assert.False(pill.ShowsCandidate);
    }

    [Fact]
    public void A_newer_preview_drops_the_older_ones_steps_without_restoring_in_between()
    {
        var pill = new Pill();
        var first = pill.StartPreview("TopLeft");
        pill.TakeAll();

        var second = pill.StartPreview("TopRight");
        pill.Step(first, "METER 100"); // the first sweep passed its check before the second preview began
        pill.Step(second, "METER 700");
        pill.TakeAll();

        Assert.Equal(["POSITION TopLeft", "RECORDING", "POSITION TopRight", "RECORDING", "METER 700"], pill.Written);
        Assert.True(pill.ShowsCandidate);

        pill.End(second);
        pill.TakeAll();
        Assert.False(pill.ShowsCandidate);
        Assert.Equal("POSITION applied", pill.Written[^1]);
    }

    [Fact]
    public void Engine_commands_with_no_preview_behind_them_are_delivered_as_they_are()
    {
        var pill = new Pill();
        pill.Engine("RECORDING");
        pill.Engine("PROCESSING 1");
        pill.Engine("HIDE");
        pill.TakeAll();

        Assert.Equal(["RECORDING", "PROCESSING 1", "HIDE"], pill.Written);
    }

    [Fact]
    public void A_superseded_preview_is_restored_once()
    {
        var pill = new Pill();
        var preview = pill.StartPreview("TopCenter");
        pill.TakeAll();

        pill.Engine("POSITION BottomLeft"); // a settings save: the applied anchor itself moves
        pill.Engine("HIDE");
        pill.TakeAll();

        Assert.Equal(["POSITION TopCenter", "RECORDING", "POSITION applied", "POSITION BottomLeft", "HIDE"], pill.Written);
        Assert.False(pill.Gate.IsCurrent(preview));
    }

    // ---- Superseded after the gate passed it (Astra's A4) --------------------------------------------------------------

    [Fact]
    public void A_preview_step_superseded_after_the_gate_passed_it_is_not_written_and_the_engine_restores_the_anchor()
    {
        // The recording look passes the gate, then the client's Prepare blocks in a launch while the engine asks for
        // processing. Written after that, the look would put the pill back to Listening over Processing.
        var pill = new Pill();
        pill.StartPreview("TopLeft");
        pill.TakeNext();
        pill.TakeNext(beforeWrite: () => pill.Engine("PROCESSING 0"));
        pill.TakeAll();

        Assert.Equal(["POSITION TopLeft", "POSITION applied", "PROCESSING 0"], pill.Written);
        Assert.Equal(1, pill.Skipped);
        Assert.False(pill.ShowsCandidate);
    }

    [Fact]
    public void A_preview_anchor_superseded_after_the_gate_passed_it_is_not_written()
    {
        // Astra's simpler variant: the candidate anchor's own launch gives the new helper the applied anchor, and the
        // engine asks for a recording meanwhile. The candidate must not follow; the look behind it is dropped.
        var pill = new Pill();
        pill.StartPreview("TopLeft");
        pill.TakeNext(beforeWrite: () => pill.Engine("RECORDING"));
        pill.TakeAll();

        Assert.DoesNotContain("POSITION TopLeft", pill.Written);
        Assert.Equal(["POSITION applied", "RECORDING"], pill.Written); // the restore is redundant here, and harmless
        Assert.False(pill.ShowsCandidate);
    }

    [Fact]
    public void A_preview_end_superseded_after_the_gate_passed_it_leaves_the_restore_to_the_engine_command()
    {
        // The end would have written the applied anchor; skipped, it leaves that to the command that superseded it, or
        // the candidate anchor would stay under the new recording.
        var pill = new Pill();
        var preview = pill.StartPreview("TopLeft");
        pill.TakeAll();
        pill.End(preview);
        pill.TakeNext(beforeWrite: () => pill.Engine("RECORDING"));
        pill.TakeAll();

        Assert.Equal(["POSITION TopLeft", "RECORDING", "POSITION applied", "RECORDING"], pill.Written);
        Assert.Equal(1, pill.Skipped);
        Assert.False(pill.ShowsCandidate);
        Assert.False(pill.Hidden);
    }

    [Fact]
    public void Only_a_superseded_preview_command_is_turned_away_at_the_write_and_only_an_end_owes_a_restore()
    {
        var gate = new OverlayPreviewGate();
        Assert.True(gate.ConfirmWrite(OverlayPreviewRole.None, 0));

        // A preview still current is written whole, and an end that was written leaves nothing to restore.
        var preview = gate.BeginPreview();
        Assert.Equal(OverlayPreviewVerdict.Deliver, gate.OnCommand(OverlayPreviewRole.Anchor, preview));
        Assert.True(gate.ConfirmWrite(OverlayPreviewRole.Anchor, preview));
        Assert.Equal(OverlayPreviewVerdict.Deliver, gate.OnCommand(OverlayPreviewRole.End, preview));
        Assert.True(gate.ConfirmWrite(OverlayPreviewRole.End, preview));
        Assert.True(gate.ConfirmWrite(OverlayPreviewRole.None, 0));
        Assert.Equal(OverlayPreviewVerdict.Deliver, gate.OnCommand(OverlayPreviewRole.None, 0));

        // A step turned away after the gate moved nothing, so it leaves nothing to restore either.
        var second = gate.BeginPreview();
        Assert.Equal(OverlayPreviewVerdict.Deliver, gate.OnCommand(OverlayPreviewRole.Step, second));
        gate.Supersede();
        Assert.False(gate.ConfirmWrite(OverlayPreviewRole.Step, second));
        Assert.True(gate.ConfirmWrite(OverlayPreviewRole.None, 0));
        Assert.Equal(OverlayPreviewVerdict.Deliver, gate.OnCommand(OverlayPreviewRole.None, 0));

        // An end turned away after the gate owes the anchor its write would have put back.
        var third = gate.BeginPreview();
        Assert.Equal(OverlayPreviewVerdict.Deliver, gate.OnCommand(OverlayPreviewRole.End, third));
        var fourth = gate.BeginPreview(); // a newer preview supersedes it as an engine command would
        Assert.False(gate.ConfirmWrite(OverlayPreviewRole.End, third));
        Assert.True(gate.IsCurrent(fourth));
        Assert.Equal(OverlayPreviewVerdict.RestoreAnchorThenDeliver, gate.OnCommand(OverlayPreviewRole.None, 0));
    }

    /// <summary>The client's queue, its consumer, and what the helper was told.</summary>
    private sealed class Pill
    {
        private readonly Queue<(OverlayPreviewRole Role, long Generation, string Line)> _queue = new();

        public OverlayPreviewGate Gate { get; } = new();

        public List<string> Written { get; } = [];

        public int Dropped { get; private set; }

        /// <summary>Commands the gate passed that were turned away at the write, after something newer superseded them.</summary>
        public int Skipped { get; private set; }

        public bool ShowsCandidate { get; private set; }

        public bool Hidden { get; private set; }

        /// <summary>Preview(): begin, then queue the candidate anchor and the recording look.</summary>
        public long StartPreview(string candidate)
        {
            var generation = Gate.BeginPreview();
            _queue.Enqueue((OverlayPreviewRole.Anchor, generation, "POSITION " + candidate));
            _queue.Enqueue((OverlayPreviewRole.Step, generation, "RECORDING"));
            return generation;
        }

        public void Step(long generation, string line) => _queue.Enqueue((OverlayPreviewRole.Step, generation, line));

        public void End(long generation) => _queue.Enqueue((OverlayPreviewRole.End, generation, string.Empty));

        /// <summary>Any engine state command: supersede, then queue.</summary>
        public void Engine(string line)
        {
            Gate.Supersede();
            _queue.Enqueue((OverlayPreviewRole.None, 0, line));
        }

        public void TakeAll()
        {
            while (_queue.Count > 0)
            {
                TakeNext();
            }
        }

        /// <summary>
        /// Takes one command as the client's consumer does. <paramref name="beforeWrite"/> runs once the gate has passed it
        /// and before its write, where the client's Prepare can block in a launch while the engine asks for something
        /// newer; the command is judged again there (<see cref="OverlayPreviewGate.ConfirmWrite"/>).
        /// </summary>
        public void TakeNext(Action? beforeWrite = null)
        {
            var command = _queue.Dequeue();
            var verdict = Gate.OnCommand(command.Role, command.Generation);
            if (verdict == OverlayPreviewVerdict.Drop)
            {
                Dropped++;
                return;
            }

            beforeWrite?.Invoke();
            if (!Gate.ConfirmWrite(command.Role, command.Generation))
            {
                Skipped++;
                return;
            }

            if (verdict == OverlayPreviewVerdict.RestoreAnchorThenDeliver)
            {
                Write("POSITION applied", candidate: false);
            }

            Deliver(command.Role, command.Line);
        }

        private void Deliver(OverlayPreviewRole role, string line)
        {
            if (role == OverlayPreviewRole.End)
            {
                // The client's end for a pill the engine keeps hidden: hide, then move back while hidden.
                Write("HIDE", candidate: false);
                Write("POSITION applied", candidate: false);
                return;
            }

            Write(line, candidate: role == OverlayPreviewRole.Anchor);
        }

        private void Write(string line, bool candidate)
        {
            Written.Add(line);
            if (line.StartsWith("POSITION ", StringComparison.Ordinal))
            {
                ShowsCandidate = candidate;
            }
            else if (!line.StartsWith("METER ", StringComparison.Ordinal))
            {
                Hidden = line == "HIDE";
            }
        }
    }
}
