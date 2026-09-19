using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WSGM.OverlayMockup;

// Only in-memory presentation values. This executable has no production service references.
internal sealed class PreviewValue<T>(T initial) : INotifyPropertyChanged
{
    private T _value = initial;

    public T Value
    {
        get => _value;
        set
        {
            if (EqualityComparer<T>.Default.Equals(_value, value))
            {
                return;
            }

            _value = value;
            Changed();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Changed([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
