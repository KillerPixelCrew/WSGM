using System.IO;

namespace WSGM.Core;

/// <summary>One entry of a folder listing.</summary>
/// <param name="Path">Its full path.</param>
/// <param name="IsDirectory">Whether it is a folder.</param>
/// <param name="Attributes">Its file attributes, for skipping hidden, system and linked entries.</param>
/// <param name="Length">The file length already available from directory enumeration.</param>
internal sealed record FolderEntry(string Path, bool IsDirectory, FileAttributes Attributes, long Length = 0);
