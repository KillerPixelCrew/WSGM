using System;
using System.Runtime.InteropServices;

namespace WSGM.Interop;

/// <summary>Raises a UI Automation notification, the event screen readers speak as an announcement.</summary>
/// <remarks>
///     The notification is raised on the window's host provider, which UI Automation supplies for any
///     window, so no element of the window's own automation tree has to exist yet. Requires Windows 10
///     1709 or later; earlier versions do not export the function and nothing is spoken.
/// </remarks>
internal static partial class UiaNotification
{
    /// <summary>NotificationKind_Other: a message with no more specific kind.</summary>
    private const int KindOther = 4;

    /// <summary>NotificationProcessing_ImportantMostRecent: speak it now, replacing an older one of the same activity.</summary>
    private const int ProcessingImportantMostRecent = 2;

    /// <summary>Raises the notification when an assistive technology is listening.</summary>
    /// <param name="window">The window the message belongs to.</param>
    /// <param name="text">What to speak.</param>
    /// <param name="activity">Groups messages so a newer one replaces an older one of the same activity.</param>
    /// <returns>Whether a listening client was sent the notification.</returns>
    internal static bool Raise(nint window, string text, string activity)
    {
        if (window == 0 || !UiaClientsAreListening())
        {
            return false;
        }

        nint provider = 0;
        try
        {
            if (UiaHostProviderFromHwnd(window, out provider) < 0 || provider == 0)
            {
                return false;
            }

            return UiaRaiseNotificationEvent(provider, KindOther, ProcessingImportantMostRecent, text, activity) >= 0;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        finally
        {
            if (provider != 0)
            {
                Marshal.Release(provider);
            }
        }
    }

    [LibraryImport("UIAutomationCore.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UiaClientsAreListening();

    /// <summary>Returns the host provider of a window as an owned IRawElementProviderSimple pointer.</summary>
    [LibraryImport("UIAutomationCore.dll")]
    private static partial int UiaHostProviderFromHwnd(nint hwnd, out nint provider);

    [LibraryImport("UIAutomationCore.dll")]
    private static partial int UiaRaiseNotificationEvent(nint provider, int notificationKind,
        int notificationProcessing, [MarshalAs(UnmanagedType.BStr)] string displayString,
        [MarshalAs(UnmanagedType.BStr)] string activityId);
}
