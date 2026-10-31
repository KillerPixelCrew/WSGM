using System.Collections.Generic;

namespace WSGM.Core;

/// <summary>
///     One injected library tab: a title + the exact app ids it should contain.
///     WSGM computes the ids (from filters, cards, or genres); Steam renders them.
/// </summary>
/// <param name="Id">Stable unique tab id (e.g. <c>wsgm-card-…</c>).</param>
/// <param name="Title">The tab's display title.</param>
/// <param name="AppIds">The app ids the tab shows.</param>
public sealed record InjectedTab(string Id, string Title, IReadOnlyList<long> AppIds);
