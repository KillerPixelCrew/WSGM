namespace WSGM.Shell;

/// <summary>The image a capture produced, or why there is none.</summary>
/// <param name="Image">Encoded PNG or JPEG bytes, or null on failure.</param>
/// <param name="Width">Pixel width.</param>
/// <param name="Height">Pixel height.</param>
/// <param name="Error">The reason the capture failed, in words the page shows.</param>
internal sealed record NotesCaptureFrame(byte[]? Image, int Width, int Height, string? Error)
{
    /// <summary>A failed capture.</summary>
    /// <param name="error">Why.</param>
    /// <returns>The frame.</returns>
    internal static NotesCaptureFrame Failed(string error)
    {
        return new NotesCaptureFrame(null, 0, 0, error);
    }
}
