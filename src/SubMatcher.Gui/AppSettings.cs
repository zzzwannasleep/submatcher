using System.Text.Json;

namespace SubMatcher.Gui;

/// <summary>UI preferences. Portable: settings.json next to the executable; unwritable folder = not remembered.</summary>
public sealed class AppSettings
{
    public bool TourDone { get; set; }
    public string Theme { get; set; } = "Default"; // Default | Light | Dark

    internal static string FilePath = Path.Combine(AppContext.BaseDirectory, "settings.json");

    public static AppSettings Load()
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
