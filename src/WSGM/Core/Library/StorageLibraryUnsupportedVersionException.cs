using System.IO;

namespace WSGM.Core;

/// <summary>A library format this build cannot interpret and must never repair or overwrite.</summary>
public sealed class StorageLibraryUnsupportedVersionException : IOException
{
    /// <summary>Identifies an unsupported schema version.</summary>
    /// <param name="version">The schema version read from the card.</param>
    public StorageLibraryUnsupportedVersionException(int version)
        : base($"Library format {version} is not supported by this WSGM. Update WSGM before using this card.")
    {
    }

    /// <summary>Identifies schema fields this reader cannot safely preserve.</summary>
    /// <param name="detail">The unsupported schema information.</param>
    public StorageLibraryUnsupportedVersionException(string detail) : base(detail)
    {
    }
}
