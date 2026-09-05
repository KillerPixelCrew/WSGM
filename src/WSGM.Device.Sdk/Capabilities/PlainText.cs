namespace WSGM.Device.Sdk.Capabilities;

/// <summary>
/// The one rule for plugin-supplied plain text — a capability's custom label
/// and a <see cref="CapabilityValueKind.Text"/> value both use it.
/// </summary>
/// <remarks>
/// The rule keeps labels and values bounded and single-line so logs and controls present the same
/// text.
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
    /// <param name="value">The text to validate.</param>
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
    /// Control and bidirectional formatting characters make logs and controls present different
    /// shapes, so the shared text contract excludes them.
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
