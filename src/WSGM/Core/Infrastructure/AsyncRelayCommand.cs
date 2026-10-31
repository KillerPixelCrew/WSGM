using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace WSGM.Core;

/// <summary>
///     An asynchronous command that disables itself while its operation is running and
///     observes every failure at the unavoidable async-void <see cref="ICommand.Execute" /> boundary.
/// </summary>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private int _running;

    /// <summary>Creates a serialized asynchronous command.</summary>
    /// <param name="execute">Non-null operation invoked on the calling synchronization context; failures are logged.</param>
    /// <exception cref="ArgumentNullException">The operation is null.</exception>
    public AsyncRelayCommand(Func<Task> execute)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
    }

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <inheritdoc />
    public bool CanExecute(object? parameter)
    {
        return Volatile.Read(ref _running) == 0;
    }

    /// <inheritdoc />
    // ReSharper disable once AsyncVoidMethod
    public async void Execute(object? parameter)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return;
        }

        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await _execute();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A command implementation should normally report its own user-facing
            // failure. This last boundary guarantees a forgotten catch never tears
            // down Avalonia or becomes an unobserved Task.
            Log.Error("Asynchronous command failed", ex);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
