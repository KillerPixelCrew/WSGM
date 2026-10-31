using Avalonia.Media.Imaging;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>Detached shared Programs presentation; images are borrowed from the session cache.</summary>
internal sealed record ProgramState(ProgramDefinition Definition, bool Running, bool Available,
    string Status, Bitmap? Icon, bool UiIdentityAvailable = true)
{
    internal bool CanLaunch => Available && UiIdentityAvailable;
}
