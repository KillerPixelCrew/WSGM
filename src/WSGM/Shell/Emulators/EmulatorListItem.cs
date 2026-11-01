namespace WSGM.Shell;

/// <summary>One registered installation as the Installed view and the detail page show it.</summary>
/// <param name="Id">The installation identity.</param>
/// <param name="Detail">The one-line summary of version, channel, ownership and cores.</param>
/// <param name="Badge">The single status word the first version of both surfaces showed.</param>
/// <param name="UpdateAvailable">Whether a newer, not skipped release is offered.</param>
internal sealed record EmulatorListItem(string Id, string Detail, string Badge, bool UpdateAvailable)
{
    /// <summary>The emulator's name.</summary>
    public string Name { get; init; } = "";

    /// <summary>"1.21.0 · stable", or "1.18.1 · your own installation".</summary>
    public string VersionLine { get; init; } = "";

    /// <summary>"24 cores · 31 systems", or the systems it runs.</summary>
    public string Summary { get; init; } = "";

    /// <summary>The offered version when <see cref="UpdateAvailable" />, otherwise empty.</summary>
    public string UpdateVersion { get; init; } = "";

    /// <summary>The card's status chips: update, setup and ownership, in that order.</summary>
    public EmulatorStatusChip[] Chips { get; init; } = [];

    /// <summary>The setup checklist: each prerequisite and the required runtimes.</summary>
    public EmulatorSetupItem[] Setup { get; init; } = [];

    /// <summary>"1 file missing", "Ready", or empty when nothing needs setting up.</summary>
    public string SetupLabel { get; init; } = "";

    /// <summary>The Steam shortcut of the emulator's interface, whose icon both surfaces show; zero when none.</summary>
    public uint AppId { get; init; }
}
