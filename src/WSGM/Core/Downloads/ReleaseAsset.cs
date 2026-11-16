namespace WSGM.Core;

/// <summary>A file a release publishes.</summary>
/// <param name="Name">The file name, which must be a plain name before it is downloaded.</param>
/// <param name="Url">The HTTPS download URL.</param>
/// <param name="Sha256">The published lowercase or uppercase SHA-256, or an empty string when none was published.</param>
/// <param name="Size">The published size in bytes, or zero when unknown.</param>
/// <param name="Revision">A provider revision that changes when the file is replaced in place, or empty.</param>
/// <param name="Md5">
///     The published MD5, or empty. Checked only when the publisher supplies no SHA-256, as ES-DE's release
///     feed does; the locally computed SHA-256 remains the integrity record either way.
/// </param>
internal sealed record ReleaseAsset(
    string Name,
    string Url,
    string Sha256 = "",
    long Size = 0,
    string Revision = "",
    string Md5 = "");
