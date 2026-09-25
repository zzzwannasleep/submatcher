using System.Text.Json;
using System.Text.Json.Serialization;

namespace SubMatcher.Gui;

/// <summary>UI preferences. Portable: settings.json next to the executable; unwritable folder = not remembered.</summary>
public sealed class AppSettings
{
    public bool TourDone { get; set; }
    public string Theme { get; set; } = "Default"; // Default | Light | Dark
    public string FontServer { get; set; } = "https://font.anibt.net";
    public string FontApiKey { get; set; } = "";
    public bool AutoSubset { get; set; }
    public string TgChannel { get; set; } = SubMatcher.Core.TgSubs.DefaultChannel;
    public string TgProxy { get; set; } = "";

    internal static string FilePath = Path.Combine(AppContext.BaseDirectory, "settings.json");

    public static AppSettings Load()
    {
        try { return JsonSerializer.Deserialize(File.ReadAllText(FilePath), SettingsJson.Default.AppSettings) ?? new(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, SettingsJson.Default.AppSettings)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

// Source-generated (de)serializer: no reflection, so it survives Native AOT trimming.
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJson : JsonSerializerContext;
