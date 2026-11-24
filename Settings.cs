using System.Text.Json;

namespace LabelPrinter;

/// <summary>Remembers the last choices, in %AppData%\LabelPrinter\settings.json.</summary>
internal sealed class Settings
{
    public string? Printer { get; set; }
    public string? DocumentType { get; set; }
    public string? LastFolder { get; set; }

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.Name, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { /* corrupt or unreadable: start fresh */ }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* not worth interrupting the user */ }
    }
}
