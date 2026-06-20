using System.Text.Json;

namespace Strama.UI.Services;

/// <summary>
/// JSON load/save for <see cref="ClientSettings"/>. Stored under the OS's
/// per-user application-data folder — on Windows that's
/// <c>%APPDATA%\Strama\config.json</c>; on Linux/macOS .NET 8's
/// <see cref="Environment.SpecialFolder.ApplicationData"/> resolves to
/// <c>~/.config/Strama/</c>, no per-OS code paths needed.
/// Missing or corrupt files yield defaults — the app never refuses to start
/// because of bad config.
/// </summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Strama",
        "config.json");

    public static ClientSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new ClientSettings();
            string json = File.ReadAllText(FilePath);
            var loaded = JsonSerializer.Deserialize<ClientSettings>(json);
            if (loaded is not null)
                loaded.RecentSessions ??= [];
            return loaded ?? new ClientSettings();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SettingsStore] Failed to load {FilePath}: {ex.Message}");
            return new ClientSettings();
        }
    }

    public static void Save(ClientSettings settings)
    {
        try
        {
            string dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            string json = JsonSerializer.Serialize(settings, JsonOpts);
            File.WriteAllText(FilePath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SettingsStore] Failed to save {FilePath}: {ex.Message}");
        }
    }
}
