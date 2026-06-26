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
            await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
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

    private void Dispatch(string line)
    {
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
            case "PROCESSING":
                _window.ShowProcessing(arg.Trim() == "1");
                break;
            case "FAILED":
                _window.ShowFailed(arg);
                break;
            case "HIDE":
                _window.Hide();
                break;
            case "METER":
                if (int.TryParse(arg.Trim(), out var v))
                {
                    _window.SetMeter(v / 1000.0);
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

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
