using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Plugin.Sdk;

/// <summary>One named operation, independent of a plugin's category or implementation.</summary>
/// <param name="Id">Stable action identity within the plugin.</param>
/// <param name="Label">Plain display label.</param>
/// <param name="Arguments">Bounded primitive arguments and defaults, validated like settings but never persisted as preferences.</param>
public sealed record PluginAction(string Id, string Label, IReadOnlyList<PluginSetting> Arguments);

/// <summary>Who requested a named operation.</summary>
public enum PluginActionOrigin
{
    /// <summary>An explicit user action.</summary>
    User,
    /// <summary>Host-owned session or display-route orchestration.</summary>
    SessionAutomation,
    /// <summary>Host-owned profile restoration.</summary>
    ProfileRestore,
}

/// <summary>One host-identified operation; neither cancellation nor a missing reply authorizes retry.</summary>
/// <param name="OperationId">Unique host-generated request identity.</param>
/// <param name="ActionId">Admitted named action.</param>
/// <param name="Origin">User or host policy origin.</param>
/// <param name="Arguments">Complete validated immutable argument snapshot.</param>
public sealed record PluginActionRequest(Guid OperationId, string ActionId, PluginActionOrigin Origin,
    IReadOnlyDictionary<string, PluginValue> Arguments);

/// <summary>What the provider can actually confirm about an operation.</summary>
public enum PluginActionOutcome
{
    /// <summary>The operation was rejected before external dispatch.</summary>
    Rejected,
    /// <summary>The command was dispatched; this does not confirm resulting external state.</summary>
    Dispatched,
    /// <summary>The action's declared effect was independently verified.</summary>
    AppliedVerified,
    /// <summary>The external outcome is unknown; do not automatically retry.</summary>
    Unconfirmed,
}

/// <summary>Outcome tied to one exact operation identity.</summary>
/// <param name="OperationId">Request identity being answered.</param>
/// <param name="Outcome">Dispatch or confirmation status.</param>
/// <param name="Detail">Bounded plain explanation.</param>
public sealed record PluginActionResult(Guid OperationId, PluginActionOutcome Outcome, string? Detail = null);

/// <summary>Optional named-action capability used by UI and Core orchestration.</summary>
public interface IPluginActions
{
    /// <summary>Static bounded action declarations, captured by the host before startup.</summary>
    IReadOnlyList<PluginAction> Actions { get; }
    /// <summary>Executes one admitted action and confirms only the effect the provider can prove.</summary>
    /// <param name="request">Host identity, origin and validated arguments.</param>
    /// <param name="context">Current instance, generation and deadline.</param>
    /// <param name="cancellationToken">Cooperative cancellation; uncertain writes cannot be retried automatically.</param>
    /// <returns>Result for the exact request identity.</returns>
    ValueTask<PluginActionResult> ExecuteActionAsync(PluginActionRequest request, PluginContext context, CancellationToken cancellationToken);
}

/// <summary>Declarative UI primitive rendered by the host, with no injected plugin UI code.</summary>
public enum PluginUiKind
{
    /// <summary>A named effective-state value.</summary>
    Status,
    /// <summary>A named action with editable primitive arguments initialized from its declared defaults.</summary>
    Action,
    /// <summary>A boolean state and one boolean action argument.</summary>
    Toggle,
    /// <summary>A numeric state and one bounded numeric action argument.</summary>
    Slider,
}

/// <summary>Optional host-rendered contribution. The host decides placement and pinning.</summary>
/// <param name="Id">Stable contribution identity.</param>
/// <param name="Label">Plain display label.</param>
/// <param name="Category">Stable grouping identity, not a plugin category policy grant.</param>
/// <param name="Kind">Host-rendered primitive.</param>
/// <param name="StateKey">Effective-state key for status, toggle or slider.</param>
/// <param name="ActionId">Named action for interactive contributions.</param>
/// <param name="ArgumentKey">Argument changed by a toggle or slider.</param>
public sealed record PluginUiContribution(string Id, string Label, string Category, PluginUiKind Kind,
    string? StateKey = null, string? ActionId = null, string? ArgumentKey = null);

/// <summary>Optional declarative UI contribution source.</summary>
public interface IPluginUi
{
    /// <summary>Optional compact groups of existing contributions, eligible for user pinning.</summary>
    IReadOnlyList<PluginWidget> Widgets => [];

    /// <summary>Static bounded contributions whose action links are validated by the host.</summary>
    IReadOnlyList<PluginUiContribution> Contributions { get; }
}

/// <summary>A compact host-rendered widget scoped to its plugin instance identity.</summary>
/// <param name="Id">Stable widget identity, independent of declaration order.</param>
/// <param name="Label">Plain display title.</param>
/// <param name="ContributionIds">One to eight existing status or control contribution identities.</param>
/// <param name="Icon">Optional host icon key; never markup or executable UI.</param>
/// <param name="SecondaryStateKey">Optional secondary effective-state value.</param>
/// <param name="VisibleStateKey">Optional boolean state key; true makes the widget available for display.</param>
/// <param name="EnabledStateKey">Optional boolean state key; true enables its controls.</param>
/// <param name="NavigationCategory">Optional owning contribution category to open.</param>
/// <remarks>State updates and commands use the normal plugin publication/action contracts. Missing
/// predicate state means unavailable. Hosts retain a placeholder for a pinned unavailable widget.</remarks>
public sealed record PluginWidget(string Id, string Label, IReadOnlyList<string> ContributionIds,
    string? Icon = null, string? SecondaryStateKey = null, string? VisibleStateKey = null,
    string? EnabledStateKey = null, string? NavigationCategory = null);
