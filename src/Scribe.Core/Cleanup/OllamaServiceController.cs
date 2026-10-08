using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Scribe.Core.Diagnostics;

namespace Scribe.Core.Cleanup;

public sealed record OllamaServiceState(bool Running, bool Owned, string? Error = null)
{
    public string ButtonText => Running ? "Stop Ollama" : "Start Ollama";
    public bool CanAct => !Running || Owned;
    public string Description => Error ?? (Owned
        ? "Ollama was started by Scribe. It stops when Scribe closes."
        : Running
            ? "Ollama is running outside Scribe. Stop it in Ollama; Scribe won't stop another app's instance."
            : "Ollama isn't running. Start it here to use or download its models.");
}

internal interface IOllamaOwnedProcess : IDisposable
{
    bool HasExited { get; }
    void Kill();
    bool WaitForExit(int milliseconds);
}

/// <summary>Starts only an explicit local server and stops only the process this controller created.</summary>
public sealed class OllamaServiceController : IDisposable
{
    private readonly SemaphoreSlim _lane = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Func<CancellationToken, Task<bool>> _running;
    private readonly Func<IOllamaOwnedProcess> _start;
    private readonly ILogger _log;
    private readonly LocalServerClient? _servers;
    private readonly TimeSpan _startBound;
    private IOllamaOwnedProcess? _owned;
    private int _disposed;

    public OllamaServiceController(ILogger<OllamaServiceController>? log = null)
    {
        _log = log ?? NullLogger<OllamaServiceController>.Instance;
        _servers = new LocalServerClient();
        _running = async ct =>
        {
            var state = await _servers.ReadAsync(LocalAiServer.OllamaAddress, cancellationToken: ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return state.Reach is LocalServerReach.Reached or LocalServerReach.NeedsKey;
        };
        _start = StartProcess;
        _startBound = TimeSpan.FromSeconds(30);
    }

    internal OllamaServiceController(
        Func<CancellationToken, Task<bool>> running,
        Func<IOllamaOwnedProcess> start,
        TimeSpan? startBound = null,
        ILogger? log = null)
    {
        _running = running;
        _start = start;
        _startBound = startBound ?? TimeSpan.FromSeconds(30);
        _log = log ?? NullLogger.Instance;
    }

    public async Task<OllamaServiceState> ReadAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _lane.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ForgetExited();
            return new(await _running(linked.Token).ConfigureAwait(false), _owned is not null);
        }
        finally
        {
            ReleaseLane();
        }
    }

    /// <summary>An admitted action belongs to the host; a window may cancel only its wait for the answer.</summary>
    public async Task<OllamaServiceState> StartAsync()
    {
        var cancellationToken = _lifetime.Token;
        await _lane.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ForgetExited();
            if (await _running(cancellationToken).ConfigureAwait(false))
            {
                return new(true, _owned is not null);
            }

            // A stopped or unhealthy owned process must be retired before creating another.
            if (_owned is not null && !StopOwned())
            {
                return new(true, true, "Scribe couldn't stop its earlier Ollama process. Choose Stop Ollama again.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            _owned = _start();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_startBound);
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (_owned.HasExited)
                {
                    ForgetExited();
                    return new(await _running(cancellationToken).ConfigureAwait(false), false,
                        "Ollama couldn't start. Check its Windows app and try again.");
                }

                if (await _running(deadline.Token).ConfigureAwait(false) && !_owned.HasExited)
                {
                    return new(true, true);
                }

                await Task.Delay(100, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StopOwned();
            throw;
        }
        catch (Exception failure)
        {
            TryLog(failure);
            var stopped = StopOwned();
            return new(!stopped, !stopped,
                failure switch
                {
                    FileNotFoundException => "Scribe couldn't find Ollama. Install its Windows app first, then try again.",
                    OperationCanceledException => "Ollama couldn't start in time. Open its Windows app or choose Start Ollama again.",
                    _ => "Ollama couldn't start. Open its Windows app or choose Start Ollama again.",
                });
        }
        finally
        {
            ReleaseLane();
        }
    }

    public async Task<OllamaServiceState> StopAsync()
    {
        var cancellationToken = _lifetime.Token;
        await _lane.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ForgetExited();
            if (!StopOwned())
            {
                return new(true, true, "Scribe couldn't stop its Ollama process. Try again.");
            }

            return new(await _running(cancellationToken).ConfigureAwait(false), false);
        }
        finally
        {
            ReleaseLane();
        }
    }

    private void ReleaseLane()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            StopOwned();
            _servers?.Dispose();
        }

        _lane.Release();
    }

    private void ForgetExited()
    {
        if (_owned?.HasExited == true)
        {
            _owned.Dispose();
            _owned = null;
        }
    }

    private bool StopOwned()
    {
        if (_owned is null)
        {
            return true;
        }

        try
        {
            if (!_owned.HasExited)
            {
                _owned.Kill();
                if (!_owned.WaitForExit(3000))
                {
                    return false;
                }
            }

            _owned.Dispose();
            _owned = null;
            return true;
        }
        catch (Exception failure)
        {
            TryLog(failure);
            return false;
        }
    }

    private static IOllamaOwnedProcess StartProcess()
    {
        var local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama", "ollama.exe");
        var executable = File.Exists(local) ? local : (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(path => Path.Combine(path.Trim('"'), "ollama.exe"))
            .FirstOrDefault(File.Exists);
        if (executable is null)
        {
            throw new FileNotFoundException("Ollama is not installed.");
        }

        return OllamaOwnedProcess.Start(StartInfo(executable));
    }

    internal static ProcessStartInfo StartInfo(string executable)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add("serve");
        info.Environment["OLLAMA_HOST"] = "127.0.0.1:11434";
        info.Environment["OLLAMA_NO_CLOUD"] = "1";
        return info;
    }

    private void TryLog(Exception failure)
    {
        try
        {
            _log.LogWarning("Ollama service control failed ({Failure}).", FailureShape.Describe(failure));
        }
        catch
        {
            // Diagnostics cannot prevent service teardown.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();
        if (_lane.Wait(TimeSpan.FromSeconds(3)))
        {
            try
            {
                StopOwned();
                _servers?.Dispose();
            }
            finally
            {
                _lane.Release();
            }
        }
    }
}
