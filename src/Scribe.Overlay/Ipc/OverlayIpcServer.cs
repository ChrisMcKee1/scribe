using System;
using System.Globalization;
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
        var trimmed = line.AsSpan().Trim();
        if (trimmed.Length == 0)
        {
            return;
        }

        var sp = trimmed.IndexOf(' ');
        var cmd = sp < 0 ? trimmed : trimmed[..sp];
        var arg = sp < 0 ? ReadOnlySpan<char>.Empty : trimmed[(sp + 1)..];

        if (cmd.Equals("RECORDING", StringComparison.OrdinalIgnoreCase))
        {
            _window.ShowRecording();
        }
        else if (cmd.Equals("WARNING", StringComparison.OrdinalIgnoreCase))
        {
            _window.ShowRecordingWarning(arg.ToString());
        }
        else if (cmd.Equals("PROCESSING", StringComparison.OrdinalIgnoreCase))
        {
            _window.ShowProcessing(arg.Trim().Equals("1", StringComparison.Ordinal));
        }
        else if (cmd.Equals("TYPED", StringComparison.OrdinalIgnoreCase))
        {
            _window.ShowOutcome(OverlayState.Typed, null);
        }
        else if (cmd.Equals("TYPEDWITHOUTCLEANUP", StringComparison.OrdinalIgnoreCase))
        {
            _window.ShowOutcome(OverlayState.TypedWithoutCleanup, arg.ToString());
        }
        else if (cmd.Equals("NOTHINGTYPED", StringComparison.OrdinalIgnoreCase))
        {
            _window.ShowOutcome(OverlayState.NothingTyped, arg.ToString());
        }
        else if (cmd.Equals("PARTLYTYPED", StringComparison.OrdinalIgnoreCase))
        {
            _window.ShowOutcome(OverlayState.PartlyTyped, arg.ToString());
        }
        else if (cmd.Equals("HIDE", StringComparison.OrdinalIgnoreCase))
        {
            _window.Hide();
        }
        else if (cmd.Equals("METER", StringComparison.OrdinalIgnoreCase))
        {
            if (int.TryParse(arg.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            {
                _window.SetMeter(v / 1000.0);
            }
        }
        else if (cmd.Equals("POSITION", StringComparison.OrdinalIgnoreCase))
        {
            if (Enum.TryParse<OverlayAnchor>(arg.Trim(), ignoreCase: true, out var anchor))
            {
                _window.SetAnchor(anchor);
            }
            else
            {
                OverlayLog.Warn($"OverlayIpcServer POSITION with unknown anchor '{arg.ToString()}'");
            }
        }
        else if (cmd.Equals("WARMUP", StringComparison.OrdinalIgnoreCase))
        {
            return; // the window is already constructed and warm
        }
        else if (cmd.Equals("EXIT", StringComparison.OrdinalIgnoreCase))
        {
            OverlayLog.Write("OverlayIpcServer EXIT received");
            _onDisconnected();
        }
        else
        {
            OverlayLog.Warn($"OverlayIpcServer unknown command '{cmd.ToString()}'");
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
