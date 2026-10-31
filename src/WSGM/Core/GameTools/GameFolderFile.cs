using System;
using System.IO;

namespace WSGM.Core;

/// <summary>A file found by the bounded walk.</summary>
/// <param name="RelativePath">The forward-slash path relative to the walked root.</param>
/// <param name="FullPath">The absolute path.</param>
/// <param name="Size">The size in bytes.</param>
/// <param name="LastWriteUtc">The last write time, which with the size identifies an unchanged file.</param>
internal sealed record GameFolderFile(string RelativePath, string FullPath, long Size, DateTime LastWriteUtc)
{
    /// <summary>The file name.</summary>
    internal string Name { get; } = Path.GetFileName(RelativePath);

    /// <summary>The forward-slash relative directory, empty at the root.</summary>
    internal string Directory { get; } =
        RelativePath.Contains('/') ? RelativePath[..RelativePath.LastIndexOf('/')] : "";
}
