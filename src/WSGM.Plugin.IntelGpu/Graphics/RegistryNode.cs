using Microsoft.Win32;

namespace WSGM.Plugin.IntelGpu.Graphics;

/// <summary>
///     The registry operations the adapter enumeration, the shared-memory transport and the per-application
///     sync use.
/// </summary>
/// <remarks>Production wraps the Windows registry; tests supply an in-memory hive.</remarks>
internal interface IRegistryNode : IDisposable
{
    /// <summary>Opens a subkey, or returns null when it does not exist.</summary>
    /// <param name="path">The subkey path, separated by backslashes.</param>
    /// <param name="writable">Whether the key is opened for writing.</param>
    /// <returns>The subkey, or null.</returns>
    IRegistryNode? OpenSubKey(string path, bool writable = false);

    /// <summary>Opens a subkey for writing, creating it when it does not exist.</summary>
    /// <param name="name">The subkey name.</param>
    /// <returns>The writable subkey.</returns>
    IRegistryNode CreateSubKey(string name);

    /// <summary>The names of the direct subkeys.</summary>
    /// <returns>Every subkey name.</returns>
    string[] GetSubKeyNames();

    /// <summary>The names of the values.</summary>
    /// <returns>Every value name.</returns>
    string[] GetValueNames();

    /// <summary>Reads a value, or returns null when it does not exist.</summary>
    /// <param name="name">The value name.</param>
    /// <returns>The value as the registry types it: a DWORD is an <see cref="int" />, a QWORD a <see cref="long" />.</returns>
    object? GetValue(string name);

    /// <summary>Writes a DWORD value.</summary>
    /// <param name="name">The value name.</param>
    /// <param name="value">The value.</param>
    void SetDWord(string name, int value);

    /// <summary>Deletes a value; one that does not exist is not an error.</summary>
    /// <param name="name">The value name.</param>
    void DeleteValue(string name);
}

/// <summary>A Windows registry key behind <see cref="IRegistryNode" />.</summary>
/// <param name="key">The key; disposing the node disposes it.</param>
internal sealed class WindowsRegistryNode(RegistryKey key) : IRegistryNode
{
    /// <summary>HKLM, which the plugin reads and never disposes.</summary>
    public static IRegistryNode LocalMachine { get; } = new WindowsRegistryNode(Registry.LocalMachine);

    public IRegistryNode? OpenSubKey(string path, bool writable = false)
    {
        return key.OpenSubKey(path, writable) is { } subKey ? new WindowsRegistryNode(subKey) : null;
    }

    public IRegistryNode CreateSubKey(string name)
    {
        return new WindowsRegistryNode(key.CreateSubKey(name, true));
    }

    public string[] GetSubKeyNames()
    {
        return key.GetSubKeyNames();
    }

    public string[] GetValueNames()
    {
        return key.GetValueNames();
    }

    public object? GetValue(string name)
    {
        return key.GetValue(name);
    }

    public void SetDWord(string name, int value)
    {
        key.SetValue(name, value, RegistryValueKind.DWord);
    }

    public void DeleteValue(string name)
    {
        key.DeleteValue(name, false);
    }

    public void Dispose()
    {
        key.Dispose();
    }
}
