using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Core;

namespace WSGM.Overlay;

/// <summary>One overlay's manual scheme workflow. Entry points and notifications belong to the
/// UI thread; native calls and persistence run on a worker. Closing prevents late publication.</summary>
internal sealed class PowerSchemeSelection(PowerSchemes schemes, Action<Guid> persist, bool readOnly = false) : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;
    internal event Action? Changed;
    internal IReadOnlyList<PowerScheme> Schemes { get; private set; } = [];
    internal Guid? ActiveId { get; private set; }
    internal string Status { get; private set; } = "Read Windows power profiles to choose one.";
    internal bool Busy { get; private set; }
    internal bool CanSelect => !readOnly && !Busy && !_disposed && ActiveId is not null && Schemes.Count > 0;

    internal Task RefreshAsync() => RunAsync(null);
    internal Task ApplyAsync(Guid id)
        => CanSelect && Schemes.Any(scheme => scheme.Id == id) ? RunAsync(id) : Task.CompletedTask;

    private async Task RunAsync(Guid? requested)
    {
        if (_disposed || Busy)
        {
            return;
        }
        Busy = true;
        Status = requested is null ? "Reading Windows power profiles..." : "Applying Windows power profile...";
        Changed?.Invoke();
        CancellationToken token = _lifetime.Token;
        try
        {
            var result = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                string? saveError = null;
                if (requested is { } id)
                {
                    lock (PowerSchemes.MutationGate)
                    {
                        schemes.Select(id, token);
                        // Record confirmed writes even if the sheet closes meanwhile.
                        try { persist(id); }
                        catch (Exception ex) { saveError = $"Windows applied the profile, but WSGM could not save the reference: {ex.Message}"; }
                    }
                }
                return (Items: schemes.Enumerate(), Active: schemes.ReadActive(), SaveError: saveError);
            }, token);
            if (_disposed)
            {
                return;
            }
            Schemes = result.Items;
            ActiveId = result.Active;
            string activeName = Schemes.FirstOrDefault(scheme => scheme.Id == result.Active)?.Name
                ?? result.Active.ToString("D");
            Status = result.SaveError ?? (Schemes.Count == 0
                ? "Windows returned no selectable power profiles. Refresh to try again."
                : $"Active: {activeName}. Changes apply immediately and also change this profile's idle timeouts.");
            if (readOnly)
            {
                Status += " Preview only; changes are disabled.";
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                ActiveId = null;
                Status = $"{ex.Message} Refresh to read Windows state before trying again.";
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
}
