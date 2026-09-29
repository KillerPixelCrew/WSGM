using Microsoft.Win32;

namespace WSGM.Plugin.IntelGpu.Tests;

/// <summary>A uniquely named HKCU subtree standing in for a machine-wide key, deleted on dispose.</summary>
/// <remarks>The transports' root seams point here; no test ever writes HKLM.</remarks>
internal sealed class TemporaryRegistryKey : IDisposable
{
    public TemporaryRegistryKey(string area)
    {
        Path = $@"Software\WSGM.Tests\{area}\{Guid.NewGuid():N}";
    }

    /// <summary>The subtree's path below HKCU.</summary>
    public string Path { get; }

    /// <summary>Creates or opens a key below the subtree.</summary>
    /// <param name="relative">The path below <see cref="Path" />, or empty for the subtree itself.</param>
    /// <returns>The writable key.</returns>
    public RegistryKey Create(string relative = "")
    {
        return Registry.CurrentUser.CreateSubKey(relative.Length == 0 ? Path : $@"{Path}\{relative}");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(Path, false);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException)
        {
            // A leaked unique subtree is preferable to a failed test run reporting a false defect.
        }
    }
}
