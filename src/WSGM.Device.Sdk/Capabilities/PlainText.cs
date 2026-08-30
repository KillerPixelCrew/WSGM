namespace WSGM.Device.Sdk.Capabilities;

/// <summary>
/// The one rule for untrusted plain text crossing the plugin boundary — a capability's custom label
/// and a <see cref="CapabilityValueKind.Text"/> value both use it.
/// </summary>
/// <remarks>
/// The bound is on accidental UI corruption, not on the plugin. A plugin is trusted .NET code that
/// DeviceHost loads and runs, already holding WMI, HID, and EC access; it is not an attacker and
/// this is not a privilege boundary. What the rule actually prevents is a malformed string
/// corrupting a log line, hiding its own tail from whoever reads it, or rendering as something other
/// than what it says.
/// <para>
/// Text validated here is never a format string, never markup, and never a localization key. WSGM
/// cannot translate text it did not author, so it renders in whatever language the plugin wrote it.
/// </para>
/// </remarks>
public static class PlainText
{
    /// <summary>
    /// Whether a value is safe to render and log.
    /// </summary>
    /// <param name="value">The untrusted text.</param>
    /// <param name="maximumLength">Longest accepted length, in characters.</param>
    /// <param name="field">Field name used in the failure message.</param>
    /// <param name="error">The reason it is not, when the result is <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the value is safe.</returns>
    public static bool TryValidate(
        string? value,
        int maximumLength,
        string field,
        out string? error
    )
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            error = $"{field} is required.";
            return false;
        }

        if (value.Length > maximumLength)
        {
            error = $"{field} exceeds {maximumLength} characters.";
            return false;
        }

        foreach (char c in value)
        {
            if (IsUnsafe(c))
            {
                error = $"{field} contains a control or bidirectional-override character.";
                return false;
            }
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Whether a value is usable as a stable identifier.
    /// </summary>
    /// <param name="value">The candidate identifier.</param>
    /// <param name="maximumLength">Longest accepted length, in characters.</param>
    /// <returns><see langword="true"/> when the value is a legal identifier.</returns>
    /// <remarks>
    /// Identifiers are matched, logged, and used as keys, so they are restricted to a shape that
    /// survives all three: letters, digits, dot, underscore, and hyphen. Uppercase is allowed
    /// because the identifiers WSGM itself sends are PascalCase.
    /// </remarks>
    public static bool IsIdentifier(string? value, int maximumLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length > maximumLength)
        {
            return false;
        }

        foreach (char c in value)
        {
            bool legal =
                c is >= 'a' and <= 'z'
                || c is >= 'A' and <= 'Z'
                || c is >= '0' and <= '9'
                || c is '.' or '_' or '-';
            if (!legal)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether a character would corrupt a log line or misrepresent the rest of the string.
    /// </summary>
    /// <param name="c">The character to judge.</param>
    /// <returns><see langword="true"/> when the character must be rejected.</returns>
    /// <remarks>
    /// Control characters break log lines and can hide the remainder of a value from a reviewer.
    /// The bidirectional formatting characters are the ones that let text render in an order other
    /// than the one it is written in, so a label can claim to say something it does not.
    /// </remarks>
    public static bool IsUnsafe(char c)
    {
        if (char.IsControl(c))
        {
            return true;
        }

        return c switch
        {
            // LRM and RLM.
            '‎' or '‏' => true,
            // The LRE/RLE/PDF/LRO/RLO embedding and override set.
            >= '‪' and <= '‮' => true,
            // The isolates, which do the same job as the overrides above.
            >= '⁦' and <= '⁩' => true,
            _ => false,
        };
    }
}
