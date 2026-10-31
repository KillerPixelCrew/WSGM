using System;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Shell;

namespace WSGM.Overlay;

/// <summary>
///     One overlay's projection of a Shell choice row, such as the Windows power profile or the processor
///     core preference. It reads and selects through the same Shell service Steam's row uses, so both
///     surfaces show one value, option list and status. Entry points and notifications belong to the UI
///     thread; closing prevents late publication.
/// </summary>
/// <param name="key">The row's stable focus key.</param>
/// <param name="title">The row's visible and accessible name.</param>
/// <param name="read">The Shell projection Steam's row reads.</param>
/// <param name="select">The Shell command Steam's row sends, with an option id from the last publication.</param>
/// <param name="readOnly">True to render the row without permitting an external-state write.</param>
internal sealed class ChoiceRowSelection(
    string key,
    string title,
    Func<ValueTask<ChoiceRowState?>> read,
    Func<string, CancellationToken, Task<SteamUiCommandResult>> select,
    bool readOnly = false) : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;
    private bool _readAgain;
    private bool _reading;
    private string? _refusal;

    /// <summary>The row's stable focus key.</summary>
    internal string Key => key;

    /// <summary>The row's visible and accessible name.</summary>
    internal string Title => title;

    /// <summary>The last published state; no options until the first read, which hides the row.</summary>
    internal ChoiceRowState State { get; private set; } = new(false, [], string.Empty, string.Empty);

    /// <summary>Whether an explicit selection is running.</summary>
    internal bool Busy { get; private set; }

    /// <summary>Whether the user can choose now; an explicit choice never waits on a fresh read.</summary>
    internal bool CanSelect => !readOnly && !Busy && !_disposed && State.Available;

    /// <summary>The line under the row: the published detail, or the running selection.</summary>
    internal string Status => Busy
        ? "Applying the selection..."
        : readOnly && State.StatusText.Length > 0
            ? State.StatusText + " Preview only; changes are disabled."
            : readOnly
                ? "Preview only; changes are disabled."
                : State.StatusText;

    /// <summary>Cancels this projection's lifetime and prevents later publication; the Shell service remains alive.</summary>
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

    /// <summary>Raised on the UI thread when the published state or busy status changes.</summary>
    internal event Action? Changed;

    /// <summary>Reads the Shell projection without overlapping another read or a selection.</summary>
    /// <returns>Completion of the read; a closed or busy projection is a no-op and late results are not published.</returns>
    internal async Task RefreshAsync()
    {
        if (_disposed || Busy || _reading)
        {
            return;
        }

        _reading = true;
        try
        {
            do
            {
                // A selection that finished during this read asks for one more, so its result is what shows.
                _readAgain = false;
                var state = await read() ?? new ChoiceRowState(false, [], string.Empty, string.Empty);
                if (_disposed || Busy || _readAgain)
                {
                    continue;
                }

                State = _refusal is { } refusal && state.StatusText != refusal
                    ? state with { StatusText = refusal }
                    : state;
                Changed?.Invoke();
            } while (_readAgain && !_disposed);
        }
        finally
        {
            _reading = false;
        }
    }

    /// <summary>Selects one published option through the Shell command, then publishes what Shell reads next.</summary>
    /// <param name="option">An option id from the current state; the current value, closed or read-only state is ignored.</param>
    /// <returns>Completion of the command and the following read; a refusal becomes the status line.</returns>
    internal async Task SelectAsync(string option)
    {
        if (!CanSelect || option == State.Current)
        {
            return;
        }

        Busy = true;
        Changed?.Invoke();
        var token = _lifetime.Token;
        SteamUiCommandResult result;
        try
        {
            result = await select(option, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        finally
        {
            if (!_disposed)
            {
                Busy = false;
            }
        }

        if (_disposed)
        {
            return;
        }

        // The command's own refusal is the clearest reason; Shell may not repeat it in its next read.
        _refusal = result.Succeeded ? null : result.Error is { Length: > 0 } error ? error : null;
        if (_reading)
        {
            _readAgain = true;
            return;
        }

        await RefreshAsync();
    }
}
