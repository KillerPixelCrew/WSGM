using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using WSGM.Interop;

namespace WSGM.Controls;

/// <summary>Speaks short messages through the running screen reader.</summary>
/// <remarks>
///     A message is a UI Automation notification on the window that shows it, and only an active window
///     speaks, so a background Settings window or a closed sheet stays silent. A message in the same
///     activity replaces an older one that has not been spoken yet, so a held slider says only where it
///     stopped. Nothing is raised when no assistive technology is listening.
/// </remarks>
internal static class ScreenReader
{
    /// <summary>Defines <see cref="GetAnnouncesText" />: the TextBlock speaks each new text it shows.</summary>
    public static readonly AttachedProperty<bool> AnnouncesTextProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, bool>("AnnouncesText", typeof(ScreenReader));

    static ScreenReader()
    {
        AnnouncesTextProperty.Changed.AddClassHandler<TextBlock>(static (block, change) =>
        {
            if (change.GetNewValue<bool>())
            {
                block.PropertyChanged += OnAnnouncingTextChanged;
            }
            else
            {
                block.PropertyChanged -= OnAnnouncingTextChanged;
            }
        });
    }

    /// <summary>Whether the TextBlock speaks each new text it shows, such as a status line or a result.</summary>
    /// <param name="block">The TextBlock.</param>
    /// <returns>Whether it announces.</returns>
    public static bool GetAnnouncesText(TextBlock block)
    {
        return block.GetValue(AnnouncesTextProperty);
    }

    /// <summary>Makes the TextBlock speak each new text it shows while it is loaded and visible.</summary>
    /// <param name="block">The TextBlock.</param>
    /// <param name="value">Whether it announces.</param>
    public static void SetAnnouncesText(TextBlock block, bool value)
    {
        block.SetValue(AnnouncesTextProperty, value);
    }

    /// <summary>Speaks a message for the window that shows <paramref name="source" />, when that window is active.</summary>
    /// <param name="source">A control in the window the message belongs to.</param>
    /// <param name="text">What to speak; empty speaks nothing.</param>
    /// <param name="activity">The kind of message; a newer one of the same kind replaces an unspoken older one.</param>
    internal static void Announce(Visual source, string? text, string activity)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Announce(source, text, activity));
            return;
        }

        if (string.IsNullOrWhiteSpace(text)
            || TopLevel.GetTopLevel(source) is not WindowBase { IsActive: true } window
            || window.TryGetPlatformHandle()?.Handle is not { } handle)
        {
            return;
        }

        UiaNotification.Raise(handle, text, activity);
    }

    private static void OnAnnouncingTextChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        // Only a change the user can see: a binding filling the block as its page loads is not news.
        if (e.Property == TextBlock.TextProperty && sender is TextBlock { IsLoaded: true, IsEffectivelyVisible: true }
                block)
        {
            Announce(block, e.GetNewValue<string?>(), "wsgm.status");
        }
    }
}
