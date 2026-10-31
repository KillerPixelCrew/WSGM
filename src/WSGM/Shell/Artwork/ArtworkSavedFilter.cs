using System.Collections.Generic;

namespace WSGM.Shell;

internal sealed class ArtworkSavedFilter
{
    public string Tab { get; set; } = "";
    public List<string> Styles { get; set; } = [];
    public List<string> Dimensions { get; set; } = [];
    public List<string> Mimes { get; set; } = [];
    public bool Static { get; set; } = true;
    public bool Animated { get; set; } = true;
    public bool Adult { get; set; }
    public bool Humor { get; set; } = true;
    public bool Epilepsy { get; set; } = true;
    public bool Untagged { get; set; } = true;
}
