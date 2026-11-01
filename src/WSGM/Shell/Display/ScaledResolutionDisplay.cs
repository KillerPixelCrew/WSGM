namespace WSGM.Shell;

internal sealed record ScaledResolutionDisplay(string Path, string Name)
{
    public override string ToString()
    {
        return Name;
    }
}
