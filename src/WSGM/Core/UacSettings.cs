using System;
using Microsoft.Win32;

namespace WSGM.Core;

/// <summary>
///     Reads and sets the machine's UAC prompting level — the same values the
///     Windows UAC slider writes. "Minimum" here means the slider's lowest position
///     ("Never notify"): admins auto-elevate silently, which is what lets WSGM start
///     the launcher elevated without a prompt on every boot.
///     UAC itself stays ENABLED (EnableLUA=1). This class never touches EnableLUA:
///     setting it to 0 turns off Windows' whole integrity/AppContainer model, breaks
///     Store/UWP apps, and needs a reboot. Changing only the prompt behavior takes
///     effect immediately.
///     Writing these values needs administrator rights (HKLM), so the change runs in a
///     short-lived elevated instance of WSGM — one prompt to silence future ones.
/// </summary>
public static class UacSettings
{
    private const string PolicyKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string ConsentPromptBehaviorAdmin = "ConsentPromptBehaviorAdmin";
    private const string PromptOnSecureDesktop = "PromptOnSecureDesktop";

    // Windows defaults ("Notify me only when apps try to make changes").
    private const int DefaultConsentPrompt = 5;
    private const int DefaultSecureDesktop = 1;

    /// <summary>Reads the current UAC policy without changing it.</summary>
    /// <returns>A readable snapshot, or an unreadable sentinel when access fails.</returns>
    public static UacState Read()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(PolicyKey);
            if (key is null)
            {
                return new UacState(false, DefaultConsentPrompt, DefaultSecureDesktop);
            }

            return new UacState(
                true,
                key.GetValue(ConsentPromptBehaviorAdmin) as int? ?? DefaultConsentPrompt,
                key.GetValue(PromptOnSecureDesktop) as int? ?? DefaultSecureDesktop);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read UAC policy: {ex.Message}");
            return new UacState(false, DefaultConsentPrompt, DefaultSecureDesktop);
        }
    }

    /// <summary>
    ///     Runs in the ELEVATED instance: writes the policy values, snapshotting
    ///     the previous ones into config first so the change can be undone exactly.
    /// </summary>
    public static bool ApplyDirect(ConfigStore store, bool disablePrompts)
    {
        try
        {
            using var operation = new WindowsPolicyOperation("UacSettings");
            // Strict load, not Load(): this is a read-modify-write of the snapshot
            // uninstall restores UAC from. An unreadable config must abort through the
            // catch below, never re-capture the ALREADY-MODIFIED prompt levels as the
            // pre-WSGM state and then save defaults over every other snapshot.
            var current = Read();
            var config = store.Update(fresh =>
            {
                if (disablePrompts && !fresh.PreviousUacSnapshotCaptured
                                   && current is { Readable: true, PromptsDisabled: false })
                {
                    fresh.PreviousUacSnapshotCaptured = true;
                    fresh.PreviousUacConsentPrompt = current.ConsentPrompt;
                    fresh.PreviousUacSecureDesktop = current.SecureDesktop;
                }

                return true;
            });

            if (disablePrompts)
            {
                using var key = Registry.LocalMachine.CreateSubKey(PolicyKey)
                                ?? throw new InvalidOperationException("Cannot open UAC policy key");
                key.SetValue(ConsentPromptBehaviorAdmin, 0, RegistryValueKind.DWord);
                key.SetValue(PromptOnSecureDesktop, 0, RegistryValueKind.DWord);
                Log.Info("UAC prompts disabled (ConsentPromptBehaviorAdmin=0, PromptOnSecureDesktop=0).");
            }
            else
            {
                var consent = config.PreviousUacSnapshotCaptured
                    ? config.PreviousUacConsentPrompt
                    : DefaultConsentPrompt;
                var desktop = config.PreviousUacSnapshotCaptured
                    ? config.PreviousUacSecureDesktop
                    : DefaultSecureDesktop;

                using var key = Registry.LocalMachine.CreateSubKey(PolicyKey)
                                ?? throw new InvalidOperationException("Cannot open UAC policy key");
                key.SetValue(ConsentPromptBehaviorAdmin, consent, RegistryValueKind.DWord);
                key.SetValue(PromptOnSecureDesktop, desktop, RegistryValueKind.DWord);

                store.Update(fresh =>
                {
                    if (fresh.PreviousUacSnapshotCaptured == config.PreviousUacSnapshotCaptured
                        && fresh.PreviousUacConsentPrompt == config.PreviousUacConsentPrompt
                        && fresh.PreviousUacSecureDesktop == config.PreviousUacSecureDesktop)
                    {
                        fresh.PreviousUacSnapshotCaptured = false;
                    }

                    return true;
                });
                Log.Info(
                    $"UAC prompts restored (ConsentPromptBehaviorAdmin={consent}, PromptOnSecureDesktop={desktop}).");
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Failed to write UAC policy", ex);
            return false;
        }
    }

    /// <summary>
    ///     Requests the change from the non-elevated UI: relaunches WSGM
    ///     elevated for the registry write and waits for it. Returns false if elevation
    ///     was declined or the write failed.
    /// </summary>
    public static bool RequestChange(bool disablePrompts)
    {
        return SelfElevation.RunElevatedAction(disablePrompts ? "--set-uac-silent" : "--restore-uac", "UAC change");
    }

    /// <summary>Snapshot of the machine UAC policy required for an exact restore.</summary>
    public sealed record UacState
    {
        /// <summary>Creates a UAC policy snapshot.</summary>
        /// <param name="readable">Whether the policy key could be read.</param>
        /// <param name="consentPrompt">The administrator consent-prompt policy value.</param>
        /// <param name="secureDesktop">The secure-desktop policy value.</param>
        public UacState(bool readable, int consentPrompt, int secureDesktop)
        {
            Readable = readable;
            ConsentPrompt = consentPrompt;
            SecureDesktop = secureDesktop;
        }

        /// <summary>Gets whether the policy values could be read.</summary>
        public bool Readable { get; }

        /// <summary>Gets the administrator consent-prompt policy value.</summary>
        public int ConsentPrompt { get; }

        /// <summary>Gets the secure-desktop policy value.</summary>
        public int SecureDesktop { get; }

        /// <summary>True when elevation happens silently for administrators.</summary>
        public bool PromptsDisabled => Readable && ConsentPrompt == 0;
    }
}
