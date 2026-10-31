namespace WSGM.Shell;

/// <summary>One named saved-endpoint choice.</summary>
/// <param name="Id">Windows endpoint identifier retained in the audio preference.</param>
/// <param name="Name">Display label; identity comparisons must use Id.</param>
public sealed record AudioEndpointOption(string Id, string Name)
{
    /// <inheritdoc />
    public override string ToString()
    {
        return Name;
    }
}
