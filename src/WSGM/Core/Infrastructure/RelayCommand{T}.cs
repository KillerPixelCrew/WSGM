using System;
using System.Windows.Input;

namespace WSGM.Core;

/// <summary>
///     The typed-parameter companion of <see cref="RelayCommand" />: the
///     command parameter is converted to <typeparamref name="T" /> before the delegate
///     runs. A parameter of the wrong type makes <see cref="CanExecute" /> return false
///     and <see cref="Execute" /> a no-op (never a crash — UI frameworks may invoke
///     Execute without a prior CanExecute check). A null parameter is accepted only
///     when <typeparamref name="T" /> can represent null (reference or nullable value
///     type).
/// </summary>
/// <typeparam name="T">The expected command-parameter type.</typeparam>
public sealed class RelayCommand<T> : ICommand
{
    private readonly Action<T?> _execute;

    /// <summary>Creates a command around <paramref name="execute" />.</summary>
    /// <param name="execute">
    ///     The action invoked by <see cref="Execute" /> with the
    ///     converted parameter.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="execute" /> is null.</exception>
    public RelayCommand(Action<T?> execute)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
    }

    /// <summary>Never raised: executability depends only on the parameter type.</summary>
    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    /// <summary>
    ///     False only when <paramref name="parameter" /> cannot be converted
    ///     to <typeparamref name="T" /> (wrong type, or null for a non-nullable value type).
    /// </summary>
    /// <param name="parameter">The command parameter to convert and test.</param>
    /// <returns>True when the parameter is assignable to T, including null only when T can represent it.</returns>
    public bool CanExecute(object? parameter)
    {
        return TryConvert(parameter, out _);
    }

    /// <summary>
    ///     Invokes the captured action with the converted parameter. Does
    ///     nothing when the parameter cannot be converted to <typeparamref name="T" />.
    /// </summary>
    /// <param name="parameter">The command parameter to convert and pass on.</param>
    public void Execute(object? parameter)
    {
        if (TryConvert(parameter, out var value))
        {
            _execute(value);
        }
    }

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
