using Scribe.Core.Libraries;

namespace Scribe.Core.Tests.Libraries.Storage;

/// <summary>
/// The journal's fault-injection seam over real files (contract 8.1, J-1): every call goes to
/// <see cref="PhysicalLibraryFileSystem"/>, and a test can make one numbered mutating call fail before its side effect or
/// after it, then fail every later call too, which is what a process that died at that instant leaves behind. It also
/// lets a test act between two steps (an outside save landing mid-install), stand in for Windows' native replace in its
/// documented failure states, and records whether the journal ever called the native replace while its backup name
/// existed, which it must never do.
/// </summary>
internal sealed class FaultingFileSystem : ILibraryFileSystem
{
    private readonly ILibraryFileSystem _inner = PhysicalLibraryFileSystem.Instance;
    private bool _crashed;

    public enum Timing
    {
        Before,
        After,
    }

    /// <summary>Mutating calls so far (writes, replaces, moves, deletes, folder creation and removal).</summary>
    public int MutatingCalls { get; private set; }

    /// <summary>The mutating call to fail, counted from 1, or null for none.</summary>
    public int? FailAt { get; set; }

    public Timing FailTiming { get; set; } = Timing.Before;

    /// <summary>After the fault, every call fails, as nothing runs in a process that died.</summary>
    public bool CrashAfterFault { get; set; } = true;

    /// <summary>Whether the fault has fired.</summary>
    public bool Faulted { get; private set; }

    /// <summary>Called before every mutating call with its kind and paths; may act on the disk (an outside save).</summary>
    public Action<string, string, string?>? BeforeMutation { get; set; }

    /// <summary>Called after every successful mutating call with its kind and paths.</summary>
    public Action<string, string, string?>? AfterMutation { get; set; }

    /// <summary>A read to fail, by path: the exception to throw, or null to read.</summary>
    public Func<string, Exception?>? ReadFault { get; set; }

    /// <summary>A mutating call to fail, by kind and paths: the exception to throw, or null to go ahead.</summary>
    public Func<string, string, string?, Exception?>? MutationFault { get; set; }

    /// <summary>Stands in for the native replace: return true when it handled the call itself.</summary>
    public Func<string, string, string, bool>? ReplaceOverride { get; set; }

    /// <summary>Stands in for a move: return true when it handled the call itself (a move that silently does nothing).</summary>
    public Func<string, string, bool>? MoveOverride { get; set; }

    /// <summary>Ends the process now, from inside a hook: this call and every later one fail.</summary>
    public void CrashNow()
    {
        Faulted = true;
        _crashed = true;
        throw Injected();
    }

    /// <summary>The native replace was called while the backup name it was handed existed (never allowed, 6.6.3).</summary>
    public bool ReplaceCalledWithExistingBackup { get; private set; }

    /// <summary>Every mutating call, as "kind source destination", for order assertions.</summary>
    public List<string> Journal { get; } = [];

    public static IOException Injected(int hresult = unchecked((int)0x80004005)) => new("injected fault", hresult);

    public static IOException SharingViolation() => new("injected sharing violation", unchecked((int)0x80070020));

    public static UnauthorizedAccessException AccessDenied() => new("injected access denied");

    public static IOException DiskFull() => new("injected disk full", unchecked((int)0x80070070));

    public bool Exists(string path)
    {
        ThrowIfCrashed();
        return _inner.Exists(path);
    }

    public bool DirectoryExists(string path)
    {
        ThrowIfCrashed();
        return _inner.DirectoryExists(path);
    }

    public byte[] ReadAllBytes(string path)
    {
        ThrowIfCrashed();
        if (ReadFault?.Invoke(path) is { } fault)
        {
            throw fault;
        }

        return _inner.ReadAllBytes(path);
    }

    public void WriteAllBytesDurably(string path, ReadOnlySpan<byte> bytes)
    {
        var copy = bytes.ToArray();
        Mutate("write", path, null, () => _inner.WriteAllBytesDurably(path, copy));
    }

    public void Replace(string installCopy, string target, string backup) =>
        Mutate("replace", installCopy, target, () =>
        {
            if (_inner.Exists(backup))
            {
                ReplaceCalledWithExistingBackup = true;
            }

            if (ReplaceOverride?.Invoke(installCopy, target, backup) != true)
            {
                _inner.Replace(installCopy, target, backup);
            }
        });

    public void Move(string source, string destination, bool overwrite) =>
        Mutate(overwrite ? "move-over" : "move", source, destination, () =>
        {
            if (MoveOverride?.Invoke(source, destination) != true)
            {
                _inner.Move(source, destination, overwrite);
            }
        });

    public void Delete(string path) => Mutate("delete", path, null, () => _inner.Delete(path));

    public IEnumerable<string> EnumerateFiles(string directory, string pattern)
    {
        ThrowIfCrashed();
        return _inner.EnumerateFiles(directory, pattern).ToList();
    }

    public IEnumerable<string> EnumerateDirectories(string directory)
    {
        ThrowIfCrashed();
        return _inner.EnumerateDirectories(directory).ToList();
    }

    public void CreateDirectory(string path) => Mutate("mkdir", path, null, () => _inner.CreateDirectory(path));

    public void DeleteDirectory(string path) => Mutate("rmdir", path, null, () => _inner.DeleteDirectory(path));

    private void Mutate(string kind, string path, string? other, Action action)
    {
        ThrowIfCrashed();
        MutatingCalls++;
        BeforeMutation?.Invoke(kind, path, other);
        if (MutationFault?.Invoke(kind, path, other) is { } injected)
        {
            throw injected;
        }

        var fire = FailAt == MutatingCalls;
        if (fire && FailTiming == Timing.Before)
        {
            Fault();
        }

        try
        {
            action();
        }
        catch (Exception) when (fire)
        {
            // The call failed on its own (a refused or interrupted replace): the process dies right after it anyway.
            Fault();
        }

        Journal.Add(other is null ? $"{kind} {path}" : $"{kind} {path} {other}");
        AfterMutation?.Invoke(kind, path, other);
        if (fire)
        {
            Fault();
        }
    }

    private void Fault()
    {
        Faulted = true;
        if (CrashAfterFault)
        {
            _crashed = true;
        }

        throw Injected();
    }

    private void ThrowIfCrashed()
    {
        if (_crashed)
        {
            throw Injected();
        }
    }
}
