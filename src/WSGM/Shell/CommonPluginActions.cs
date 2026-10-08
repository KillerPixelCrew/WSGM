using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamUiToolkit;
using WSGM.Device.Sdk.Capabilities;
using WSGM.Plugin.Sdk;

namespace WSGM.Shell;

/// <summary>Validates static named actions and UI links, then dispatches explicit requests once.</summary>
internal sealed class CommonPluginActions
{
    private readonly IPluginActions? _provider;
    private readonly IPluginSteamUi? _steamUi;

    /// <summary>Validates and captures the plugin's action, widget, and Steam UI declarations.</summary>
    /// <param name="plugin">Borrowed provider retained for explicit actions and UI invalidation subscriptions.</param>
    /// <exception cref="ArgumentException">A declaration, argument schema, or linked action/state identifier is invalid.</exception>
    internal CommonPluginActions(IPlugin plugin)
    {
        _provider = plugin as IPluginActions;
        _steamUi = plugin as IPluginSteamUi;
        var declared = _provider is null
            ? []
            : _provider.Actions ?? throw new ArgumentException("Plugin actions declaration is absent.");
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

    /// <summary>Read-only action declarations with copied argument schemas and choice lists.</summary>
    internal IReadOnlyList<PluginAction> Actions { get; }

    /// <summary>Captured controls whose action and argument links passed admission validation.</summary>
    internal IReadOnlyList<PluginUiContribution> Contributions { get; }

    /// <summary>Captured widgets with copied contribution identifier lists.</summary>
    internal IReadOnlyList<PluginWidget> Widgets { get; }

    /// <summary>Steam entry points linked to admitted actions; app-context links require a numeric argument.</summary>
    internal IReadOnlyList<PluginSteamUiContribution> SteamUiContributions { get; }

    /// <summary>Captured module references; the host still owns route and surface admission.</summary>
    internal IReadOnlyList<ISteamUiModule> SteamUiModules { get; }

    /// <summary>Captured page declarations; their route availability is checked by the frontend host.</summary>
    internal IReadOnlyList<SteamPage> SteamPages { get; }

    /// <summary>Subscribes to the provider's UI invalidation event when it implements Steam UI.</summary>
    /// <param name="handler">Callback retained by the provider until explicitly unsubscribed.</param>
    internal void SubscribeSteamUiChanged(Action handler)
    {
        if (_steamUi is not null)
        {
            _steamUi.SteamUiChanged += handler;
        }
    }

    /// <summary>Removes a previously registered UI invalidation callback.</summary>
    /// <param name="handler">The same delegate supplied when subscribing.</param>
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
        if (pages is null || pages.Any(page => page is null
                                               || !PluginConfigurationRules.ValidKey(page.Id)
                                               || !Label(page.Title)))
        {
            throw new ArgumentException("Invalid plugin Steam page declaration.");
        }

        return Array.AsReadOnly([.. pages]);
    }

    private static IReadOnlyList<ISteamUiModule> CaptureSteamUiModules(IReadOnlyList<ISteamUiModule> modules)
    {
        if (modules is null || modules.Any(module => module is null))
        {
            throw new ArgumentException("Invalid plugin Steam UI module declaration.");
        }

        return Array.AsReadOnly([.. modules]);
    }

    private IReadOnlyList<PluginSteamUiContribution> CaptureSteamUiContributions(
        IReadOnlyList<PluginSteamUiContribution> contributions)
    {
        if (contributions is null)
        {
            throw new ArgumentException("Plugin Steam UI contribution declaration is absent.");
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

    /// <summary>Validates widget identifiers and links and copies each contribution identifier list.</summary>
    /// <param name="widgets">Complete widget declaration; null, duplicates, and empty control groups are invalid.</param>
    /// <param name="contributions">Admitted controls to which widgets may link.</param>
    /// <returns>A read-only captured widget list.</returns>
    /// <exception cref="ArgumentException">A widget or its control/category links are invalid.</exception>
    internal static IReadOnlyList<PluginWidget> CaptureWidgets(IReadOnlyList<PluginWidget> widgets,
        IReadOnlyList<PluginUiContribution> contributions)
    {
        if (widgets is null)
        {
            throw new ArgumentException("Plugin widget declaration is absent.");
        }

        HashSet<string> ids = new(StringComparer.Ordinal);
        HashSet<string> controls = new(contributions.Select(item => item.Id), StringComparer.Ordinal);
        List<PluginWidget> captured = [];
        foreach (var widget in widgets)
        {
            // ReSharper disable ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
            if (widget is null || !PluginConfigurationRules.ValidKey(widget.Id) || !ids.Add(widget.Id)
                || !Label(widget.Label) || widget.ContributionIds is null || widget.ContributionIds.Count == 0
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

    /// <summary>Validates arguments, supplies declared defaults, and dispatches one explicit action.</summary>
    /// <param name="actionId">Exact identifier in the captured action declaration.</param>
    /// <param name="origin">Defined request source used by the provider for policy and diagnostics.</param>
    /// <param name="arguments">Explicit declared arguments; undeclared keys and invalid values are rejected.</param>
    /// <param name="context">Current instance identity, generation, and deadline.</param>
    /// <param name="cancellationToken">Cancels before dispatch or cooperatively during execution.</param>
    /// <returns>
    ///     The provider's correlated result; invalid input is rejected. Exceptions, including cancellation after
    ///     dispatch, and invalid confirmations become unconfirmed results and are never retried here.
    /// </returns>
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
                (!route.StartsWith("/wsgm/", StringComparison.Ordinal) || route.Any(char.IsControl)))
            {
                return new PluginActionResult(operationId, PluginActionOutcome.Unconfirmed,
                    "The plugin returned an invalid Steam route.");
            }

            return result with
            {
                SteamRoute = route
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new PluginActionResult(operationId, PluginActionOutcome.Unconfirmed, ex.Message);
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
        return PlainText.TryValidate(label, "label", out _);
    }
}
