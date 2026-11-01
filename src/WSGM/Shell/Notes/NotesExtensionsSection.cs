using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;

namespace WSGM.Shell;

/// <summary>The Notes entry on the Quick Access Extensions tab, which opens the Steam Notes page.</summary>
internal sealed class NotesExtensionsSection : IExtensionsTabSection
{
    /// <summary>The Extensions item id.</summary>
    internal const string SectionKey = "wsgm.notes";

    /// <summary>The Extensions action that opens Notes.</summary>
    internal const string OpenId = "wsgm.notes.open";

    /// <inheritdoc />
    public string SectionId => SectionKey;

    /// <inheritdoc />
    public SteamExtensionsTabItem ReadExtensionsItem()
    {
        return new SteamExtensionsTabItem(SectionKey, "Notes", string.Empty, "Ready",
            "Game notes with text, drawings and screenshots, shared with the WSGM overlay.",
            [new SteamExtensionsTabAction(OpenId, "Open Notes")]);
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> ActivateExtensionAsync(string id, CancellationToken cancellationToken)
    {
        return Task.FromResult(id == OpenId
            ? SteamUiCommandResult.Route(NotesSteamSurface.Route)
            : new SteamUiCommandResult(false, "That entry is no longer available."));
    }

    /// <inheritdoc />
    public Task<SteamUiCommandResult> ConfigureExtensionAsync(string key, JsonElement value,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new SteamUiCommandResult(false, "Notes has no settings here."));
    }
}
