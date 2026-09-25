using System.IO;
using System.Text;

namespace WSGM.DeviceLab.Gui;

/// <summary>The help and licence text embedded in the executable.</summary>
internal static class WizardHelp
{
    /// <summary>The README, this project's licence and the third-party notices, in that order.</summary>
    /// <returns>The text.</returns>
    public static string Text()
    {
        StringBuilder text = new();
        foreach (var name in new[] { "README.md", "LICENSE", "THIRD_PARTY_NOTICES.md" })
        {
            using var stream = typeof(WizardHelp).Assembly.GetManifestResourceStream($"WSGM.DeviceLab.Help.{name}");
            if (stream is null)
            {
                continue;
            }

            using StreamReader reader = new(stream);
            text.AppendLine(reader.ReadToEnd()).AppendLine();
        }

        return text.ToString();
    }
}
