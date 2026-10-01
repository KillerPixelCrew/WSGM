using System.Text;

namespace WSGM.Core;

/// <summary>Windows argv quoting shared by recovery and the contained launch wrapper.</summary>
internal static class WindowsCommandLine
{
    internal static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"']) < 0)
        {
            return arg;
        }

        var sb = new StringBuilder(arg.Length + 2);
        sb.Append('"');
        var backslashes = 0;
        foreach (var c in arg)
        {
            switch (c)
            {
                case '\\':
                    backslashes++;
                    continue;
                case '"':
                    sb.Append('\\', backslashes * 2 + 1);
                    break;
                default:
                    sb.Append('\\', backslashes);
                    break;
            }

            sb.Append(c);
            backslashes = 0;
        }

        sb.Append('\\', backslashes * 2);
        sb.Append('"');
        return sb.ToString();
    }
}
