namespace VMixPlayerController;

public sealed record VmixListItem(string Name, string Value);
public sealed record VmixTextField(string Name, string Value);

public sealed record VmixInput(
    string Key,
    int Number,
    string Title,
    string Type,
    string State,
    long Position,
    long Duration,
    int SelectedIndex,
    IReadOnlyList<VmixListItem> ListItems,
    IReadOnlyList<VmixTextField> TextFields,
    bool Loop = false,
    bool Muted = false,
    double Volume = 100,
    string AudioBuses = "",
    bool? AutoNext = null)
{
    private static readonly char[] AudioBusOrder = ['M', 'A', 'B', 'C', 'D', 'E', 'F', 'G'];

    public bool IsList => Type.Contains("List", StringComparison.OrdinalIgnoreCase);
    public bool IsTitle => Type.Contains("Title", StringComparison.OrdinalIgnoreCase) ||
                           Type.Contains("GT", StringComparison.OrdinalIgnoreCase);
    public bool IsPlaying => State.Equals("Running", StringComparison.OrdinalIgnoreCase) ||
                             State.Equals("Playing", StringComparison.OrdinalIgnoreCase);
    public string Kind => IsList ? "LISTA" : IsTitle ? "TÍTULO" : Type.ToUpperInvariant();

    public bool IsAudioBusEnabled(string bus)
    {
        if (string.IsNullOrWhiteSpace(bus)) return false;
        var target = char.ToUpperInvariant(bus.Trim()[0]);
        if (!AudioBusOrder.Contains(target)) return false;

        var raw = AudioBuses.Trim().ToUpperInvariant();
        if (raw.Length > 0 && raw.All(AudioBusOrder.Contains)) return raw.Contains(target);

        var tokens = raw.Split([',', ';', '|', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tokens.Any(token => token == target.ToString() ||
                                   (target == 'M' && token == "MASTER") ||
                                   token == $"BUS{target}");
    }

    public VmixInput WithAudioBus(string bus, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(bus)) return this;
        var target = char.ToUpperInvariant(bus.Trim()[0]);
        if (!AudioBusOrder.Contains(target)) return this;

        var active = AudioBusOrder.Where(candidate => IsAudioBusEnabled(candidate.ToString())).ToHashSet();
        if (enabled) active.Add(target);
        else active.Remove(target);
        return this with { AudioBuses = string.Concat(AudioBusOrder.Where(active.Contains)) };
    }
}

public sealed record VmixSnapshot(
    IReadOnlyList<VmixInput> Inputs,
    int Active,
    int Preview,
    IReadOnlyDictionary<int, int> MixActive,
    bool Recording,
    bool Streaming,
    bool External,
    bool MultiCorder,
    string Version,
    string Edition,
    long LatencyMs,
    DateTime ReceivedAt,
    IReadOnlyDictionary<int, int>? OverlayInputs = null)
{
    public static readonly VmixSnapshot Empty = new([], 0, 0, new Dictionary<int, int>(), false, false, false, false, "", "", 0, DateTime.MinValue, new Dictionary<int, int>());

    public bool IsInputOnOverlay(int overlayNumber, int inputNumber) =>
        OverlayInputs?.TryGetValue(overlayNumber, out var activeInput) == true && activeInput == inputNumber;
}

public sealed record InputChoice(VmixConnectionService Service, VmixInput Input)
{
    public override string ToString() => $"{Service.Name}  ·  {Input.Number:00}  {Input.Title}";
}

public sealed record AtemInput(int Id, string LongName, string ShortName)
{
    public override string ToString() => Id <= 0 ? LongName : $"{Id:00}  {LongName}";
}

public sealed record AtemSnapshot(
    IReadOnlyList<AtemInput> Inputs,
    IReadOnlyDictionary<int, int> ProgramByMe,
    bool IsConnected,
    string Status,
    DateTime ReceivedAt)
{
    public static readonly AtemSnapshot Empty = new([], new Dictionary<int, int>(), false, "DESCONECTADA", DateTime.MinValue);

    public bool IsOnProgram(int inputId, int me)
    {
        if (me < 0) return ProgramByMe.Values.Contains(inputId);
        return ProgramByMe.TryGetValue(me, out var program) && program == inputId;
    }
}

public sealed class EndpointConfig
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 8088;
}

public sealed class PlayerConfig
{
    public string VmixName { get; set; } = "";
    public string InputKey { get; set; } = "";
    public List<int> SelectedMixes { get; set; } = [];
    public int AtemInputId { get; set; }
    public int AtemMe { get; set; }
    public bool AtemAutoPlayPause { get; set; }
    public bool GoRestart { get; set; } = true;
    public bool GoAudioAuto { get; set; }
}

public sealed class AppSettings
{
    public EndpointConfig VmixA { get; set; } = new() { Host = "127.0.0.1", Port = 8088 };
    public EndpointConfig VmixB { get; set; } = new() { Host = "127.0.0.1", Port = 8088 };
    public bool UseVmixB { get; set; }
    public string AtemHost { get; set; } = "192.168.10.240";
    public bool AutoConnect { get; set; }
    public List<PlayerConfig> Players { get; set; } = [new(), new(), new(), new()];
}
