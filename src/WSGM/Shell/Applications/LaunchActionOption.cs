namespace WSGM.Shell;

/// <summary>A wrapper action with the shared subsystem label and its semantic mode.</summary>
internal sealed record LaunchActionOption(int Mode, string Label);
