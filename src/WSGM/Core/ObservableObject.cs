using System.Collections.Generic;
using System.ComponentModel;

namespace WSGM.Core;

/// <summary>Change notification for the objects Settings, the overlay and the taskbar bind to.</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    /// <summary>Raised after a bound property changes.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Tells bindings that a property changed.</summary>
    /// <param name="name">The property name.</param>
    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>Stores a value and raises its change, even when the value is the same.</summary>
    /// <typeparam name="T">The field type.</typeparam>
    /// <param name="field">The backing field.</param>
    /// <param name="value">The new value.</param>
    /// <param name="name">The property name.</param>
    protected void SetField<T>(ref T field, T value, string name)
    {
        field = value;
        Raise(name);
    }

    /// <summary>Stores a value and raises its change only when it differs.</summary>
    /// <typeparam name="T">The field type.</typeparam>
    /// <param name="field">The backing field.</param>
    /// <param name="value">The new value.</param>
    /// <param name="name">The property name.</param>
    /// <returns>Whether the value changed.</returns>
    protected bool SetFieldIfChanged<T>(ref T field, T value, string name)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        Raise(name);
        return true;
    }
}
