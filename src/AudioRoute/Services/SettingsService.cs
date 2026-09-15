using System;
using System.IO;
using System.Text.Json;
using AudioRoute.Models;

namespace AudioRoute.Services;

/// <summary>
/// Loads / saves user preferences from %LocalAppData%\AudioRoute\settings.json.
/// Persistence is fire-and-forget — corrupt files fall back to defaults.
/// </summary>
public sealed class SettingsService
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AudioRoute");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    public AppSettings Settings { get; private set; } = new();

    public event Action? Changed;

    public void Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var s = JsonSerializer.Deserialize<AppSettings>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                });
                if (s != null) Settings = s;
            }
        }
        catch
        {
            // corrupt file: ignore and keep defaults
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var json = JsonSerializer.Serialize(Settings, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            });
            File.WriteAllText(FilePath, json);
        }
        catch
        {
            // best effort
        }
    }

    public void Update(Action<AppSettings> mutate)
    {
        mutate(Settings);
        Save();
        Changed?.Invoke();
    }
}
