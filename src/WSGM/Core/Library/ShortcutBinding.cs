namespace WSGM.Core;

/// <summary>The card a Steam shortcut's literal drive path was last found on.</summary>
/// <remarks>
///     A literal path such as <c>D:\Games\Foo.exe</c> only means whatever is in D: right now; a handheld's reader
///     gives every card the same letter. The binding remembers which card held the file the last time it was
///     present, so a different card in that drive reads as the wrong card rather than as deleted files.
/// </remarks>
/// <param name="AccountId">The Steam account whose shortcut list it belongs to.</param>
/// <param name="AppId">The shortcut.</param>
/// <param name="Path">The literal path, exactly as resolved from the shortcut.</param>
/// <param name="VolumeId">The volume GUID path the file was found on.</param>
/// <param name="CardId">That card's own identity when it has one, so a new volume GUID for the same card still matches.</param>
/// <param name="Name">The card's name at the time, shown while it is out.</param>
public sealed record ShortcutBinding(uint AccountId, uint AppId, string Path, string VolumeId, string CardId,
    string Name);
