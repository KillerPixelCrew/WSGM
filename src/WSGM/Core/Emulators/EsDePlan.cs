namespace WSGM.Core;

/// <summary>Everything ES-DE is configured with: its systems, the ROM folder's links and the systems left out.</summary>
/// <param name="Systems">Every system the ROM folders hold, shown or left out, in WSGM's system order.</param>
/// <param name="Links">The links the ES-DE ROM folder holds, one per folder of a shown system.</param>
internal sealed record EsDePlan(EsDeSystem[] Systems, EsDeLink[] Links);
