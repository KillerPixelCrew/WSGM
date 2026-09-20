using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Plugin.Sdk;

namespace WSGM.Plugin.Artwork;

/// <summary>The bundled Steam artwork plugin.</summary>
public sealed class ArtworkPlugin : IPlugin, IConfigurablePlugin, IPluginActions, IPluginSteamUi
{
    /// <summary>The stable package identity.</summary>
    public const string PluginId = "wsgm.artwork";

    private readonly SteamArtworkBrowserSource _browser;
    private readonly ArtworkStateStore _store = new();
    private ArtworkConfiguration _configuration = ArtworkConfiguration.Default;
    private bool _ready;

    /// <summary>Creates the plugin without touching Steam or the filesystem.</summary>
    public ArtworkPlugin()
    {
        _browser = new SteamArtworkBrowserSource(() => Volatile.Read(ref _configuration), _store);
        _browser.Changed += OnBrowserChanged;
        SteamUiModules =
        [
            SteamArtworkBrowserSurface.Module(
                () => _ready,
                () => new ValueTask<SteamArtworkBrowserState?>(_browser.ReadState()),
                _browser)
        ];
    }

    /// <inheritdoc />
    public IReadOnlyList<PluginSetting> Settings { get; } =
    [
        new("steamgriddb-api-key", "SteamGridDB API key", PluginSettingKind.Text, new PluginValue(Text: "")),
        new("screenscraper-enabled", "Search Screenscraper.fr", PluginSettingKind.Boolean,
            new PluginValue(true)),
        new("screenscraper-user", "Screenscraper account", PluginSettingKind.Text, new PluginValue(Text: "")),
        new("screenscraper-password", "Screenscraper password", PluginSettingKind.Secret, new PluginValue(Text: "")),
        new("default-tab", "Default artwork tab", PluginSettingKind.Text, new PluginValue(Text: "grid"),
            Choices: ["grid", "wide", "hero", "logo", "icon", "manage"]),
        new("tab-order", "Artwork tab order", PluginSettingKind.OrderedChoices,
            new PluginValue(Text: "grid,wide,hero,logo,icon,manage"),
            Choices: ["grid", "wide", "hero", "logo", "icon", "manage"]),
        new("show-grid", "Show Capsule tab", PluginSettingKind.Boolean, new PluginValue(true)),
        new("show-wide", "Show Wide Capsule tab", PluginSettingKind.Boolean, new PluginValue(true)),
        new("show-hero", "Show Hero tab", PluginSettingKind.Boolean, new PluginValue(true)),
        new("show-logo", "Show Logo tab", PluginSettingKind.Boolean, new PluginValue(true)),
        new("show-icon", "Show Icon tab", PluginSettingKind.Boolean, new PluginValue(true)),
        new("show-manage", "Show Manage tab", PluginSettingKind.Boolean, new PluginValue(true))
    ];

    /// <inheritdoc />
    public ValueTask<PluginConfigurationResult> ConfigureAsync(
        PluginConfiguration configuration,
        PluginContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _configuration = new ArtworkConfiguration(
            Text(configuration, "steamgriddb-api-key"),
            Boolean(configuration, "screenscraper-enabled"),
            Text(configuration, "screenscraper-user"),
            Text(configuration, "screenscraper-password"),
            Text(configuration, "default-tab"),
            Text(configuration, "tab-order"),
            Boolean(configuration, "show-grid"),
            Boolean(configuration, "show-wide"),
            Boolean(configuration, "show-hero"),
            Boolean(configuration, "show-logo"),
            Boolean(configuration, "show-icon"),
            Boolean(configuration, "show-manage"));
        _browser.ConfigurationChanged();
        SteamUiChanged?.Invoke();
        return ValueTask.FromResult(new PluginConfigurationResult(
            configuration.Revision, PluginConfigurationOutcome.Applied));
    }

    /// <inheritdoc />
    public string Id => PluginId;

    /// <inheritdoc />
    public ValueTask<PluginHealth> StartAsync(
        IPluginHost host,
        PluginContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(context.StateDirectory);
        _store.SetDirectory(context.StateDirectory);
        _ready = true;
        return ValueTask.FromResult(PluginHealth.Ready);
    }

    /// <inheritdoc />
    public ValueTask SessionChangedAsync(PluginContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<bool> StopAsync(PluginContext context, CancellationToken cancellationToken)
    {
        _ready = false;
        SteamUiChanged?.Invoke();
        return ValueTask.FromResult(true);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _ready = false;
        _browser.Changed -= OnBrowserChanged;
        _browser.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public IReadOnlyList<PluginAction> Actions { get; } =
    [
        new("open", "Change Artwork", [
            new PluginSetting("app-id", "Steam application id", PluginSettingKind.Number,
                new PluginValue(Number: 1), 1, uint.MaxValue)
        ])
    ];

    /// <inheritdoc />
    public ValueTask<PluginActionResult> ExecuteActionAsync(
        PluginActionRequest request,
        PluginContext context,
        CancellationToken cancellationToken)
    {
        if (!_ready || request.ActionId != "open"
                    || !request.Arguments.TryGetValue("app-id", out var value)
                    || value.Number is not { } number
                    || number < 1 || number > uint.MaxValue || number != Math.Truncate(number))
        {
            return ValueTask.FromResult(new PluginActionResult(
                request.OperationId, PluginActionOutcome.Rejected, "The selected Steam game is unavailable."));
        }

        var appId = checked((uint)number);
        return OpenAsync(request.OperationId, appId, cancellationToken);
    }

    /// <inheritdoc />
    public IReadOnlyList<PluginSteamUiContribution> SteamUiContributions { get; } =
    [
        new("change-artwork", "Change Artwork…", PluginSteamUiPlacement.GameContextMenu, "open", "app-id")
    ];

    /// <inheritdoc />
    public IReadOnlyList<ISteamUiModule> SteamUiModules { get; }

    /// <inheritdoc />
    public event Action? SteamUiChanged;

    private async ValueTask<PluginActionResult> OpenAsync(
        Guid operationId,
        uint appId,
        CancellationToken cancellationToken)
    {
        var result = await _browser.OpenAsync(appId, cancellationToken).ConfigureAwait(false);
        return result.Succeeded
            ? new PluginActionResult(
                operationId,
                PluginActionOutcome.Dispatched,
                SteamRoute: $"/wsgm/artwork/{appId.ToString(CultureInfo.InvariantCulture)}")
            : new PluginActionResult(operationId, PluginActionOutcome.Rejected, result.Error);
    }

    private static string Text(PluginConfiguration configuration, string key)
    {
        return configuration.Values[key].Text?.Trim() ?? "";
    }

    private static bool Boolean(PluginConfiguration configuration, string key)
    {
        return configuration.Values[key].Boolean == true;
    }

    private void OnBrowserChanged()
    {
        SteamUiChanged?.Invoke();
    }
}
