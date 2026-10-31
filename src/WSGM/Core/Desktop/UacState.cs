namespace WSGM.Core;

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
