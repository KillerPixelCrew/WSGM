namespace WSGM.Core;

/// <summary>An unmanaged copy of the tool found in the target directory.</summary>
/// <param name="RelativePath">The file relative to the target directory.</param>
/// <param name="Version">Its product version.</param>
internal sealed record GameToolUnmanagedCopy(string RelativePath, string Version);
