using WSGM.Plugin.IntelGpu.Graphics;

namespace WSGM.Plugin.IntelGpu.Tests.Fakes;

/// <summary>An in-memory registry key tree, so the registry tests never touch a real hive.</summary>
/// <remarks>Names compare without case, as the registry's do. Opening a key hands out the same node.</remarks>
internal sealed class MemoryRegistryNode : IRegistryNode
{
    private readonly Dictionary<string, MemoryRegistryNode> _subKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, object> _values = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Makes every open of this key throw, as an adapter subkey the caller may not read does.</summary>
    public bool Unreadable { get; set; }

    public IRegistryNode? OpenSubKey(string path, bool writable = false)
    {
        return Find(path);
    }

    public IRegistryNode CreateSubKey(string name)
    {
        return Create(name);
    }

    public string[] GetSubKeyNames()
    {
        return [.. _subKeys.Keys];
    }

    public string[] GetValueNames()
    {
        return [.. _values.Keys];
    }

    public object? GetValue(string name)
    {
        return _values.GetValueOrDefault(name);
    }

    public void SetDWord(string name, int value)
    {
        _values[name] = value;
    }

    public void DeleteValue(string name)
    {
        _values.Remove(name);
    }

    public void Dispose()
    {
    }

    /// <summary>Opens or creates the key at a backslash-separated path below this one.</summary>
    /// <param name="path">The path.</param>
    /// <returns>The key.</returns>
    public MemoryRegistryNode Create(string path)
    {
        var node = this;
        foreach (var name in path.Split('\\'))
        {
            if (!node._subKeys.TryGetValue(name, out var child))
            {
                child = new MemoryRegistryNode();
                node._subKeys[name] = child;
            }

            node = child;
        }

        return node;
    }

    /// <summary>Stores a value of any registry type: a string, an <see cref="int" /> DWORD or a <see cref="long" /> QWORD.</summary>
    /// <param name="name">The value name.</param>
    /// <param name="value">The value.</param>
    public void Set(string name, object value)
    {
        _values[name] = value;
    }

    private MemoryRegistryNode? Find(string path)
    {
        var node = this;
        foreach (var name in path.Split('\\'))
        {
            if (!node._subKeys.TryGetValue(name, out var child))
            {
                return null;
            }

            child.ThrowIfUnreadable();
            node = child;
        }

        return node;
    }

    private void ThrowIfUnreadable()
    {
        if (Unreadable)
        {
            throw new UnauthorizedAccessException("Requested registry access is not allowed.");
        }
    }
}
