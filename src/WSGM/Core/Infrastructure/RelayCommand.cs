using System;
using System.Windows.Input;

namespace WSGM.Core;

/// <summary>
///     A minimal always-executable <see cref="ICommand" /> that forwards
///     <see cref="Execute" /> to a captured delegate, so XAML buttons can bind
///     commands without any UI-framework dependency.
/// </summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;

    /// <summary>Creates a command around <paramref name="execute" />.</summary>
    /// <param name="execute">The action invoked by <see cref="Execute" />.</param>
    /// <exception cref="ArgumentNullException"><paramref name="execute" /> is null.</exception>
    public RelayCommand(Action execute)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
    }

    /// <summary>Never raised: the command is always executable.</summary>
    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    /// <summary>Always true. The <paramref name="parameter" /> is ignored.</summary>
    /// <param name="parameter">Unused; parameterless commands take no argument.</param>
    /// <returns>Always true; delegate exceptions, if any, occur only on execution.</returns>
    public bool CanExecute(object? parameter)
    {
        return true;
    }

    /// <summary>Invokes the captured action. The <paramref name="parameter" /> is ignored.</summary>
    /// <param name="parameter">Unused; parameterless commands take no argument.</param>
    public void Execute(object? parameter)
    {
        _execute();
    }
}
