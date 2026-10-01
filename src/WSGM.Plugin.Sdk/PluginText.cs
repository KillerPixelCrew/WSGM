using System.Linq;

namespace WSGM.Plugin.Sdk;

/// <summary>Shared validation for plugin-supplied plain text.</summary>
public static class PluginText
{
    /// <summary>Checks that a value is nonblank and safe to display or log.</summary>
    /// <param name="value">Candidate text.</param>
    /// <param name="field">Field name used in the failure message.</param>
    /// <param name="error">The rejection reason, or null when the value is valid.</param>
    /// <returns>Whether the value is valid plain text.</returns>
    /// <remarks>The rule sets no length: labels and names are as long as the plugin wrote them.</remarks>
    public static bool TryValidate(string? value, string field, out string? error)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            error = $"{field} is required.";
            return false;
        }

        if (value.Any(IsUnsafe))
        {
            error = $"{field} contains a control or bidirectional-override character.";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>Checks that a value is valid plain text no longer than a maximum its declaration names.</summary>
    /// <param name="value">Candidate text.</param>
    /// <param name="maximumLength">The longest value the declaration accepts, in characters.</param>
    /// <param name="field">Field name used in the failure message.</param>
    /// <param name="error">The rejection reason, or null when the value is valid.</param>
    /// <returns>Whether the value is valid plain text within the declared maximum.</returns>
    public static bool TryValidate(string? value, int maximumLength, string field, out string? error)
    {
        if (!TryValidate(value, field, out error))
        {
            return false;
        }

        if (value!.Length <= maximumLength)
        {
            return true;
        }

        error = $"{field} exceeds its declared {maximumLength} characters.";
        return false;
    }

    /// <summary>Checks whether a character can corrupt a log line or visually reorder text.</summary>
    /// <param name="character">Character to inspect.</param>
    /// <returns>Whether the character is unsafe in plugin-supplied plain text.</returns>
    public static bool IsUnsafe(char character)
    {
        if (char.IsControl(character))
        {
            return true;
        }

        return character switch
        {
            '‎' or '‏' => true,
            >= '‪' and <= '‮' => true,
            >= '⁦' and <= '⁩' => true,
            _ => false
        };
    }
}
