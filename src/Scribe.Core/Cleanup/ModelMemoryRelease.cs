namespace Scribe.Core.Cleanup;

/// <summary>Why Scribe gives back the memory of AI cleanup's model on this PC (<see cref="ITextCleanupService.ReleaseModelMemory"/>).</summary>
public enum ModelMemoryRelease
{
    /// <summary>
    /// Scribe went unused for the idle time the user chose. Foundry Local's model is unloaded. Ollama and LM Studio were
    /// asked with every request to keep the model only that long (keep_alive, ttl), so each frees it on its own clock,
    /// counted from Scribe's last request; Scribe asks them itself only when its requests could not say so.
    /// </summary>
    Idle,

    /// <summary>Dictation was paused: Scribe stands down, and asks whichever app holds the model to free it now.</summary>
    Pause,
}
