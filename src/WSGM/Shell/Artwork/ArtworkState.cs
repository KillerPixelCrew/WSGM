using System.Collections.Generic;

namespace WSGM.Shell;

internal sealed class ArtworkState
{
    public List<ArtworkGameLink> Games { get; set; } = [];
    public List<ArtworkSavedFilter> Filters { get; set; } = [];
}
