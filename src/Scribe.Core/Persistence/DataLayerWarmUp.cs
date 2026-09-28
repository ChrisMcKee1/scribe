using Scribe.Core.Models;

namespace Scribe.Core.Persistence;

/// <summary>
/// Compiles the data layer's first use on a worker while startup does other work (DATA-O-01,
/// <see cref="Diagnostics.PerfFlags.DataLayerWarmUp"/>).
/// </summary>
/// <remarks>
/// The session banner's settings load is the first thing that opens the database, on the UI thread before the tray and the
/// hotkey exist, and it paid the first use of e_sqlite3, SQLitePCL, Microsoft.Data.Sqlite and System.Text.Json: about 145
/// ms cold against a few warm. This runs the same repository code once against a throwaway in-memory database, so that code
/// is compiled and the serializer's metadata for the settings document is cached before the UI thread needs them. It
/// touches no file and no path, starts nothing that outlives it (no history repository, whose constructor starts the
/// deletion notifier's drain), logs nothing, and swallows every failure: the real first use then pays what it paid before.
/// Nothing it does can reach the real database's state (its integrity check, its lost-document record, <c>LastLoadFailed</c>),
/// which belongs to that instance alone; the one thing the two share is the provider's initialization, which
/// <see cref="ScribeDatabase"/> runs once and which a second caller waits for.
/// </remarks>
public static class DataLayerWarmUp
{
    /// <summary>Starts the warm-up on the thread pool. The task never faults; nothing needs to wait for it.</summary>
    public static Task Start() => Task.Run(Run);

    internal static void Run()
    {
        try
        {
            using var database = ScribeDatabase.CreateInMemory();
            database.Initialize();
            var settings = new SettingsRepository(database);
            settings.Save(AppSettings.CreateDefault());
            _ = settings.Load();
            _ = new DictionaryRepository(database).GetAll();
        }
        catch (Exception)
        {
            // A warm-up is an optimization: whatever failed here, the real first use does its own work and reports its own.
        }
    }
}
