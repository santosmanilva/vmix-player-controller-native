using System.IO;
using System.Text.Json;

namespace VMixPlayerController;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public string FilePath { get; }

    public SettingsService()
    {
        var portable = Path.Combine(AppContext.BaseDirectory, "vmix-player-controller.settings.json");
        FilePath = CanWriteFolder(AppContext.BaseDirectory)
            ? portable
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "vMix Player Controller", "settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return Normalize(new AppSettings());
            return Normalize(JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new AppSettings());
        }
        catch (Exception ex)
        {
            LogService.Write("WARN", $"No se pudo cargar la configuración: {ex.Message}");
            return Normalize(new AppSettings());
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Normalize(settings), JsonOptions));
        }
        catch (Exception ex)
        {
            LogService.Write("WARN", $"No se pudo guardar la configuración: {ex.Message}");
        }
    }

    public void Export(AppSettings settings, string destination)
    {
        File.WriteAllText(destination, JsonSerializer.Serialize(Normalize(settings), JsonOptions));
    }

    public AppSettings Import(string source) => Normalize(JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(source), JsonOptions) ?? new AppSettings());

    private static AppSettings Normalize(AppSettings settings)
    {
        settings.Players ??= [];
        while (settings.Players.Count < 4) settings.Players.Add(new PlayerConfig());
        if (settings.Players.Count > 4) settings.Players = settings.Players.Take(4).ToList();
        settings.VmixA ??= new EndpointConfig();
        settings.VmixB ??= new EndpointConfig();
        return settings;
    }

    private static bool CanWriteFolder(string folder)
    {
        try
        {
            var probe = Path.Combine(folder, $".vmix-write-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }
}
