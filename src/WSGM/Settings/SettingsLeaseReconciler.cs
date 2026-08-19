namespace WSGM.Settings;

internal enum SettingsLeaseAction
{
    None,
    Acquire,
    Release,
}

/// <summary>Serializes the Settings window's desired Steam Input ownership against
/// the owner claim registered by <c>SteamInputBlocker.AcquireFor</c>.</summary>
internal sealed class SettingsLeaseReconciler
{
    private bool _desired;
    private bool _claimed;
    private bool _busy;

    internal static bool ShouldHold(
        bool gameModeSurface,
        bool leaseEnabled,
        bool closed,
        bool minimized,
        bool active,
        bool hasChildSurface,
        bool handoffPending)
        => gameModeSurface && leaseEnabled && !closed && !minimized
           && (handoffPending || active || hasChildSurface);

    internal SettingsLeaseAction SetDesired(bool desired)
    {
        _desired = desired;
        return Next();
    }

    internal SettingsLeaseAction InheritClaim(bool leaseApplied)
    {
        _desired = true;
        _claimed = true;
        if (!leaseApplied && !_busy)
        {
            _busy = true;
            return SettingsLeaseAction.Acquire;
        }
        return Next();
    }

    internal SettingsLeaseAction CompleteAcquireFor()
    {
        // AcquireFor registers the owner BEFORE it attempts the native acquire.
        // A failed native acquire therefore still leaves a real claim that must
        // be released when the Settings surface goes away.
        _claimed = true;
        _busy = false;
        return Next();
    }

    internal SettingsLeaseAction CompleteRelease()
    {
        _claimed = false;
        _busy = false;
        return Next();
    }

    private SettingsLeaseAction Next()
    {
        if (_busy || _desired == _claimed)
        {
            return SettingsLeaseAction.None;
        }

        _busy = true;
        return _desired ? SettingsLeaseAction.Acquire : SettingsLeaseAction.Release;
    }
}
