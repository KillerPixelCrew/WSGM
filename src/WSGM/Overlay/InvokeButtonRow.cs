using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;

namespace WSGM.Overlay;

/// <summary>
///     The plain Run button of a capability or descriptor row: one press at a time, disabled while it runs,
///     and focus handed back to it when it was focused and nothing else took focus meanwhile.
/// </summary>
internal sealed class InvokeButtonRow
{
    /// <summary>Creates the button.</summary>
    /// <param name="content">The button's content.</param>
    /// <param name="canInvoke">Reads whether the row's current state accepts a press.</param>
    /// <param name="invoke">Runs the action; it handles its own failures.</param>
    internal InvokeButtonRow(object content, Func<bool> canInvoke, Func<Task> invoke)
    {
        Button = new Button { Content = content };
        Button.Click += async (_, _) =>
        {
            if (Invoking || !canInvoke())
            {
                return;
            }

            var restoreFocus = Button.IsFocused;
            var root = TopLevel.GetTopLevel(Button);
            Invoking = true;
            Button.IsEnabled = false;
            try
            {
                await invoke();
            }
            finally
            {
                Invoking = false;
                Button.IsEnabled = canInvoke();
                if (restoreFocus && Button.IsEnabled && Button.IsEffectivelyVisible
                    && ReferenceEquals(root, TopLevel.GetTopLevel(Button))
                    && root?.FocusManager?.GetFocusedElement() is null)
                {
                    Button.Focus(NavigationMethod.Directional);
                }
            }
        };
    }

    /// <summary>The button the row places in its setting container.</summary>
    internal Button Button { get; }

    /// <summary>Whether a press is running.</summary>
    internal bool Invoking { get; private set; }
}
