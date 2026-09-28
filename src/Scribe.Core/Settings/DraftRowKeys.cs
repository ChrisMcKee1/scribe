using System.Globalization;

namespace Scribe.Core.Settings;

/// <summary>
/// The keys the Settings footer's dirty check matches draft rows by, exactly as the rows have made them since 0.5.0: a
/// dictionary or snippet row by its Id once it has one, a profile row by the name and processes it was loaded with, and a
/// row that has none of those by its own identity.
/// </summary>
public static class DraftRowKeys
{
    /// <summary>A dictionary or snippet row's key.</summary>
    public static string ForId(long id, object row) =>
        id > 0 ? id.ToString(CultureInfo.InvariantCulture) : $"new:{row.GetHashCode()}";

    /// <summary>A profile row's key.</summary>
    public static string ForProfile(bool saved, string? loadedName, string? loadedProcesses, object row) =>
        saved ? $"saved:{loadedName}:{loadedProcesses}" : $"new:{row.GetHashCode()}";
}

/// <summary>
/// A row's key, kept until what it is made from changes (LeanFooterRefresh): the footer reads every row's key on every
/// keystroke anywhere in Settings, and each read used to build a new string. Held as a mutable field of the row it keys.
/// </summary>
public struct DraftRowKeyCache
{
    private string? _key;
    private long _id;
    private bool _saved;
    private string? _name;
    private string? _processes;

    /// <summary>The key <see cref="DraftRowKeys.ForId"/> gives, built again only when <paramref name="id"/> changed.</summary>
    public string ForId(long id, object row)
    {
        if (_key is null || _id != id)
        {
            _id = id;
            _key = DraftRowKeys.ForId(id, row);
        }

        return _key;
    }

    /// <summary>The key <see cref="DraftRowKeys.ForProfile"/> gives, built again only when one of its parts changed.</summary>
    public string ForProfile(bool saved, string? loadedName, string? loadedProcesses, object row)
    {
        if (_key is null || _saved != saved ||
            !string.Equals(_name, loadedName, StringComparison.Ordinal) ||
            !string.Equals(_processes, loadedProcesses, StringComparison.Ordinal))
        {
            _saved = saved;
            _name = loadedName;
            _processes = loadedProcesses;
            _key = DraftRowKeys.ForProfile(saved, loadedName, loadedProcesses, row);
        }

        return _key;
    }
}
