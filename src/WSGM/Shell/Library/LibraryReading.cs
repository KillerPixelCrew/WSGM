using System.Collections.Generic;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>What a finished sync saved and found, projected without another scan.</summary>
/// <param name="Config">The configuration the sync saved.</param>
/// <param name="Present">The content ids of the cards its scan found mounted.</param>
internal sealed record LibraryReading(AppConfig Config, IReadOnlySet<string> Present);
