using System.IO;

namespace WSGM.Shell;

/// <summary>One mounted disk volume as one inventory read saw it.</summary>
/// <param name="Letter">The drive letter.</param>
/// <param name="Disk">The physical disk number under the volume.</param>
/// <param name="DriveType">The .NET drive type (Fixed or Removable).</param>
/// <param name="Ready">Whether the media was ready when read.</param>
/// <param name="CapacityBytes">The volume size, 0 when the media is not ready.</param>
/// <param name="Label">The volume label for a ready volume on external storage, otherwise empty.</param>
/// <param name="VolumeId">The volume GUID path behind the letter, or empty when Windows did not name one.</param>
internal sealed record StorageVolumeFacts(
    char Letter,
    int Disk,
    DriveType DriveType,
    bool Ready,
    long CapacityBytes,
    string Label,
    string VolumeId = "")
{
    /// <summary>The volume's root, such as <c>D:\</c>.</summary>
    internal string MountPath => $@"{Letter}:\";
}
