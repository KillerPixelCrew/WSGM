using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace WSGM.Core;

/// <summary>Stages a native config without changing unknown keys or the immutable package cache.</summary>
/// <remarks>
///     Native keys and value constraints come from the upstream files cited in docs/game-tools.md:
///     DXVK dxvk.conf, dgVoodoo's shipped dgVoodoo.conf, ReShade runtime.cpp/runtime_gui.cpp,
///     and Special K config.cpp/widgets/hdr.cpp. Shared OSD keys are never written to a per-game proxy INI.
/// </remarks>
internal sealed class GameToolConfiguration : IDisposable
{
    private readonly string _root;
    internal Dictionary<string, string> Values { get; }
    internal Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    private GameToolConfiguration(string root, Dictionary<string, string> values)
    {
        _root = root;
        Values = values;
    }

    internal static GameToolConfiguration Prepare(GameToolDefinition tool, string directory, GameToolPackage package,
        IReadOnlyDictionary<string, string> requested, GameToolManifest? current)
    {
        var root = Path.Combine(Path.GetTempPath(), "WSGM-tool-config-" + Guid.NewGuid().ToString("N"));
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var stage = new GameToolConfiguration(root, values);
        Directory.CreateDirectory(root);
        try
        {
            var name = tool.PrimaryConfig;
            var destination = Path.Combine(root, name);
            var existing = Path.Combine(directory, name);
            var stock = Path.Combine(package.Root, name);
            if (File.Exists(existing))
            {
                File.Copy(existing, destination);
            }
            else if (File.Exists(stock) && new FileInfo(stock).Length > 0)
            {
                File.Copy(stock, destination);
            }
            else
            {
                File.WriteAllText(destination, "", tool.ConfigEncoding == "utf16le" ? Encoding.Unicode : new UTF8Encoding(false));
            }
            File.SetAttributes(destination, FileAttributes.Normal);
            // The package's own file only supplies a format: WSGM's defaults win over it, while a file already
            // in the game folder holds the user's values and is left alone.
            var freshFromPackage = !File.Exists(existing);
            var fields = tool.Settings.Where(setting => setting.Scope == "game").ToArray();
            var native = GameToolIni.Read(destination, fields.Select(setting => (setting.Section, setting.Key)));
            List<GameToolIniEdit> edits = [];
            foreach (var field in fields)
            {
                if (field.UnavailableReason.Length > 0)
                {
                    continue;
                }
                var explicitValue = requested.GetValueOrDefault(field.Id);
                var nativeValue = freshFromPackage ? null : native.GetValueOrDefault(GameToolIni.Id(field.Section, field.Key));
                var value = explicitValue ?? (nativeValue is null ? null : field.FromRaw(nativeValue))
                    ?? current?.Settings.GetValueOrDefault(field.Id) ?? field.Default;
                if (!field.Accepts(value))
                {
                    throw new GameToolException("Invalid value for " + field.Label + ".");
                }

                values[field.Id] = value;
                if (explicitValue is null && (nativeValue is not null || field.OmitDefault))
                {
                    continue;
                }

                edits.Add(new GameToolIniEdit(field.Section, field.Key, field.Raw(value), CreateMissing: true));
                foreach (var additional in field.Options.FirstOrDefault(option => option.Value == value)?.AdditionalKeys
                             ?? new Dictionary<string, string>())
                {
                    edits.Add(new GameToolIniEdit(field.Section, additional.Key, additional.Value, CreateMissing: true));
                }
            }

            GameToolIni.Apply(destination, edits);
            stage.Files[name] = destination;
            return stage;
        }
        catch
        {
            stage.Dispose();
            throw;
        }
    }

    public void Dispose() => GameToolStorage.DeleteTree(_root);
}
