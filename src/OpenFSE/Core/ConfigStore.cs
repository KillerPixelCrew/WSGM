using System;
using System.IO;
using System.Text.Json;

namespace OpenFSE.Core;

public static class ConfigStore
{
    public static string ConfigPath => Path.Combine(Log.Directory, "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var config = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.AppConfig);
                if (config is not null)
                {
                    return config;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Failed to load config, using defaults", ex);
        }
        return new AppConfig();
    }

    public static void Save(AppConfig config)
    {
        Directory.CreateDirectory(Log.Directory);
        var json = JsonSerializer.Serialize(config, ConfigJsonContext.Default.AppConfig);
        var temp = ConfigPath + ".tmp";
        File.WriteAllText(temp, json);
        if (File.Exists(ConfigPath))
        {
            File.Replace(temp, ConfigPath, null);
        }
        else
        {
            File.Move(temp, ConfigPath);
        }
    }
}
