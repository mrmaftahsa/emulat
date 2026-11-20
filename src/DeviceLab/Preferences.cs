using System.IO;
using System.Text.Json;

namespace DeviceLab;

internal sealed class Preferences
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    internal static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeviceLab");
    private static readonly string FilePath = Path.Combine(Folder, "settings.json");

    public string? SdkDirectory { get; set; }
    public Dictionary<string, LocationProfile> Locations { get; set; } = new(StringComparer.Ordinal);

    public static Preferences Load(out string? warning)
    {
        warning = null;

        try
        {
            if (File.Exists(FilePath))
            {
                var settings = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(FilePath));
                if (settings is not null)
                {
                    settings.Locations ??= new(StringComparer.Ordinal);
                    return settings;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            warning = "The saved settings could not be read. Device Lab opened with default settings.";
        }

        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        var temporaryFile = Path.Combine(Folder, Path.GetRandomFileName());
        try
        {
            File.WriteAllText(temporaryFile, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(temporaryFile, FilePath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryFile);
        }
    }
}

internal sealed record LocationProfile(double Latitude, double Longitude);
