using System.Collections.Generic;
using WSGM.Plugin.Sdk;

namespace WSGM.Shell;

/// <summary>One running plugin's declared settings.</summary>
/// <param name="Id">The opaque id <see cref="CommonPluginSteamUiSource.ConfigureAsync" /> takes.</param>
/// <param name="Identity">The instance the settings belong to.</param>
/// <param name="Name">The package's display name.</param>
/// <param name="Revision">The revision a change must be made against.</param>
/// <param name="Settings">Each declared setting with the value in force.</param>
internal sealed record CommonPluginSettingsView(
    string Id,
    PluginInstanceIdentity Identity,
    string Name,
    long Revision,
    IReadOnlyList<CommonPluginSettingValue> Settings);
