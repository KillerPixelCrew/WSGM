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
    internal IReadOnlyList<PluginAction> Actions { get; }
    internal IReadOnlyList<PluginUiContribution> Contributions { get; }
    internal PluginActionResult? LastResult { get; private set; }

    internal CommonPluginActions(IPlugin plugin)
    {
        _provider = plugin as IPluginActions;
        var declared = _provider is null ? [] : _provider.Actions ?? throw new ArgumentException("Plugin actions declaration is absent.");
        if (declared.Count > 128) { throw new ArgumentException("Too many plugin actions."); }
        HashSet<string> ids = new(StringComparer.Ordinal);
        List<PluginAction> captured = [];
        foreach (var action in declared)
        {
            if (action is null || !PluginConfigurationRules.ValidKey(action.Id) || !ids.Add(action.Id)
                || !Label(action.Label) || !PluginConfigurationRules.IsValid(action.Arguments))
            { throw new ArgumentException("Invalid plugin action declaration."); }
            captured.Add(action with
            {
                Arguments = Array.AsReadOnly(action.Arguments.Select(argument => argument with
                { Choices = argument.Choices is null ? null : Array.AsReadOnly(argument.Choices.ToArray()) }).ToArray())
            });
        }
        Actions = captured.AsReadOnly();
        var contributions = plugin is IPluginUi ui ? ui.Contributions ?? throw new ArgumentException("Plugin UI declaration is absent.") : [];
        if (contributions.Count > 128) { throw new ArgumentException("Too many plugin UI contributions."); }
        ids.Clear();
        foreach (var contribution in contributions)
        {
            if (contribution is null || !PluginConfigurationRules.ValidKey(contribution.Id) || !ids.Add(contribution.Id)
                || !PluginConfigurationRules.ValidKey(contribution.Category) || !Label(contribution.Label)
                || !ValidContribution(contribution))
            { throw new ArgumentException("Plugin UI contribution has an invalid state or action link."); }
        }
        Contributions = Array.AsReadOnly(contributions.ToArray());
    }

    internal async Task<PluginActionResult> ExecuteAsync(string actionId, PluginActionOrigin origin,
        IReadOnlyDictionary<string, PluginValue> arguments, PluginContext context, CancellationToken cancellationToken)
    {
        Guid operationId = Guid.NewGuid();
        var action = Actions.FirstOrDefault(candidate => candidate.Id == actionId);
        if (_provider is null || action is null || !Enum.IsDefined(origin)
            || arguments.Any(pair => !action.Arguments.Any(argument => argument.Key == pair.Key)))
        { return LastResult = new(operationId, PluginActionOutcome.Rejected, "Unknown action, origin or argument."); }
        Dictionary<string, PluginValue> values = new(StringComparer.Ordinal);
        foreach (var argument in action.Arguments)
        {
            var value = arguments.TryGetValue(argument.Key, out var supplied) ? supplied : argument.Default;
            if (!PluginConfigurationRules.Accepts(argument, value))
            { return LastResult = new(operationId, PluginActionOutcome.Rejected, "Action arguments do not match the declaration."); }
            values.Add(argument.Key, value);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var request = new PluginActionRequest(operationId, actionId, origin, new ReadOnlyDictionary<string, PluginValue>(values));
        LastResult = new(operationId, PluginActionOutcome.Unconfirmed, "Action dispatch pending");
        try
        {
            var result = await _provider.ExecuteActionAsync(request, context, cancellationToken).ConfigureAwait(false);
            if (result is null || result.OperationId != operationId || !Enum.IsDefined(result.Outcome))
            { return LastResult = new(operationId, PluginActionOutcome.Unconfirmed, "The plugin returned an invalid action confirmation."); }
            return LastResult = result with { Detail = result.Detail is { Length: > 2048 } detail ? detail[..2048] : result.Detail };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return LastResult = new(operationId, PluginActionOutcome.Unconfirmed, ex.Message.Length > 2048 ? ex.Message[..2048] : ex.Message); }
    }

    private bool ValidContribution(PluginUiContribution contribution)
    {
        var action = Actions.FirstOrDefault(candidate => candidate.Id == contribution.ActionId);
        var argument = action?.Arguments.FirstOrDefault(candidate => candidate.Key == contribution.ArgumentKey);
        return contribution.Kind switch
        {
            PluginUiKind.Status => PluginConfigurationRules.ValidKey(contribution.StateKey)
                && contribution.ActionId is null && contribution.ArgumentKey is null,
            PluginUiKind.Action => action is not null && contribution.StateKey is null && contribution.ArgumentKey is null,
            PluginUiKind.Toggle => PluginConfigurationRules.ValidKey(contribution.StateKey)
                && argument?.Kind == PluginSettingKind.Boolean,
            PluginUiKind.Slider => PluginConfigurationRules.ValidKey(contribution.StateKey)
                && argument is { Kind: PluginSettingKind.Number, Minimum: not null, Maximum: not null },
            _ => false,
        };
    }

    private static bool Label(string? label) => !string.IsNullOrWhiteSpace(label) && label.Length <= 128 && !label.Any(char.IsControl);
}
