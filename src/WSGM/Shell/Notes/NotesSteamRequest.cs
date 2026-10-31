using System.Collections.Generic;
using WSGM.Core;

namespace WSGM.Shell;

/// <summary>One request from Steam's Notes page; each command reads the fields it needs.</summary>
internal sealed class NotesSteamRequest
{
    public uint? AppId { get; set; }
    public uint? ProcessId { get; set; }
    public string? Scope { get; set; }
    public string? PageId { get; set; }
    public long? Revision { get; set; }
    public string? ElementId { get; set; }
    public string? Title { get; set; }
    public string? Text { get; set; }
    public double? X { get; set; }
    public double? Y { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
    public NoteStroke? Stroke { get; set; }
    public List<NoteStroke>? Strokes { get; set; }
    public List<int>? Indices { get; set; }
    public List<string>? StrokeIds { get; set; }
    public bool? Pinned { get; set; }
    public int? Delta { get; set; }
    public int? Seconds { get; set; }
    public string? Asset { get; set; }
    public string? Capabilities { get; set; }
}
