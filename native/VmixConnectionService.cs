using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Xml.Linq;

namespace VMixPlayerController;

public sealed class VmixConnectionService : IAsyncDisposable
{
    private readonly HttpClient http;
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly object stateLock = new();
    private long feedbackRevision;
    public long ConnectionGeneration { get; private set; }
    private readonly SemaphoreSlim commandGate = new(1, 1);
    private CancellationTokenSource? cancellation;
    private Task? pollTask;
    private List<VmixInput> demoInputs = [];
    private readonly Dictionary<int, int> demoOverlays = [];
    private readonly Dictionary<int, int> demoMixes = [];
    private readonly Dictionary<string, bool> knownAutoNext = new(StringComparer.OrdinalIgnoreCase);
    private bool demoRecording, demoStreaming, demoExternal, demoMultiCorder;

    public string Name { get; }
    public string Host { get; private set; } = "127.0.0.1";
    public int Port { get; private set; } = 8088;
    public bool IsEnabled { get; private set; } = true;
    public bool IsConnected { get; private set; }
    public bool IsDemo { get; private set; }
    public string LastError { get; private set; } = "";
    public VmixSnapshot Snapshot { get; private set; } = VmixSnapshot.Empty;
    public event Action<VmixConnectionService, VmixSnapshot>? SnapshotChanged;
    public event Action<VmixConnectionService>? ConnectionChanged;

    public VmixConnectionService(string name, HttpMessageHandler? handler = null)
    {
        Name = name;
        http = handler == null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = TimeSpan.FromSeconds(3);
    }

    public async Task StartAsync(string host, int port)
    {
        await lifecycleGate.WaitAsync();
        try
        {
            await StopCoreAsync();
            knownAutoNext.Clear();
            demoOverlays.Clear();
            demoMixes.Clear();
            foreach (var pair in new Dictionary<int, int> { [0] = 1, [1] = 2, [2] = 1, [3] = 4 }) demoMixes[pair.Key] = pair.Value;
            IsEnabled = true;
            Host = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host.Trim();
            Port = port is > 0 and <= 65535 ? port : 8088;
            IsDemo = Host.Equals("demo", StringComparison.OrdinalIgnoreCase);
            cancellation = new CancellationTokenSource();

            if (IsDemo)
            {
                demoInputs = CreateDemoInputs(Name);
                var demoTitle = demoInputs.FirstOrDefault(input => input.IsTitle);
                if (demoTitle != null) demoOverlays[1] = demoTitle.Number;
                SetSnapshot(BuildDemoSnapshot(0));
                SetConnected(true, "");
            }
            else
            {
                SetSnapshot(VmixSnapshot.Empty);
                try
                {
                    SetSnapshot(await FetchSnapshotAsync(cancellation.Token));
                    SetConnected(true, "");
                }
                catch (Exception ex)
                {
                    SetConnected(false, FriendlyError(ex));
                }
            }

            var token = cancellation.Token;
            pollTask = Task.Run(() => PollLoopAsync(token));
        }
        finally { lifecycleGate.Release(); }
    }

    public async Task DisableAsync()
    {
        await lifecycleGate.WaitAsync();
        try
        {
            await StopCoreAsync();
            IsEnabled = false;
            IsDemo = false;
            lock (stateLock) demoInputs.Clear();
            SetSnapshot(VmixSnapshot.Empty);
            SetConnected(false, "Desactivado");
        }
        finally { lifecycleGate.Release(); }
    }

    public async Task StopAsync()
    {
        await lifecycleGate.WaitAsync();
        try { await StopCoreAsync(); }
        finally { lifecycleGate.Release(); }
    }

    private async Task StopCoreAsync()
    {
        var session = cancellation;
        if (session == null) return;
        session.Cancel();
        SetConnected(false, "Desconectado");
        try { if (pollTask != null) await pollTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        // Drain the cancelled operation before replacing the endpoint or disposing HttpClient.
        await commandGate.WaitAsync().ConfigureAwait(false);
        try
        {
            cancellation = null;
            pollTask = null;
            session.Dispose();
        }
        finally { commandGate.Release(); }
    }

    public Task CommandAsync(string function, string? inputKey = null,
        IReadOnlyDictionary<string, string>? parameters = null, CancellationToken token = default) =>
        ExecuteBatchAsync([new VmixCommand(function, inputKey, parameters)], token);

    public async Task ExecuteBatchAsync(IReadOnlyList<VmixCommand> commands, CancellationToken token = default,
        VmixCommand? cleanup = null)
    {
        var session = cancellation;
        if (!IsEnabled || !IsConnected || session == null || session.IsCancellationRequested)
            throw new InvalidOperationException($"{Name} no está conectado.");
        var sessionToken = session.Token;
        var endpoint = new UriBuilder("http", Host, Port, "/api/").Uri;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, sessionToken);
        await commandGate.WaitAsync(linked.Token);
        var started = false;
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(session, cancellation) || !IsConnected)
                throw new OperationCanceledException("La conexión ha cambiado.", linked.Token);
            started = true;
            foreach (var command in commands)
            {
                linked.Token.ThrowIfCancellationRequested();
                await SendCommandCoreAsync(endpoint, command, linked.Token);
            }
        }
        finally
        {
            try
            {
                // Resume title rendering even if a field fails, but never send cleanup to a new session.
                if (started && cleanup != null && !sessionToken.IsCancellationRequested)
                    await SendCommandCoreAsync(endpoint, cleanup, sessionToken);
            }
            finally { commandGate.Release(); }
        }
    }

    internal static string BuildCommandQuery(VmixCommand command)
    {
        var values = new Dictionary<string, string> { ["Function"] = command.Function };
        if (!string.IsNullOrWhiteSpace(command.InputKey)) values["Input"] = command.InputKey;
        if (command.Parameters != null)
            foreach (var pair in command.Parameters) values[pair.Key] = pair.Value;
        return string.Join("&", values.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
    }

    private async Task SendCommandCoreAsync(Uri endpoint, VmixCommand command, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(command.Function)) return;
        try
        {
            if (IsDemo)
            {
                lock (stateLock) ApplyDemoCommand(command.Function, command.InputKey, command.Parameters);
            }
            else
            {
                using var response = await http.GetAsync(new Uri(endpoint, "?" + BuildCommandQuery(command)), token);
                response.EnsureSuccessStatusCode();
                lock (stateLock)
                {
                    feedbackRevision++;
                    ApplyImmediateCommandFeedback(command.Function, command.InputKey, command.Parameters);
                }
            }
            LogService.Write(IsDemo ? "DEMO" : "CMD", $"{Name}: {command.Function} · {command.InputKey}");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LogService.Write("ERROR", $"{Name}: {command.Function}: {ex.Message}");
            throw;
        }
    }

    private async Task PollLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (IsDemo)
                {
                    lock (stateLock)
                    {
                        AdvanceDemo(350);
                        SetSnapshot(BuildDemoSnapshot(2));
                    }
                }
                else
                {
                    long revision;
                    lock (stateLock) revision = feedbackRevision;
                    var snapshot = await FetchSnapshotAsync(token);
                    token.ThrowIfCancellationRequested();
                    lock (stateLock)
                    {
                        // A response started before a command must not undo its newer feedback.
                        if (revision == feedbackRevision) SetSnapshot(snapshot);
                    }
                    SetConnected(true, "");
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                SetConnected(false, FriendlyError(ex));
            }

            await Task.Delay(IsConnected ? 350 : 1800, token);
        }
    }

    private async Task<VmixSnapshot> FetchSnapshotAsync(CancellationToken token)
    {
        var sw = Stopwatch.StartNew();
        var xml = await http.GetStringAsync(new UriBuilder("http", Host, Port, "/api").Uri, token);
        sw.Stop();
        var parsed = ParseSnapshot(xml, sw.ElapsedMilliseconds);
        lock (stateLock) return ApplyKnownRuntimeFeedback(parsed);
    }

    public static VmixSnapshot ParseSnapshot(string xml, long latencyMs = 0)
    {
        var doc = XDocument.Parse(xml);
        var root = doc.Root ?? throw new InvalidDataException("Respuesta XML de vMix vacía.");
        if (root.Name.LocalName != "vmix") throw new InvalidDataException("La respuesta no es XML de vMix.");
        var inputs = root.Element("inputs")?.Elements("input").Select(ParseInput).ToList() ?? [];
        var active = Int(root.Element("active")?.Value);
        var preview = Int(root.Element("preview")?.Value);
        var mixes = new Dictionary<int, int> { [0] = active };
        foreach (var mix in root.Element("mixes")?.Elements("mix") ?? [])
        {
            var number = Int(mix.Attribute("number")?.Value, mixes.Count + 1);
            var apiIndex = Math.Max(0, number - 1);
            mixes[apiIndex] = Int(mix.Element("active")?.Value);
        }
        var overlays = (root.Element("overlays")?.Elements("overlay") ?? [])
            .GroupBy(overlay => Int(overlay.Attribute("number")?.Value))
            .Where(group => group.Key > 0)
            .ToDictionary(group => group.Key, group => Int(group.Last().Value));
        return new(inputs, active, preview, mixes,
            Bool(root.Element("recording")?.Value), Bool(root.Element("streaming")?.Value),
            Bool(root.Element("external")?.Value), Bool(root.Element("multiCorder")?.Value),
            root.Element("version")?.Value ?? "", root.Element("edition")?.Value ?? "",
            latencyMs, DateTime.Now, overlays);
    }

    private static VmixInput ParseInput(XElement node)
    {
        var lists = ParseListItems(node);
        var texts = node.Elements("text").Select(x => new VmixTextField(x.Attribute("name")?.Value ?? "", x.Value)).ToList();
        var type = node.Attribute("type")?.Value ?? "Unknown";
        var selectedIndex = SelectedListIndex(node, lists.Count);
        var title = type.Contains("List", StringComparison.OrdinalIgnoreCase)
            ? node.Attribute("shortTitle")?.Value ?? node.Attribute("title")?.Value ?? "Input"
            : node.Attribute("title")?.Value ?? "Input";
        return new(
            node.Attribute("key")?.Value ?? "",
            Int(node.Attribute("number")?.Value),
            title,
            type,
            node.Attribute("state")?.Value ?? "",
            Long(node.Attribute("position")?.Value),
            Long(node.Attribute("duration")?.Value),
            selectedIndex,
            lists, texts,
            Bool(node.Attribute("loop")?.Value),
            Bool(node.Attribute("muted")?.Value),
            Double(node.Attribute("volume")?.Value, 100),
            node.Attribute("audiobusses")?.Value ?? "",
            NullableBool(AttributeValue(node, "autoNext", "autoPlayNext", "autoplaynext")));
    }

    private static List<VmixListItem> ParseListItems(XElement input)
    {
        var result = new List<VmixListItem>();
        foreach (var list in input.Elements("list"))
        {
            var items = list.Elements("item").ToList();
            if (items.Count > 0)
            {
                var baseIndex = result.Count;
                result.AddRange(items.Select((item, index) => new VmixListItem(
                    item.Attribute("name")?.Value ?? $"item-{baseIndex + index + 1}",
                    item.Value.Trim())));
            }
            else if (!string.IsNullOrWhiteSpace(list.Value))
            {
                result.Add(new VmixListItem(list.Attribute("name")?.Value ?? $"item-{result.Count + 1}", list.Value.Trim()));
            }
        }
        return result;
    }

    private static int SelectedListIndex(XElement input, int itemCount)
    {
        if (itemCount <= 0) return 0;
        var selectedItem = input.Elements("list").Elements("item")
            .Select((item, index) => new { Item = item, Index = index })
            .FirstOrDefault(x => Bool(x.Item.Attribute("selected")?.Value));
        if (selectedItem != null) return Math.Clamp(selectedItem.Index, 0, itemCount - 1);
        var apiIndex = Int(input.Attribute("selectedIndex")?.Value);
        return Math.Clamp(apiIndex > 0 ? apiIndex - 1 : 0, 0, itemCount - 1);
    }

    private void SetSnapshot(VmixSnapshot snapshot)
    {
        Snapshot = snapshot;
        SnapshotChanged?.Invoke(this, snapshot);
    }

    private void SetConnected(bool connected, string error)
    {
        var changed = IsConnected != connected || LastError != error;
        if (connected && !IsConnected) ConnectionGeneration++;
        IsConnected = connected;
        LastError = error;
        if (!connected) lock (stateLock) knownAutoNext.Clear();
        if (changed)
        {
            LogService.Write(connected ? "INFO" : "WARN", $"{Name}: {(connected ? "conectado" : error)}");
            ConnectionChanged?.Invoke(this);
        }
    }

    private static string FriendlyError(Exception ex) => ex switch
    {
        HttpRequestException => "Sin respuesta de vMix",
        TaskCanceledException => "Tiempo de espera agotado",
        _ => ex.Message
    };

    private static int Int(string? value, int fallback = 0) => int.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var result) ? result : fallback;
    private static long Long(string? value) => long.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var result) ? result : 0;
    private static double Double(string? value, double fallback = 0) => double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var result) ? result : fallback;
    private static bool Bool(string? value) => bool.TryParse(value, out var result)
        ? result
        : value is "1" or "On" or "ON" or "on";
    private static bool? NullableBool(string? value) => string.IsNullOrWhiteSpace(value) ? null : Bool(value);
    private static string? AttributeValue(XElement node, params string[] names) => node.Attributes()
        .FirstOrDefault(attribute => names.Any(name => attribute.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase)))?.Value;

    private static List<VmixInput> CreateDemoInputs(string serviceName)
    {
        var prefix = serviceName.EndsWith("B", StringComparison.OrdinalIgnoreCase) ? "B" : "A";
        var clips = Enumerable.Range(1, 18).Select(i => new VmixListItem($"clip-{i:00}", $"{i:00} · Pieza {prefix} {i:00}.mp4")).ToList();
        return
        [
            new($"demo-{prefix}-1", 1, $"Rundown Noticias {prefix}", "VideoList", "Paused", 0, 118_000, 0, clips, [], AudioBuses: "MA", AutoNext: true),
            new($"demo-{prefix}-2", 2, $"Publicidad {prefix}", "VideoList", "Paused", 34_000, 62_000, 2, clips.Take(8).ToList(), [], Loop: true, Muted: true, AudioBuses: "MB", AutoNext: false),
            new($"demo-{prefix}-3", 3, $"Rótulo Invitado {prefix}", "GT", "Paused", 0, 0, 0, [],
                [new("Headline.Text", "María López"), new("Description.Text", "Alcaldesa de Manilva")]),
            new($"demo-{prefix}-4", 4, $"Cortinilla {prefix}", "Video", "Paused", 0, 12_000, 0, [], [])
        ];
    }

    private VmixSnapshot BuildDemoSnapshot(long latency) => new(demoInputs.ToList(), demoMixes.GetValueOrDefault(0), 2, new Dictionary<int, int>(demoMixes),
        demoRecording, demoStreaming, demoExternal, demoMultiCorder, "29.0 demo", "4K", latency, DateTime.Now, new Dictionary<int, int>(demoOverlays));

    private void AdvanceDemo(long milliseconds)
    {
        for (var i = 0; i < demoInputs.Count; i++)
        {
            var input = demoInputs[i];
            if (!input.IsPlaying || input.Duration <= 0) continue;
            var next = input.Position + milliseconds;
            if (next < input.Duration)
            {
                demoInputs[i] = input with { Position = next };
                continue;
            }

            if (input.IsList && input.AutoNext == true && input.SelectedIndex + 1 < input.ListItems.Count)
                demoInputs[i] = input with { Position = 0, SelectedIndex = input.SelectedIndex + 1 };
            else if (input.Loop)
                demoInputs[i] = input with { Position = 0, SelectedIndex = input.AutoNext == true ? 0 : input.SelectedIndex };
            else
                demoInputs[i] = input with { Position = input.Duration, State = "Paused" };
        }
    }

    private void ApplyDemoCommand(string function, string? inputKey, IReadOnlyDictionary<string, string>? parameters)
    {
        if (function is "StartRecording" or "StopRecording") demoRecording = function.StartsWith("Start");
        if (function is "StartStreaming" or "StopStreaming") demoStreaming = function.StartsWith("Start");
        if (function is "StartExternal" or "StopExternal") demoExternal = function.StartsWith("Start");
        if (function is "StartMultiCorder" or "StopMultiCorder") demoMultiCorder = function.StartsWith("Start");
        var overlayNumber = OverlayNumber(function);
        if (overlayNumber > 0)
        {
            if (function.EndsWith("In", StringComparison.OrdinalIgnoreCase))
            {
                var overlayInput = demoInputs.FirstOrDefault(input => input.Key == inputKey);
                if (overlayInput != null) demoOverlays[overlayNumber] = overlayInput.Number;
            }
            else if (function.EndsWith("Out", StringComparison.OrdinalIgnoreCase) || function.EndsWith("Off", StringComparison.OrdinalIgnoreCase))
                demoOverlays[overlayNumber] = 0;
            SetSnapshot(BuildDemoSnapshot(1));
            return;
        }
        var index = demoInputs.FindIndex(x => x.Key == inputKey);
        if (index < 0) { SetSnapshot(BuildDemoSnapshot(1)); return; }
        var input = demoInputs[index];
        var value = parameters != null && parameters.TryGetValue("Value", out var v) ? v : "";
        if (function == "ActiveInput" && parameters != null && parameters.TryGetValue("Mix", out var rawMix) && int.TryParse(rawMix, out var mix))
            demoMixes[mix] = input.Number;
        switch (function)
        {
            case "Play": input = input with { State = "Running" }; break;
            case "Pause": input = input with { State = "Paused" }; break;
            case "PlayPause": input = input with { State = input.IsPlaying ? "Paused" : "Running" }; break;
            case "Loop": input = input with { Loop = !input.Loop }; break;
            case "LoopOn": input = input with { Loop = true }; break;
            case "LoopOff": input = input with { Loop = false }; break;
            case "AutoPlayNext": input = input with { AutoNext = !(input.AutoNext ?? false) }; break;
            case "AutoPlayNextOn": input = input with { AutoNext = true }; break;
            case "AutoPlayNextOff": input = input with { AutoNext = false }; break;
            case "AudioBusOn": input = input.WithAudioBus(value, true); break;
            case "AudioBusOff": input = input.WithAudioBus(value, false); break;
            case "AudioOn": input = input with { Muted = false }; break;
            case "AudioOff": input = input with { Muted = true }; break;
            case "Restart": input = input with { Position = 0, State = "Running" }; break;
            case "SetPosition" when long.TryParse(value, out var position): input = input with { Position = Math.Clamp(position, 0, input.Duration) }; break;
            case "SelectIndex" when int.TryParse(value, out var selected): input = input with { SelectedIndex = Math.Clamp(selected - 1, 0, Math.Max(0, input.ListItems.Count - 1)), Position = 0 }; break;
            case "NextItem": input = input with { SelectedIndex = Math.Min(input.ListItems.Count - 1, input.SelectedIndex + 1), Position = 0 }; break;
            case "PreviousItem": input = input with { SelectedIndex = Math.Max(0, input.SelectedIndex - 1), Position = 0 }; break;
            case "SetText" when parameters != null && parameters.TryGetValue("SelectedName", out var fieldName):
                input = input with { TextFields = input.TextFields.Select(f => f.Name == fieldName ? f with { Value = value } : f).ToList() };
                break;
        }
        demoInputs[index] = input;
        SetSnapshot(BuildDemoSnapshot(1));
    }

    private VmixSnapshot ApplyKnownRuntimeFeedback(VmixSnapshot snapshot)
    {
        if (knownAutoNext.Count == 0) return snapshot;
        var inputs = snapshot.Inputs.Select(input => input.AutoNext.HasValue
            ? input
            : knownAutoNext.TryGetValue(input.Key, out var value)
                ? input with { AutoNext = value }
                : input).ToList();
        return snapshot with { Inputs = inputs };
    }

    private void ApplyImmediateCommandFeedback(string function, string? inputKey, IReadOnlyDictionary<string, string>? parameters)
    {
        var overlayNumber = OverlayNumber(function);
        if (overlayNumber > 0)
        {
            var overlays = Snapshot.OverlayInputs == null
                ? new Dictionary<int, int>()
                : new Dictionary<int, int>(Snapshot.OverlayInputs);
            if (function.EndsWith("In", StringComparison.OrdinalIgnoreCase))
            {
                var overlayInput = Snapshot.Inputs.FirstOrDefault(input => input.Key.Equals(inputKey, StringComparison.OrdinalIgnoreCase));
                if (overlayInput != null) overlays[overlayNumber] = overlayInput.Number;
            }
            else if (function.EndsWith("Out", StringComparison.OrdinalIgnoreCase) || function.EndsWith("Off", StringComparison.OrdinalIgnoreCase))
                overlays[overlayNumber] = 0;
            SetSnapshot(Snapshot with { OverlayInputs = overlays });
        }

        if (string.IsNullOrWhiteSpace(inputKey)) return;
        bool? autoNext = function switch
        {
            "AutoPlayNextOn" => true,
            "AutoPlayNextOff" => false,
            _ => null
        };
        if (autoNext.HasValue) knownAutoNext[inputKey] = autoNext.Value;
        var value = parameters != null && parameters.TryGetValue("Value", out var rawValue) ? rawValue : "";

        var changed = false;
        var inputs = Snapshot.Inputs.Select(input =>
        {
            if (!input.Key.Equals(inputKey, StringComparison.OrdinalIgnoreCase)) return input;
            var updated = function switch
            {
                "Play" => input with { State = "Running" },
                "Pause" => input with { State = "Paused" },
                "LoopOn" => input with { Loop = true },
                "LoopOff" => input with { Loop = false },
                "AutoPlayNextOn" => input with { AutoNext = true },
                "AutoPlayNextOff" => input with { AutoNext = false },
                "AudioBusOn" => input.WithAudioBus(value, true),
                "AudioBusOff" => input.WithAudioBus(value, false),
                "AudioOn" => input with { Muted = false },
                "AudioOff" => input with { Muted = true },
                "SetText" when parameters != null && parameters.TryGetValue("SelectedName", out var fieldName) =>
                    input with { TextFields = input.TextFields.Select(f => f.Name == fieldName ? f with { Value = value } : f).ToList() },
                _ => input
            };
            changed |= updated != input;
            return updated;
        }).ToList();
        if (changed) SetSnapshot(Snapshot with { Inputs = inputs });
    }

    private static int OverlayNumber(string function)
    {
        const string prefix = "OverlayInput";
        if (!function.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return 0;
        var suffix = function[prefix.Length..];
        var digits = new string(suffix.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var number) ? number : 0;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        http.Dispose();
        commandGate.Dispose();
    }
}
