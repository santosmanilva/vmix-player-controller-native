using System.IO;
using System.Text.Json;
using System.Net;
using System.Net.Sockets;

namespace VMixPlayerController;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public string FilePath { get; }
    public string LastError { get; private set; } = "";

    public SettingsService(string? filePath = null)
    {
        var portable = Path.Combine(AppContext.BaseDirectory, "vmix-player-controller.settings.json");
        FilePath = filePath ?? (CanWriteFolder(AppContext.BaseDirectory)
            ? portable
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "vMix Player Controller", "settings.json"));
    }

    public AppSettings Load()
    {
        foreach (var path in new[] { FilePath, FilePath + ".bak" })
        {
            if (!File.Exists(path)) continue;
            try
            {
                var settings = Import(path);
                if (path != FilePath) LogService.Write("WARN", "Configuración recuperada de la copia anterior.");
                return settings;
            }
            catch (Exception ex) { LogService.Write("WARN", $"No se pudo cargar la configuración: {ex.Message}"); }
        }
        return Normalize(new AppSettings());
    }

    public bool Save(AppSettings settings)
    {
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(Normalize(settings), JsonOptions));
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, FilePath + ".bak");
            else File.Move(temporary, FilePath);
            LastError = "";
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            LogService.Write("WARN", $"No se pudo guardar la configuración: {ex.Message}");
            return false;
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
    }

    public void Export(AppSettings settings, string destination) =>
        File.WriteAllText(destination, JsonSerializer.Serialize(Normalize(settings), JsonOptions));

    public AppSettings Import(string source) => Normalize(
        JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(source), JsonOptions)
        ?? throw new InvalidDataException("El perfil no contiene una configuración."));

    internal static AppSettings Normalize(AppSettings settings)
    {
        settings.Players = (settings.Players ?? []).Take(4).Select(p => p ?? new PlayerConfig()).ToList();
        while (settings.Players.Count < 4) settings.Players.Add(new PlayerConfig());
        settings.VmixA ??= new EndpointConfig();
        settings.VmixB ??= new EndpointConfig();
        foreach (var endpoint in new[] { settings.VmixA, settings.VmixB })
        {
            endpoint.Host = endpoint.Host?.Trim() ?? "127.0.0.1";
            if (!IsValidHost(endpoint.Host)) throw new InvalidDataException("La dirección de vMix no es válida.");
            if (endpoint.Port is < 1 or > 65535) throw new InvalidDataException("El puerto debe estar entre 1 y 65535.");
        }
        settings.AtemHost = settings.AtemHost?.Trim() ?? "";
        if (!IPAddress.TryParse(settings.AtemHost, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
            throw new InvalidDataException("La ATEM requiere una dirección IPv4 válida.");
        foreach (var player in settings.Players)
        {
            player.VmixName ??= "";
            player.InputKey ??= "";
            player.SelectedMixes = (player.SelectedMixes ?? []).Where(mix => mix is >= 0 and <= 3).Distinct().ToList();
            if (player.AtemMe is < -1 or > 1) player.AtemMe = 0;
            if (player.AtemInputId < 0) player.AtemInputId = 0;
        }
        return settings;
    }

    internal static bool IsValidHost(string host) => !string.IsNullOrWhiteSpace(host) &&
        Uri.CheckHostName(host) != UriHostNameType.Unknown;

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
