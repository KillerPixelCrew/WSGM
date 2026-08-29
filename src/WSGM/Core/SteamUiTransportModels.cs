using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WSGM.Core;

/// <summary>Allowlisted Steam UI contexts that WSGM may attach to persistently.</summary>
public enum SteamUiTargetRole
{
    /// <summary>The headless Steam context that owns client stores and webpack modules.</summary>
    SharedJsContext,

    /// <summary>The native controller-oriented Quick Access popup.</summary>
    QuickAccess,

    /// <summary>The Big Picture window, which renders the pages the user is looking at.</summary>
    /// <remarks>
    /// Distinct from <see cref="SharedJsContext"/>, which owns the route and the module registry but
    /// almost no DOM: with the Steam Input page open on the reference Claw its body measured 218
    /// bytes, while every element that page draws — and every Valve glyph image the stylesheet keys
    /// off — was here. CSS is per document, so a stylesheet meant for what the user sees has to be
    /// installed in this one. The glyph stylesheet was going to SharedJSContext, which is why half a
    /// megabyte of correct CSS applied, verified, and changed nothing.
    /// </remarks>
    MainWindow,
}

/// <summary>Health of one persistent Steam UI target channel.</summary>
public enum SteamUiTransportHealth
{
    /// <summary>No subscriber currently needs the target.</summary>
    Idle,

    /// <summary>The target is being discovered or reconnected.</summary>
    Connecting,

    /// <summary>A validated target is connected and accepting bounded requests.</summary>
    Ready,

    /// <summary>Steam or the requested target is not currently present.</summary>
    Unavailable,

    /// <summary>The endpoint or target failed validation.</summary>
    Incompatible,

    /// <summary>A connected channel failed and will be retried while subscribed.</summary>
    Retrying,

    /// <summary>The channel has been disposed.</summary>
    Disposed,
}

/// <summary>Generations that invalidate cached Steam UI state and commands.</summary>
/// <param name="Browser">Changes when Steam's browser endpoint changes.</param>
/// <param name="Target">Changes when the selected page target changes.</param>
/// <param name="Session">Changes for every new WebSocket attachment.</param>
/// <param name="Frame">Changes on main-frame navigation.</param>
/// <param name="ExecutionContext">Changes when JavaScript contexts are created or cleared.</param>
/// <param name="Document">Changes when navigation or context replacement invalidates owned UI.</param>
public readonly record struct SteamUiGenerations(
    long Browser,
    long Target,
    long Session,
    long Frame,
    long ExecutionContext,
    long Document);

/// <summary>Sanitized state of one persistent Steam UI target.</summary>
/// <param name="Role">The allowlisted target role.</param>
/// <param name="Health">Current transport health.</param>
/// <param name="Generations">Current replacement generations.</param>
/// <param name="TargetId">The current non-secret DevTools target id, when connected.</param>
/// <param name="LastFailure">The latest bounded diagnostic reason.</param>
/// <param name="OutstandingRequests">Requests currently awaiting a response.</param>
/// <param name="Subscribers">Patch subscriptions keeping reconnect active.</param>
public sealed record SteamUiTransportSnapshot(
    SteamUiTargetRole Role,
    SteamUiTransportHealth Health,
    SteamUiGenerations Generations,
    string? TargetId,
    string? LastFailure,
    int OutstandingRequests,
    int Subscribers);

/// <summary>Result of one bounded evaluation on a persistent Steam UI target.</summary>
/// <param name="Reachable">Whether a validated target accepted the request.</param>
/// <param name="Value">The by-value string result returned by JavaScript.</param>
/// <param name="Error">A bounded transport or JavaScript failure.</param>
/// <param name="Generations">The generations under which the result was produced.</param>
public readonly record struct SteamUiEvaluationResult(
    bool Reachable,
    string? Value,
    string? Error,
    SteamUiGenerations Generations)
{
    /// <summary>Creates an unavailable result without a JavaScript value.</summary>
    public static SteamUiEvaluationResult Unavailable(
        string error, SteamUiGenerations generations) => new(false, null, error, generations);
}

/// <summary>A bounded CDP notification emitted by a validated Steam UI channel.</summary>
/// <param name="Role">The target that emitted the notification.</param>
/// <param name="Method">The CDP notification method.</param>
/// <param name="ParametersJson">The notification parameters as bounded JSON.</param>
/// <param name="Generations">Generations after applying notification invalidation.</param>
public sealed record SteamUiNotification(
    SteamUiTargetRole Role,
    string Method,
    string ParametersJson,
    SteamUiGenerations Generations);

/// <summary>Persistent, generation-aware access to allowlisted Steam UI contexts.</summary>
public interface ISteamUiTransport : IAsyncDisposable
{
    /// <summary>Raised for bounded CDP notifications, including Runtime bindings.</summary>
    event EventHandler<SteamUiNotification>? NotificationReceived;

    /// <summary>Raised after any browser, target, session, frame, context, or document replacement.</summary>
    event EventHandler<SteamUiTransportSnapshot>? GenerationChanged;

    /// <summary>Keeps asynchronous reconnect active for an allowlisted target.</summary>
    /// <param name="role">The target needed by the subscriber.</param>
    /// <param name="cancellationToken">Cancels acquisition.</param>
    /// <returns>A lease that stops reconnect when the last subscriber releases it.</returns>
    ValueTask<IAsyncDisposable> SubscribeAsync(
        SteamUiTargetRole role, CancellationToken cancellationToken = default);

    /// <summary>Evaluates one bounded expression without exposing a general bridge to injected code.</summary>
    /// <param name="role">The allowlisted target.</param>
    /// <param name="expression">The repository-owned JavaScript expression.</param>
    /// <param name="timeout">The complete request deadline.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<SteamUiEvaluationResult> EvaluateAsync(
        SteamUiTargetRole role,
        string expression,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    /// <summary>Returns sanitized state for every target channel.</summary>
    IReadOnlyList<SteamUiTransportSnapshot> GetSnapshots();
}
