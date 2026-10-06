using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>
///     One overlay's projection of the session's manual scheme workflow. Entry points and notifications
///     belong to the UI thread. Closing prevents late publication.
/// </summary>
internal sealed class PowerSchemeSelection(NativeQamPowerProfileService profiles, bool readOnly = false)
    : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;
    internal IReadOnlyList<PowerScheme> Schemes { get; private set; } = [];
    internal Guid? ActiveId { get; private set; }
    internal string Status { get; private set; } = "Read Windows power profiles to choose one.";

    internal bool Busy { get; private set; }

    // An explicit choice is the user action: it never waits on a fresh read of the active profile.
    internal bool CanSelect => !readOnly && !Busy && !_disposed && Schemes.Count > 0;

    /// <summary>
    ///     Whether the picker is worth showing: Windows offers more than one profile. A machine with a
    ///     single plan has nothing to choose, and the overlay hides the section rather than showing a
    ///     dropdown with one entry.
    /// </summary>
    internal bool Offered => PowerSchemes.OffersChoice(Schemes.Count);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
        Changed = null;
    }

    internal event Action? Changed;

    internal Task RefreshAsync()
    {
        return RunAsync(null);
    }

    internal Task ApplyAsync(Guid id)
    {
        return CanSelect && Schemes.Any(scheme => scheme.Id == id) ? RunAsync(id) : Task.CompletedTask;
    }

    private async Task RunAsync(Guid? requested)
    {
        if (_disposed || Busy)
        {
            return;
        }

        Busy = true;
        Status = requested is null ? "Reading Windows power profiles..." : "Applying Windows power profile...";
        Changed?.Invoke();
        var token = _lifetime.Token;
        try
        {
            IReadOnlyList<PowerScheme> items;
            Guid? active;
            string? detail;
            if (requested is { } id)
            {
                detail = await profiles.SelectAsync(id, token);
                items = Schemes;
                active = id;
            }
            else
            {
                (items, active, detail) = await profiles.ReadSchemesAsync(token);
            }

            if (_disposed)
            {
                return;
            }

            Schemes = items;
            ActiveId = active;
            var activeName = Schemes.FirstOrDefault(scheme => scheme.Id == active)?.Name
                             ?? active?.ToString("D");
            Status = detail ?? (Schemes.Count == 0
                ? "Windows returned no selectable power profiles. Refresh to try again."
                : $"Active: {activeName}. Changes apply immediately and also change this profile's idle timeouts.");
            if (readOnly)
            {
                Status += " Preview only; changes are disabled.";
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                if (requested is null)
                {
                    ActiveId = null;
                }

                Status = $"{ex.Message} Choose again for another explicit attempt, or refresh Windows state.";
            }
        }
        finally
        {
            if (!_disposed)
            {
                Busy = false;
                Changed?.Invoke();
            }
        }
    }
}
