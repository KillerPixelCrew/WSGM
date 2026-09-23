using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WSGM.Plugin.Sdk;

namespace WSGM.Shell;

/// <summary>Validates static named actions and UI links, then dispatches explicit requests once.</summary>
internal sealed class CommonPluginActions
{
    private readonly IPluginActions? _provider;
    private readonly IPluginSteamUi? _steamUi;

    internal CommonPluginActions(IPlugin plugin)
    {
        _provider = plugin as IPluginActions;
        _steamUi = plugin as IPluginSteamUi;
        var declared = _provider is null
            ? []
            : _provider.Actions ?? throw new ArgumentException("Plugin actions declaration is absent.");
        if (declared.Count > 128)
        {
            throw new ArgumentException("Too many plugin actions.");
        }

        HashSet<string> ids = new(StringComparer.Ordinal);
        List<PluginAction> captured = [];
        foreach (var action in declared)
        {
            // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
            if (action is null || !PluginConfigurationRules.ValidKey(action.Id) || !ids.Add(action.Id)
                || !Label(action.Label) || !PluginConfigurationRules.IsValid(action.Arguments))
            {
                throw new ArgumentException("Invalid plugin action declaration.");
            }

            captured.Add(action with
            {
                Arguments = Array.AsReadOnly([
                    .. action.Arguments.Select(argument => argument with
                    {
                        Choices = argument.Choices is null ? null : Array.AsReadOnly([.. argument.Choices])
                    })
                ])
            });
        }

        Actions = captured.AsReadOnly();
        var contributions = plugin is IPluginUi ui
            ? ui.Contributions ?? throw new ArgumentException("Plugin UI declaration is absent.")
            : [];
        if (contributions.Count > 128)
        {
            throw new ArgumentException("Too many plugin UI contributions.");
        }

        ids.Clear();
        // ReSharper disable ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (contributions.Any(contribution =>
                contribution is null || !PluginConfigurationRules.ValidKey(contribution.Id)
                                     || !ids.Add(contribution.Id) ||
                                     !PluginConfigurationRules.ValidKey(contribution.Category)
                                     || !Label(contribution.Label) || !ValidContribution(contribution)))
            // ReSharper restore ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        {
            throw new ArgumentException("Plugin UI contribution has an invalid state or action link.");
        }

        Contributions = Array.AsReadOnly([.. contributions]);
        Widgets = CaptureWidgets(plugin is IPluginUi widgetSource ? widgetSource.Widgets : [], Contributions);
        SteamUiContributions = CaptureSteamUiContributions(
            plugin is IPluginSteamUi steamUi ? steamUi.SteamUiContributions : []);
        SteamUiModules = CaptureSteamUiModules(plugin is IPluginSteamUi steamModuleSource
            ? steamModuleSource.SteamUiModules
            : []);
        SteamPages = CaptureSteamPages(plugin is IPluginSteamUi steamPageSource
            ? steamPageSource.SteamPages
            : []);
    }

    internal IReadOnlyList<PluginAction> Actions { get; }
    internal IReadOnlyList<PluginUiContribution> Contributions { get; }
    internal IReadOnlyList<PluginWidget> Widgets { get; }
    internal IReadOnlyList<PluginSteamUiContribution> SteamUiContributions { get; }
    internal IReadOnlyList<ISteamUiModule> SteamUiModules { get; }
    internal IReadOnlyList<SteamPage> SteamPages { get; }

    internal void SubscribeSteamUiChanged(Action handler)
    {
        if (_steamUi is not null)
        {
            _steamUi.SteamUiChanged += handler;
        }
    }

    internal void UnsubscribeSteamUiChanged(Action handler)
    {
        if (_steamUi is not null)
        {
            _steamUi.SteamUiChanged -= handler;
        }
    }

    /// <summary>Captures declared pages, refusing a malformed declaration at admission.</summary>
    /// <remarks>
    ///     Only shape is checked here, as everywhere else in this class. Whether a route is actually
    ///     served is the host's decision, because only it knows what the rest of the session already
    ///     claims.
    /// </remarks>
    private static IReadOnlyList<SteamPage> CaptureSteamPages(IReadOnlyList<SteamPage> pages)
    {
        if (pages is null || pages.Count > 16 || pages.Any(page => page is null
                                                                   || !PluginConfigurationRules.ValidKey(page.Id)
                                                                   || !Label(page.Title)))
        {
            throw new ArgumentException("Invalid plugin Steam page declaration.");
        }

        return Array.AsReadOnly([.. pages]);
    }

    private static IReadOnlyList<ISteamUiModule> CaptureSteamUiModules(IReadOnlyList<ISteamUiModule> modules)
    {
        if (modules is null || modules.Count > 16 || modules.Any(module => module is null))
        {
            throw new ArgumentException("Invalid plugin Steam UI module declaration.");
        }

        return Array.AsReadOnly([.. modules]);
    }

    private IReadOnlyList<PluginSteamUiContribution> CaptureSteamUiContributions(
        IReadOnlyList<PluginSteamUiContribution> contributions)
    {
        if (contributions is null || contributions.Count > 64)
        {
            throw new ArgumentException("Invalid plugin Steam UI contribution count.");
        }

        HashSet<string> ids = new(StringComparer.Ordinal);
        List<PluginSteamUiContribution> captured = [];
        foreach (var contribution in contributions)
        {
            var action = Actions.FirstOrDefault(candidate => candidate.Id == contribution?.ActionId);
            var argument =
                action?.Arguments.FirstOrDefault(candidate => candidate.Key == contribution?.AppIdArgumentKey);
            // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
            if (contribution is null || !PluginConfigurationRules.ValidKey(contribution.Id)
                                     || !ids.Add(contribution.Id) || !Label(contribution.Label) || action is null
                                     || !Enum.IsDefined(contribution.Placement)
                                     || (contribution.Placement == PluginSteamUiPlacement.ExtensionsTab
                                         ? contribution.AppIdArgumentKey is not null
                                         : !PluginConfigurationRules.ValidKey(contribution.AppIdArgumentKey)
                                           || argument?.Kind != PluginSettingKind.Number))
            {
                // ReSharper restore once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
                throw new ArgumentException("Invalid plugin Steam UI contribution.");
            }

            captured.Add(contribution);
        }

        return captured.AsReadOnly();
    }

    internal static IReadOnlyList<PluginWidget> CaptureWidgets(IReadOnlyList<PluginWidget> widgets,
        IReadOnlyList<PluginUiContribution> contributions)
    {
        if (widgets is null || widgets.Count > 32)
        {
            throw new ArgumentException("Invalid plugin widget count.");
        }

        HashSet<string> ids = new(StringComparer.Ordinal);
        HashSet<string> controls = new(contributions.Select(item => item.Id), StringComparer.Ordinal);
        List<PluginWidget> captured = [];
        foreach (var widget in widgets)
        {
            // ReSharper disable ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
            if (widget is null || !PluginConfigurationRules.ValidKey(widget.Id) || !ids.Add(widget.Id)
                || !Label(widget.Label) || widget.ContributionIds is null || widget.ContributionIds.Count is < 1 or > 8
                // ReSharper restore ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
                || widget.ContributionIds.Distinct(StringComparer.Ordinal).Count() != widget.ContributionIds.Count
                || widget.ContributionIds.Any(id => !controls.Contains(id))
                || new[] { widget.Icon, widget.SecondaryStateKey, widget.VisibleStateKey, widget.EnabledStateKey }
                    .Any(key => key is not null && !PluginConfigurationRules.ValidKey(key))
                || (widget.NavigationCategory is not null &&
                    contributions.All(item => item.Category != widget.NavigationCategory)))
            {
                throw new ArgumentException("Invalid plugin widget declaration or contribution link.");
            }

            captured.Add(widget with { ContributionIds = Array.AsReadOnly([.. widget.ContributionIds]) });
        }

        return captured.AsReadOnly();
    }

    internal async Task<PluginActionResult> ExecuteAsync(string actionId, PluginActionOrigin origin,
        IReadOnlyDictionary<string, PluginValue> arguments, PluginContext context, CancellationToken cancellationToken)
    {
        var operationId = Guid.NewGuid();
        var action = Actions.FirstOrDefault(candidate => candidate.Id == actionId);
        if (_provider is null || action is null || !Enum.IsDefined(origin)
            || arguments.Any(pair => action.Arguments.All(argument => argument.Key != pair.Key)))
        {
            return new PluginActionResult(operationId, PluginActionOutcome.Rejected,
                "Unknown action, origin or argument.");
        }

        Dictionary<string, PluginValue> values = new(StringComparer.Ordinal);
        foreach (var argument in action.Arguments)
        {
            var value = arguments.TryGetValue(argument.Key, out var supplied) ? supplied : argument.Default;
            if (!PluginConfigurationRules.Accepts(argument, value))
            {
                return new PluginActionResult(operationId, PluginActionOutcome.Rejected,
                    "Action arguments do not match the declaration.");
            }

            values.Add(argument.Key, value);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var request = new PluginActionRequest(operationId, actionId, origin,
            new ReadOnlyDictionary<string, PluginValue>(values));
        try
        {
            var result = await _provider.ExecuteActionAsync(request, context, cancellationToken).ConfigureAwait(false);
            // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
            if (result is null || result.OperationId != operationId || !Enum.IsDefined(result.Outcome))
            {
                return new PluginActionResult(operationId, PluginActionOutcome.Unconfirmed,
                    "The plugin returned an invalid action confirmation.");
            }

            var route = result.SteamRoute;
            if (route is not null &&
                (route.Length is 0 or > 256 || !route.StartsWith("/wsgm/", StringComparison.Ordinal)
                                            || route.Any(char.IsControl)))
            {
                return new PluginActionResult(operationId, PluginActionOutcome.Unconfirmed,
                    "The plugin returned an invalid Steam route.");
            }

            return result with
            {
                Detail = result.Detail is { Length: > 2048 } detail ? detail[..2048] : result.Detail,
                SteamRoute = route
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new PluginActionResult(operationId, PluginActionOutcome.Unconfirmed,
                ex.Message.Length > 2048 ? ex.Message[..2048] : ex.Message);
        }
    }

    private bool ValidContribution(PluginUiContribution contribution)
    {
        var action = Actions.FirstOrDefault(candidate => candidate.Id == contribution.ActionId);
        var argument = action?.Arguments.FirstOrDefault(candidate => candidate.Key == contribution.ArgumentKey);
        return contribution.Kind switch
        {
            PluginUiKind.Status => PluginConfigurationRules.ValidKey(contribution.StateKey)
                                   && contribution.ActionId is null && contribution.ArgumentKey is null,
            PluginUiKind.Action => action is not null && contribution.StateKey is null &&
                                   contribution.ArgumentKey is null,
            PluginUiKind.Toggle => PluginConfigurationRules.ValidKey(contribution.StateKey)
                                   && argument?.Kind == PluginSettingKind.Boolean,
            PluginUiKind.Slider => PluginConfigurationRules.ValidKey(contribution.StateKey)
                                   && argument is
                                       { Kind: PluginSettingKind.Number, Minimum: not null, Maximum: not null },
            _ => false
        };
    }

    private static bool Label(string? label)
    {
        return PluginText.TryValidate(label, 128, "label", out _);
    }
}
