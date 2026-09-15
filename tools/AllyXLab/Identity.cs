using System.Diagnostics;
using System.Management;

namespace WSGM.AllyXLab;

internal sealed record Identity(string Manufacturer, string Model, string Board, string Sku, string Bios, string Ec, string Os)
{
    internal string Fingerprint => SessionLog.Token($"{Manufacturer}|{Model}|{Board}|{Sku}|{Bios}|{Ec}");
    // A bring-up family gate, deliberately not the production plugin's firmware allowlist.
    internal bool IsAllyX => Manufacturer.Contains("ASUS", StringComparison.OrdinalIgnoreCase)
        || Manufacturer.Contains("ASUSTeK", StringComparison.OrdinalIgnoreCase);
    internal bool MatchesModel => IsAllyX
        && (Model.StartsWith("ROG Ally X RC72LA", StringComparison.OrdinalIgnoreCase) || Model.Equals("RC72LA", StringComparison.OrdinalIgnoreCase))
        && (Board.Equals("RC72LA", StringComparison.OrdinalIgnoreCase) || Board.Equals("RC72L", StringComparison.OrdinalIgnoreCase))
        && !string.IsNullOrWhiteSpace(Bios) && !string.IsNullOrWhiteSpace(Sku);

    internal static Identity Read()
    {
        Dictionary<string, string> ReadClass(string name, params string[] fields)
        {
            using var search = new ManagementObjectSearcher("root\\CIMV2", $"SELECT {string.Join(',', fields)} FROM {name}");
            search.Options.Timeout = TimeSpan.FromSeconds(5);
            using var rows = search.Get();
            foreach (ManagementObject item in rows)
            {
                using (item)
                {
                    return fields.ToDictionary(field => field, field => Convert.ToString(item[field])?.Trim() ?? "");
                }
            }
            return fields.ToDictionary(field => field, _ => "");
        }
        var system = ReadClass("Win32_ComputerSystem", "Manufacturer", "Model", "SystemSKUNumber");
        var board = ReadClass("Win32_BaseBoard", "Product");
        var bios = ReadClass("Win32_BIOS", "SMBIOSBIOSVersion", "EmbeddedControllerMajorVersion", "EmbeddedControllerMinorVersion");
        return new(system["Manufacturer"], system["Model"], board["Product"], system["SystemSKUNumber"],
            bios["SMBIOSBIOSVersion"], bios["EmbeddedControllerMajorVersion"] + "." + bios["EmbeddedControllerMinorVersion"], Environment.OSVersion.VersionString);
    }

    internal static string[] ConflictingApps()
    {
        List<string> names = [];
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    string name = p.ProcessName;
                    if (name.Equals("WSGM", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("HandheldCompanion", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("ArmouryCrate", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("GHelper", StringComparison.OrdinalIgnoreCase))
                    {
                        names.Add(name);
                    }
                }
                catch (InvalidOperationException) { }
            }
        }
        return names.Distinct().Order().ToArray();
    }
}
