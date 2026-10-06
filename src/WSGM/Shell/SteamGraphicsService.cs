using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Core;
using WSGM.Device.Sdk.Capabilities;

namespace WSGM.Shell;

/// <summary>The Graphics page in Steam, reached from its row in Steam's main menu.</summary>
/// <remarks>
///     <para>
///         The graphics packages' controls, drawn with Steam's own Settings components by the toolkit's
///         renderer: one sidebar page per adapter and per display, as Intel Graphics Software has a tab
///         for each, and one Steam section per category the package declares. It reads the same
///         projection as the overlay's Device GPU section, so the two cannot disagree about a row.
///     </para>
///     <para>
///         A row the running game overrides is followed by a Use global row, the page's form of the
///         override marker. A Global-only row never has one. A row that applies later says when, and an
///         unavailable row says why and cannot be changed.
///     </para>
/// </remarks>
internal sealed class SteamGraphicsService : ISteamGraphicsBackend, ISteamSettingsQuickAccessBackend, IDisposable
{
    /// <summary>The main menu row's id.</summary>
    internal const string MenuItemId = "wsgm.graphics";

    /// <summary>A graphics chip with its pins, in Steam's menu convention: one solid shape, holes even-odd.</summary>
    internal const string Glyph =
        "M7 7h10v10H7ZM9 9v6h6V9ZM9 3h2v3H9ZM13 3h2v3h-2ZM9 18h2v3H9ZM13 18h2v3h-2ZM3 9h3v2H3ZM3 13h3v2H3Z"
        + "M18 9h3v2h-3ZM18 13h3v2h-3Z";

    /// <summary>A display, for a display's sidebar page.</summary>
    private const string DisplayGlyph = "M2 4h20v13H2ZM4 6v9h16V6ZM9 19h6v2H9Z";

    private readonly Lock _gate = new();
    private readonly IGraphicsOverlaySource _source;
    private long _revision = 1;

    /// <summary>Creates the page's backend over a graphics source it then owns.</summary>
    /// <param name="source">The graphics projection, disposed with this.</param>
    internal SteamGraphicsService(IGraphicsOverlaySource source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _source.Changed += Refresh;
    }

    /// <summary>Whether the page has anything to show: at least one graphics package runs.</summary>
    internal bool Visible => _source.Snapshot().Visible;

    public void Dispose()
    {
        _source.Changed -= Refresh;
        _source.Dispose();
        Changed = null;
    }

    /// <inheritdoc />
    public async Task<SteamUiCommandResult> SetAsync(string key, JsonElement value,
        CancellationToken cancellationToken)
    {
        if (Find(key) is not { } capability)
        {
            return new SteamUiCommandResult(false, "This graphics setting is no longer available.");
        }

        if (!capability.CanInvoke)
        {
            return new SteamUiCommandResult(false,
                capability.Description is { Length: > 0 } reason ? reason : "This setting cannot be changed now.");
        }

        if (!TryValue(capability, value, out var candidate))
        {
            return new SteamUiCommandResult(false, "The graphics setting value is invalid.");
        }

        var result = await _source.WriteAsync(capability, candidate, cancellationToken).ConfigureAwait(false);
        Refresh();
        return result switch
        {
            null => new SteamUiCommandResult(false, "The setting changed while you were choosing. Try again."),
            _ when result.Outcome.IsApplied() || result.Outcome is CommandOutcome.Accepted =>
                SteamUiCommandResult.Applied,
            _ => new SteamUiCommandResult(false, result.Reason?.Detail ?? "The graphics driver refused the setting.")
        };
    }

    /// <summary>Raised when what the page shows may have changed.</summary>
    internal event Action? Changed;

    /// <summary>The page as it should be drawn now.</summary>
    /// <returns>The page model.</returns>
    internal SteamGraphicsState ReadState()
    {
        long revision;
        lock (_gate)
        {
            revision = _revision;
        }

        return new SteamGraphicsState(Pages(_source.Snapshot()), revision);
    }

    internal SteamSettingsQuickAccessState ReadQuickAccessState()
    {
        long revision;
        lock (_gate)
        {
            revision = _revision;
        }

        return new SteamSettingsQuickAccessState(QuickAccessPages(_source.Snapshot()), revision);
    }

    /// <summary>Tells the page something it shows changed.</summary>
    internal void Refresh()
    {
        lock (_gate)
        {
            _revision++;
        }

        Changed?.Invoke();
    }

    /// <summary>The sidebar's pages for a graphics snapshot.</summary>
    /// <param name="snapshot">The graphics projection.</param>
    /// <returns>One page per section, in the snapshot's order.</returns>
    internal static IReadOnlyList<SteamSettingsPage> Pages(GraphicsOverlaySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        List<SteamSettingsPage> pages = [];
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (var section in snapshot.Sections)
        {
            var id = PageId(section.Key);
            for (var suffix = 2; !ids.Add(id); suffix++)
            {
                id = $"{PageId(section.Key)}-{suffix}";
            }

            List<SteamSettingsSection> sections = [];
            if (snapshot.Publishers.FirstOrDefault(publisher => publisher.PluginId == section.PluginId) is
                { Note: { } note })
            {
                sections.Add(new SteamSettingsSection(null,
                [
                    new SteamSettingsRow(id + ".status", SteamSettingsRowKind.Note, "Status", Text: note)
                ], "status"));
            }

            var lead = section.Capabilities.Where(capability =>
                capability.CategoryId is null
                || section.Categories.All(category => category.Id != capability.CategoryId)).ToArray();
            if (lead.Length > 0)
            {
                sections.Add(new SteamSettingsSection(null, [.. lead.Select(Row)], "main"));
            }

            foreach (var category in section.Categories)
            {
                var rows = section.Capabilities.Where(capability => capability.CategoryId == category.Id).ToArray();
                if (rows.Length > 0)
                {
                    sections.Add(new SteamSettingsSection(category.Title, [.. rows.Select(Row)], category.Id));
                }
            }

            pages.Add(new SteamSettingsPage(id, section.Title, sections,
                section.Icon is SectionIcon.Display ? DisplayGlyph : Glyph));
        }

        return pages;
    }

    /// <summary>One vendor dropdown in Quick Access, retaining the adapter and display category headings.</summary>
    internal static IReadOnlyList<SteamSettingsPage> QuickAccessPages(GraphicsOverlaySnapshot snapshot)
    {
        return snapshot.Publishers.Select(publisher =>
        {
            var pages = Pages(snapshot with
            {
                Publishers = [publisher],
                Sections = [.. snapshot.Sections.Where(section => section.PluginId == publisher.PluginId)]
            });
            var sections = pages.SelectMany(page => page.Sections.Select(section => section with
            {
                Title = section.Title is null ? page.Title : page.Title + ": " + section.Title,
                Id = page.Id + "." + section.Id
            })).ToArray();
            return new SteamSettingsPage(PageId(publisher.PluginId),
                GraphicsCapabilityText.PublisherTitle(publisher.Name), sections, Glyph);
        }).ToArray();
    }

    /// <summary>One graphics row on the page.</summary>
    /// <param name="capability">The projected row.</param>
    /// <returns>The page's row for it.</returns>
    /// <remarks>
    ///     A value the running game overrides is marked as Steam's Quick Access rows mark one: its
    ///     description leads with "Game override" and is drawn in Steam's accent blue. There is no Use global row: Steam's
    ///     surfaces return to Global through Steam's Reset button, and the overlay keeps its own.
    /// </remarks>
    internal static SteamSettingsRow Row(DeviceOverlayCapability capability)
    {
        ArgumentNullException.ThrowIfNull(capability);
        var key = RowKey(capability);
        var description = capability.Description is { Length: > 0 } text ? text : null;
        var disabled = !capability.CanInvoke;
        var row = capability switch
        {
            { Writable: true, ValueKind: CapabilityValueKind.Boolean } => new SteamSettingsRow(key,
                SteamSettingsRowKind.Boolean, capability.Title, description,
                capability.CurrentValue?.BooleanValue ?? false, Disabled: disabled),
            { Writable: true, ValueKind: CapabilityValueKind.Integer, Minimum: { } minimum, Maximum: { } maximum }
                when maximum >= minimum => new SteamSettingsRow(key, SteamSettingsRowKind.Range, capability.Title,
                    description, Number: capability.CurrentValue?.IntegerValue ?? minimum, Minimum: minimum,
                    Maximum: maximum, Step: capability.Step ?? 1,
                    Suffix: DeviceOverlayBridge.UnitSuffix(capability.Unit) is { Length: > 0 } suffix ? suffix : null,
                    Disabled: disabled),
            { Writable: true, ValueKind: CapabilityValueKind.Choice, Choices.Count: > 0 } => new SteamSettingsRow(key,
                SteamSettingsRowKind.Choice, capability.Title, description,
                Text: capability.CurrentValue?.ChoiceValue ?? string.Empty,
                Choices:
                [
                    .. capability.Choices.Select(choice =>
                        new SteamSettingsChoice(choice.Value,
                            CapabilityDisplayLabels.For(choice.Display, choice.Value)))
                ],
                Disabled: disabled),
            { SupportsAction: true } => new SteamSettingsRow(key, SteamSettingsRowKind.Action, capability.Title,
                description, Disabled: disabled, ButtonLabel: "Run"),
            _ => new SteamSettingsRow(key, SteamSettingsRowKind.Note, capability.Title, description,
                Text: capability.TrailingText)
        };
        return capability.OverrideId is null
            ? row
            : row with { Accent = true, Description = NativeQamLayout.AccentDescription(row.Description) };
    }

    /// <summary>A row's key: the plugin, the capability and its instance.</summary>
    /// <param name="capability">The projected row.</param>
    /// <returns><c>&lt;pluginId&gt;/&lt;capabilityId&gt;</c>, followed by <c>#&lt;instanceId&gt;</c> when it has one.</returns>
    internal static string RowKey(DeviceOverlayCapability capability)
    {
        return capability.GpuPluginId + "/" + capability.CapabilityId
               + (capability.InstanceId is { } instance ? "#" + instance : string.Empty);
    }

    /// <summary>Puts the renderer's value into the capability's own shape, within what it accepts.</summary>
    /// <param name="capability">The row being changed.</param>
    /// <param name="value">The renderer's value.</param>
    /// <param name="candidate">The capability value, or null for an action.</param>
    /// <returns>Whether the value fits the row.</returns>
    internal static bool TryValue(DeviceOverlayCapability capability, JsonElement value,
        out CapabilityValue? candidate)
    {
        candidate = null;
        switch (capability)
        {
            case { Writable: true, ValueKind: CapabilityValueKind.Boolean }
                when value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                candidate = new CapabilityValue
                    { Kind = CapabilityValueKind.Boolean, BooleanValue = value.GetBoolean() };
                return true;
            case { Writable: true, ValueKind: CapabilityValueKind.Integer, Minimum: { } minimum, Maximum: { } maximum }
                when value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
                                                             && double.IsFinite(number):
                // Steam's slider reports a double; it lands on the row's own steps.
                if (CapabilityProjection.ValidInteger(CapabilityValue.Integer((int)Math.Round(number)), minimum,
                        maximum,
                        Math.Max(1, capability.Step ?? 1)) is not { } integer)
                {
                    return false;
                }

                candidate = CapabilityValue.Integer(integer);
                return true;
            case { Writable: true, ValueKind: CapabilityValueKind.Choice } when value.ValueKind == JsonValueKind.String
                && capability.Choices.Any(choice => choice.Value == value.GetString()):
                candidate = CapabilityValue.Choice(value.GetString()!);
                return true;
            case { SupportsAction: true }:
                return true;
            default:
                return false;
        }
    }

    private DeviceOverlayCapability? Find(string key)
    {
        return _source.Snapshot().Sections
            .SelectMany(section => section.Capabilities)
            .FirstOrDefault(capability => string.Equals(RowKey(capability), key, StringComparison.Ordinal));
    }

    /// <summary>A URL-safe sidebar path segment for a section key.</summary>
    private static string PageId(string key)
    {
        StringBuilder id = new(key.Length);
        foreach (var character in key)
        {
            id.Append(char.IsAsciiLetterOrDigit(character) ? char.ToLowerInvariant(character) : '-');
        }

        return id.ToString().Trim('-') is { Length: > 0 } trimmed ? trimmed : "graphics";
    }
}
