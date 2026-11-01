using WSGM.Core;

namespace WSGM.Shell;

/// <summary>One active Core Audio endpoint shown by the quick access sheet's audio panel.</summary>
public sealed class AudioEndpointEntry : ObservableObject
{
    private string _name;

    /// <summary>Creates a visible endpoint row.</summary>
    /// <param name="id">The opaque Windows endpoint identifier.</param>
    /// <param name="name">The friendly device name.</param>
    internal AudioEndpointEntry(string id, string name)
    {
        Id = id;
        _name = name;
    }

    /// <summary>Gets the opaque Windows endpoint identifier.</summary>
    internal string Id { get; }

    /// <summary>Gets the friendly device name.</summary>
    public string Name
    {
        get => _name;
        internal set
        {
            if (_name == value)
            {
                return;
            }

            _name = value;
            Raise(nameof(Name));
        }
    }
}
