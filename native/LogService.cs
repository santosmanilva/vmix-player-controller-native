using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace VMixPlayerController;

public static class LogService
{
    private static readonly ConcurrentQueue<string> Lines = new();
    public static event Action<string>? LineAdded;

    public static IReadOnlyList<string> Snapshot() => Lines.ToArray();

    public static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff}  {level,-5}  {message}";
        Lines.Enqueue(line);
        while (Lines.Count > 1000) Lines.TryDequeue(out _);
        LineAdded?.Invoke(line);
    }

    public static string CreateSupportBundle(string folder, AppSettings settings, IEnumerable<VmixConnectionService> services, AtemConnectionService atem)
    {
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, $"diagnostico-vmix-player-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        var sb = new StringBuilder();
        sb.AppendLine("vMix Player Controller · diagnóstico local");
        sb.AppendLine($"Fecha: {DateTime.Now:O}");
        sb.AppendLine($"Windows: {Environment.OSVersion}");
        sb.AppendLine($".NET: {Environment.Version}");
        foreach (var service in services)
            sb.AppendLine($"{service.Name}: conectado={service.IsConnected}, versión={service.Snapshot.Version}, edición={service.Snapshot.Edition}, inputs={service.Snapshot.Inputs.Count}, latencia={service.Snapshot.LatencyMs}ms");
        sb.AppendLine($"ATEM: conectada={atem.Snapshot.IsConnected}, entradas={atem.Snapshot.Inputs.Count}, estado={atem.Snapshot.Status}");
        sb.AppendLine();
        sb.AppendLine("REGISTRO");
        foreach (var line in Snapshot()) sb.AppendLine(line);
        File.WriteAllText(file, sb.ToString());
        return file;
    }
}
