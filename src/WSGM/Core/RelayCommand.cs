using System;
using System.Windows.Input;

namespace WSGM.Core;

/// <summary>A minimal <see cref="ICommand"/> that forwards
/// <see cref="Execute"/> to a captured delegate and gates it through an optional
/// <c>canExecute</c> predicate. Used by the view models so XAML buttons can bind
/// commands without any Avalonia (or other UI-framework) dependency, which keeps
/// the type small and unit-testable in isolation.</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    /// <summary>Creates a command around <paramref name="execute"/>.</summary>
    /// <param name="execute">The action invoked by <see cref="Execute"/>.</param>
    /// <param name="canExecute">Optional predicate consulted by
    /// <see cref="CanExecute"/>; when null the command is always executable.</param>
    /// <exception cref="ArgumentNullException"><paramref name="execute"/> is null.</exception>
    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    /// <summary>Raised when <see cref="RaiseCanExecuteChanged"/> is called so bound
    /// controls re-query <see cref="CanExecute"/>.</summary>
    public event EventHandler? CanExecuteChanged;

    /// <summary>Returns the result of the <c>canExecute</c> predicate, or true when
    /// none was supplied. The <paramref name="parameter"/> is ignored.</summary>
    /// <param name="parameter">Unused; parameterless commands take no argument.</param>
    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    /// <summary>Invokes the captured action. The <paramref name="parameter"/> is
    /// ignored; callers honoring <see cref="ICommand"/> semantics should consult
    /// <see cref="CanExecute"/> first (this method does not re-check it).</summary>
    /// <param name="parameter">Unused; parameterless commands take no argument.</param>
    public void Execute(object? parameter) => _execute();

    /// <summary>Notifies bound controls that the executability may have changed
    /// (fires <see cref="CanExecuteChanged"/>).</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>The typed-parameter companion of <see cref="RelayCommand"/>: the
/// command parameter is converted to <typeparamref name="T"/> before the delegates
/// run. A parameter of the wrong type makes <see cref="CanExecute"/> return false
/// and <see cref="Execute"/> a no-op (never a crash — UI frameworks may invoke
/// Execute without a prior CanExecute check). A null parameter is accepted only
/// when <typeparamref name="T"/> can represent null (reference or nullable value
/// type).</summary>
/// <typeparam name="T">The expected command-parameter type.</typeparam>
public sealed class RelayCommand<T> : ICommand
{
    private readonly Action<T?> _execute;
    private readonly Func<T?, bool>? _canExecute;

    /// <summary>Creates a command around <paramref name="execute"/>.</summary>
    /// <param name="execute">The action invoked by <see cref="Execute"/> with the
    /// converted parameter.</param>
    /// <param name="canExecute">Optional predicate consulted by
    /// <see cref="CanExecute"/> with the converted parameter; when null the command
    /// is executable for every convertible parameter.</param>
    /// <exception cref="ArgumentNullException"><paramref name="execute"/> is null.</exception>
    public RelayCommand(Action<T?> execute, Func<T?, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    /// <summary>Raised when <see cref="RaiseCanExecuteChanged"/> is called so bound
    /// controls re-query <see cref="CanExecute"/>.</summary>
    public event EventHandler? CanExecuteChanged;

    /// <summary>Returns false when <paramref name="parameter"/> cannot be converted
    /// to <typeparamref name="T"/> (wrong type, or null for a non-nullable value
    /// type); otherwise the result of the <c>canExecute</c> predicate, or true when
    /// none was supplied.</summary>
    /// <param name="parameter">The command parameter to convert and test.</param>
    public bool CanExecute(object? parameter)
    {
        if (!TryConvert(parameter, out var value))
        {
            return false;
        }

        return _canExecute?.Invoke(value) ?? true;
    }

    /// <summary>Invokes the captured action with the converted parameter. Does
    /// nothing when the parameter cannot be converted to
    /// <typeparamref name="T"/>. Does not re-check the <c>canExecute</c>
    /// predicate.</summary>
    /// <param name="parameter">The command parameter to convert and pass on.</param>
    public void Execute(object? parameter)
    {
        if (TryConvert(parameter, out var value))
        {
            _execute(value);
        }
    }

    /// <summary>Notifies bound controls that the executability may have changed
    /// (fires <see cref="CanExecuteChanged"/>).</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    private static bool TryConvert(object? parameter, out T? value)
    {
        if (parameter is T typed)
        {
            value = typed;
            return true;
        }

        value = default;
        // Null is a valid value only for types that can actually hold it:
        // reference types and Nullable<T> have default == null; a non-nullable
        // value type must reject null instead of silently becoming default(T).
        return parameter is null && default(T) is null;
    }
}
