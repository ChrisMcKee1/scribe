using System.Net.Http;
using System.Net.Sockets;
using Azure.Identity;
using Microsoft.Extensions.Logging;

namespace Scribe.Core.Cleanup;

internal sealed partial class TextCleanupService
{
    internal static readonly TimeSpan AutomaticReconnectBackoff = TimeSpan.FromSeconds(30);

    internal TimeProvider AutomaticReconnectClock { get; set; } = TimeProvider.System;

    // This budget belongs to an explicit configuration, not an initialization generation. The automatic
    // reservation takes a new generation too, and must not give its own failure another attempt.
    private bool _automaticReconnectAttempted;
    private long _automaticReconnectOwner;
    private CleanupOptions? _automaticReconnectOptions;
    private long _automaticReconnectFailedAt;

    private void ResetAutomaticReconnectLocked()
    {
        _automaticReconnectAttempted = false;
        _automaticReconnectOwner = 0;
        _automaticReconnectOptions = null;
        _automaticReconnectFailedAt = 0;
    }

    private void RememberAutomaticReconnectLocked(long owner, CleanupOptions options, Exception? failure)
    {
        if (_operations.IsClosed || _lifetime.IsCancellationRequested || _configureCts?.IsCancellationRequested == true ||
            owner == 0 || owner != _initGeneration || _status != CleanupStatus.Unavailable || _options != options)
        {
            return;
        }

        _automaticReconnectOwner = 0;
        _automaticReconnectOptions = null;
        if (_automaticReconnectAttempted || !CanAutomaticallyReconnect(options, failure))
        {
            return;
        }

        _automaticReconnectFailedAt = AutomaticReconnectClock.GetTimestamp();
        _automaticReconnectOwner = owner;
        _automaticReconnectOptions = options;
    }

    private static bool CanAutomaticallyReconnect(CleanupOptions options, Exception? failure)
    {
        if (!options.IsActionable ||
            options.Provider is not (CleanupProvider.AzureFoundry or CleanupProvider.OpenAiCompatible) ||
            failure is null)
        {
            return false;
        }

        foreach (var current in CleanupFailureShape.Walk(failure))
        {
            if (current is OperationCanceledException or TimeoutException or AuthenticationFailedException or
                CredentialUnavailableException or ArgumentException or FormatException or NotSupportedException)
            {
                return false;
            }
        }

        if (IsModelNotLoaded(failure) || IsGpuShaderIncompatibility(failure) || IsModelBuildFailure(failure) ||
            IsExecutionProviderUnavailable(failure) || IsLocalModelLoadFailure(failure, options))
        {
            return false;
        }

        var status = ExtractHttpStatus(failure);
        return status == 429 || status is >= 500 and <= 599 ||
            (status == 0 && CleanupFailureShape.Walk(failure)
                .Any(current => current is HttpRequestException { StatusCode: null } or SocketException));
    }

    private void TryStartAutomaticReconnect()
    {
        InitReservation? reservation = null;
        try
        {
            CleanupOptions options;
            lock (_gate)
            {
                if (_operations.IsClosed || _lifetime.IsCancellationRequested || _configureCts?.IsCancellationRequested == true ||
                    _status != CleanupStatus.Unavailable || _initPhase != InitPhase.Idle ||
                    _automaticReconnectAttempted || _automaticReconnectOwner != _initGeneration ||
                    _automaticReconnectOptions is not { } failedOptions || _options != failedOptions ||
                    !failedOptions.IsActionable ||
                    AutomaticReconnectClock.GetElapsedTime(_automaticReconnectFailedAt) < AutomaticReconnectBackoff)
                {
                    return;
                }

                if (ReserveInitializationLocked("Reconnecting AI cleanup...") is not { } reserved)
                {
                    return;
                }

                reservation = reserved;
                options = failedOptions;
                _automaticReconnectAttempted = true;
                _automaticReconnectOwner = 0;
                _automaticReconnectOptions = null;
            }

            TryCancel(reservation.Value.Superseded);
            if (reservation.Value.StatusChanged)
            {
                RaiseStatusChanged();
            }

            // Diagnostics cannot stop a recording or prevent the reserved initialization from starting.
            try
            {
                _log.LogInformation("AI cleanup automatic reconnect reserved ({Provider}).", options.Provider);
            }
            catch (Exception)
            {
            }

            StartInitialization(options, reservation.Value);
        }
        catch (Exception ex)
        {
            // Prewarm runs on recording admission. A scheduling or clock failure must not escape it,
            // and a reservation that could not start must not leave a status nobody will finish.
            var changed = false;
            if (reservation is { } failed)
            {
                lock (_gate)
                {
                    if (_initGeneration == failed.Generation && _initPhase == InitPhase.Live)
                    {
                        _initPhase = InitPhase.Idle;
                        changed = WriteStatusLocked(failed.Generation, CleanupStatus.Unavailable,
                            CleanupReason.Same("AI cleanup couldn't reconnect. Try again in Settings."));
                    }
                }
            }

            TryLogFailureShape(ex, "Automatic AI cleanup reconnect could not start.");
            if (changed)
            {
                RaiseStatusChanged();
            }
        }
    }
}
