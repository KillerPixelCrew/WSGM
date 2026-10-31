namespace WSGM.Core;

/// <summary>How a source says one of its games launches, as far as importing it is concerned.</summary>
/// <param name="Label">What to call the route on screen.</param>
/// <param name="Validated">
///     Whether a validated route exists. Without one, Steam integration is never offered, because
///     there is nothing an acknowledgement could authorise.
/// </param>
/// <param name="Evidence">Why, in one sentence the review shows.</param>
/// <remarks>
///     This is the source's evidence, not a launch argument. The shortcut carries only the title's
///     key and the chosen mode; the launcher decides the route again from the process it actually
///     starts, because a package can change after its shortcut was written.
/// </remarks>
public sealed record GameLaunch(string Label, bool Validated, string Evidence);
