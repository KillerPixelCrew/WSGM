using System;

namespace WSGM.Core;

/// <summary>One configuration field edited by Settings and by another WSGM surface.</summary>
/// <param name="Name">Stable dotted configuration key used for dirty-field tracking.</param>
/// <param name="Read">Reads the field from a supplied snapshot without mutating it.</param>
/// <param name="Write">Writes a value of that field's expected runtime type into the supplied configuration.</param>
internal sealed record WsgmSharedSetting(string Name, Func<AppConfig, object> Read, Action<AppConfig, object> Write);
