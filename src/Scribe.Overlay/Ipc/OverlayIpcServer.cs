using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Scribe.Overlay.Logging;

namespace Scribe.Overlay.Ipc;

/// <summary>
/// Named-pipe server that receives newline-delimited commands from the Scribe (WPF) engine and
/// drives the overlay window. The overlay is a kept-warm child process; when the pipe closes (the
/// engine exited or crashed) the server reports a disconnect so the process can exit cleanly rather
/// than orphan itself. METER commands are high-frequency and intentionally not logged.
/// </summary>
internal sealed class OverlayIpcServer : IDisposable
{
    private static readonly TimeSpan InitialConnectionTimeout = TimeSpan.FromSeconds(12);
    private readonly string _pipeName;
    private readonly OverlayWindow _window;
    private readonly Action _onDisconnected;
    private readonly CancellationTokenSource _cts = new();

    public OverlayIpcServer(string pipeName, OverlayWindow window, Action onDisconnected)
    {
        _pipeName = pipeName;
        _window = window;
        _onDisconnected = onDisconnected;
    }

    public void Start()
    {
        _ = Task.Run(() => RunAsync(_cts.Token));
        OverlayLog.Write($"OverlayIpcServer.Start pipe='{_pipeName}'");
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            using var server = new NamedPipeServerStream(
                _pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

            OverlayLog.Write("OverlayIpcServer waiting for connection");
            using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectionCts.CancelAfter(InitialConnectionTimeout);
            try
            {
                await server.WaitForConnectionAsync(connectionCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                OverlayLog.Warn("OverlayIpcServer connection timed out; exiting orphaned helper");
                return;
            }

            OverlayLog.Write("OverlayIpcServer client connected");

            using var reader = new StreamReader(server, Encoding.UTF8);
            string? line;
            while (!ct.IsCancellationRequested
                   && (line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
            {
                Dispatch(line);
            }

            OverlayLog.Write("OverlayIpcServer pipe closed (client gone)");
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            OverlayLog.Error("OverlayIpcServer loop failed", ex);
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                _onDisconnected();
            }
        }
    }

    // The verbs are Scribe.Core.Overlay.OverlayPipeProtocol's, written here as literals because the overlay has no
    // reference to Scribe.Core; OverlayPipeProtocolTests keeps this switch and that list equal.
    private void Dispatch(string line)
    {
        if (TryDispatchMeter(line))
        {
            return;
        }

        var trimmed = line.Trim();
        if (trimmed.Length == 0)
        {
            return;
        }

        var sp = trimmed.IndexOf(' ');
        var cmd = sp < 0 ? trimmed : trimmed[..sp];
        var arg = sp < 0 ? string.Empty : trimmed[(sp + 1)..];

        switch (cmd.ToUpperInvariant())
        {
            case "RECORDING":
                _window.ShowRecording();
                break;
            case "WARNING":
                _window.ShowRecordingWarning(arg);
                break;
            case "PROCESSING":
                _window.ShowProcessing(arg.Trim() == "1");
                break;
            case "TYPED":
                _window.ShowOutcome(OverlayState.Typed, null);
                break;
            case "TYPEDWITHOUTCLEANUP":
                _window.ShowOutcome(OverlayState.TypedWithoutCleanup, arg);
                break;
            case "NOTHINGTYPED":
                _window.ShowOutcome(OverlayState.NothingTyped, arg);
                break;
            case "PARTLYTYPED":
                _window.ShowOutcome(OverlayState.PartlyTyped, arg);
                break;
            case "HIDE":
                _window.Hide();
                break;
            case "METER":
                DispatchMeter(arg);
                break;
            case "POSITION":
                if (Enum.TryParse<OverlayAnchor>(arg.Trim(), ignoreCase: true, out var anchor))
                {
                    _window.SetAnchor(anchor);
                }
                else
                {
                    OverlayLog.Warn($"OverlayIpcServer POSITION with unknown anchor '{arg}'");
                }
                break;
            case "WARMUP":
                break; // the window is already constructed and warm
            case "EXIT":
                OverlayLog.Write("OverlayIpcServer EXIT received");
                _onDisconnected();
                break;
            default:
                OverlayLog.Warn($"OverlayIpcServer unknown command '{cmd}'");
                break;
        }
    }

    // METER arrives up to 40 times a second while recording, so it is read on the line's own characters, without the two
    // substrings Dispatch cuts from other lines. It takes exactly the lines the switch reads as METER: trimmed as
    // string.Trim trims, a verb equal to "METER" ignoring case is one ToUpperInvariant makes "METER" (no character outside
    // ASCII upper-cases to M, E, T or R), and the level is parsed the same way. So no line reaches the switch's METER case,
    // which is kept so that the switch still names every verb.
    private bool TryDispatchMeter(string line)
    {
        var trimmed = line.AsSpan().Trim();
        var sp = trimmed.IndexOf(' ');
        var cmd = sp < 0 ? trimmed : trimmed[..sp];
        if (!cmd.Equals("METER", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        DispatchMeter(sp < 0 ? ReadOnlySpan<char>.Empty : trimmed[(sp + 1)..]);
        return true;
    }

    private void DispatchMeter(ReadOnlySpan<char> arg)
    {
        if (int.TryParse(arg.Trim(), out var v))
        {
            _window.SetMeter(v / 1000.0);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
