using System.Text;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.IO;
using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace VMixPlayerController;

internal static class NativeSelfTests
{
    public static async Task<string> RunAsync()
    {
        try
        {
            TestVmixParser();
            TestAtemParser();
            await TestAtemTransportAsync();
            TestClipDisplayName();
            TestSettings();
            TestQueryEncoding();
            await TestDemoCommandsAsync();
            await TestAutomationAsync();
            await TestCommandBatchesAsync();
            await TestEndpointChangeAsync();
            return "PASS · XML/ATEM, UDP y secuencias · perfiles/backup · parámetros vacíos · demo · AUTO fallo/reintento/reconexión/cancelación · lotes sin intercalado · ResumeRender tras fallo · órdenes canceladas al cambiar conexión";
        }
        catch (Exception ex) { return $"FAIL · {ex}"; }
    }

    private static void TestVmixParser()
    {
        const string xml = """
            <vmix><version>29.0</version><edition>4K</edition><inputs>
              <input key="abc" number="3" type="VideoList" title="Noticias - dos.mp4" shortTitle="Noticias" state="Running" position="1500" duration="9000" selectedIndex="2" loop="True" muted="False" volume="82.5" audiobusses="M,A,C" autoPlayNext="True">
                <list>
                  <item>C:\Media\uno.mp4</item>
                  <item selected="true">C:\Media\dos.mp4</item>
                  <item>C:\Media\tres.mp4</item>
                </list>
              </input>
              <input key="legacy" number="4" type="VideoList" title="Archivo" state="Paused" position="0" duration="5000" selectedIndex="1">
                <list name="a">antiguo-uno.mp4</list><list name="b">antiguo-dos.mp4</list>
              </input>
            </inputs><overlays><overlay number="1">3</overlay><overlay number="2"/><overlay number="3"/><overlay number="4"/></overlays><active>3</active><preview>2</preview><recording>True</recording><streaming>False</streaming><external>False</external><multiCorder>False</multiCorder></vmix>
            """;
        var snapshot = VmixConnectionService.ParseSnapshot(xml, 12);
        Assert(snapshot.Inputs.Count == 2, "vMix inputs");
        Assert(snapshot.Inputs[0].IsList && snapshot.Inputs[0].ListItems.Count == 3, "vMix list container");
        Assert(snapshot.Inputs[0].SelectedIndex == 1 && snapshot.Inputs[0].ListItems[1].Value.EndsWith("dos.mp4"), "vMix selected list item");
        Assert(snapshot.Inputs[0].Title == "Noticias", "vMix list short title");
        Assert(snapshot.Inputs[0].Loop && snapshot.Inputs[0].AutoNext == true && Math.Abs(snapshot.Inputs[0].Volume - 82.5) < 0.01, "vMix input feedback");
        Assert(snapshot.Inputs[0].IsAudioBusEnabled("M") && snapshot.Inputs[0].IsAudioBusEnabled("A") && snapshot.Inputs[0].IsAudioBusEnabled("C") &&
               !snapshot.Inputs[0].IsAudioBusEnabled("B") && !snapshot.Inputs[0].IsAudioBusEnabled("D"), "vMix audio bus feedback");
        Assert(snapshot.Inputs[1].ListItems.Count == 2 && snapshot.Inputs[1].SelectedIndex == 0, "vMix legacy list");
        Assert(snapshot.Recording && snapshot.Active == 3 && snapshot.LatencyMs == 12, "vMix status");
        Assert(snapshot.IsInputOnOverlay(1, 3) && !snapshot.IsInputOnOverlay(2, 3), "vMix overlay feedback");
    }

    private static void TestAtemParser()
    {
        var inputBody = new byte[36];
        Write16(inputBody, 0, 7);
        Encoding.ASCII.GetBytes("VTR 1").CopyTo(inputBody, 2);
        Encoding.ASCII.GetBytes("VTR1").CopyTo(inputBody, 22);
        inputBody[32] = 0;
        var programBody = new byte[4];
        programBody[0] = 1;
        Write16(programBody, 2, 7);
        var block = Command("InPr", inputBody).Concat(Command("PrgI", programBody)).Concat(Command("InCm", [])).ToArray();
        var inputs = new Dictionary<int, AtemInput>();
        var program = new Dictionary<int, int>();
        var initialized = false;
        var changed = AtemConnectionService.ParseCommandBlock(block, 0, block.Length, inputs, program, ref initialized);
        Assert(changed && initialized, "ATEM init");
        Assert(inputs.TryGetValue(7, out var input) && input.LongName == "VTR 1", "ATEM input name");
        Assert(program.TryGetValue(1, out var source) && source == 7, "ATEM M/E program");
        Assert(AtemConnectionService.ClassifyPacket(32767, 0) == 1, "ATEM wrap de secuencia");
        Assert(AtemConnectionService.ClassifyPacket(4, 4) == 0 && AtemConnectionService.ClassifyPacket(4, 3) == 0,
            "ATEM duplicados y paquetes antiguos");
        Assert(AtemConnectionService.ClassifyPacket(4, 6) > 1, "ATEM detecta pérdida");
        var malformed = Command("PrgI", programBody);
        Write16(malformed, 0, malformed.Length + 1);
        try
        {
            AtemConnectionService.ParseCommandBlock(malformed, 0, malformed.Length, inputs, program, ref initialized);
            throw new Exception("ATEM aceptó un comando truncado");
        }
        catch (InvalidDataException) { }
        var handshake = AtemConnectionService.BuildHandshake(0x1234);
        Assert(handshake.Length == 20 && handshake[2] == 0x12 && handshake[3] == 0x34, "ATEM handshake");
    }

    private static async Task TestAtemTransportAsync()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var rogue = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await using var atem = new AtemConnectionService(((IPEndPoint)server.Client.LocalEndPoint!).Port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var seen = new ConcurrentQueue<int>();
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var updated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        atem.SnapshotChanged += snapshot =>
        {
            if (snapshot.IsConnected && snapshot.ProgramByMe.TryGetValue(0, out var source))
            {
                seen.Enqueue(source);
                connected.TrySetResult();
                if (source == 8) updated.TrySetResult();
            }
            if (snapshot.Status == "REINTENTANDO") disconnected.TrySetResult();
        };
        await atem.StartAsync("127.0.0.1");
        var hello = await server.ReceiveAsync(timeout.Token);
        var peer = hello.RemoteEndPoint;
        byte[] Packet(int flags, int session, int id, byte[] body)
        {
            var packet = new byte[12 + body.Length];
            Write16(packet, 0, (flags << 11) | packet.Length);
            Write16(packet, 2, session);
            Write16(packet, 10, id);
            body.CopyTo(packet, 12);
            return packet;
        }
        byte[] Program(int id) => Command("PrgI", new byte[] { 0, 0, 0, (byte)id });
        async Task Send(UdpClient sender, byte[] packet) => await sender.SendAsync(packet, peer, timeout.Token);
        await Send(server, Packet(2, 0x1234, 0, []));
        await server.ReceiveAsync(timeout.Token); // Handshake ACK before the initial state.
        await Send(server, Packet(1, 0x8123, 1, Program(7).Concat(Command("InCm", [])).ToArray()));
        await connected.Task.WaitAsync(timeout.Token);
        await Send(rogue, Packet(1, 0x8123, 2, Program(77)));
        await Send(server, Packet(1, 0x8123, 1, Program(9))); // Duplicate must not restore old PGM.
        await Send(server, Packet(1, 0x9999, 2, Program(66))); // Another session must not alter PGM.
        await Send(server, Packet(1, 0x8123, 2, Program(8)));
        await updated.Task.WaitAsync(timeout.Token);
        Assert(seen.All(source => source is 7 or 8), "ATEM ignora emisor, sesión y secuencia ajenos");
        await Send(server, Packet(1, 0x8123, 4, Program(10))); // Packet 3 was lost.
        await disconnected.Task.WaitAsync(timeout.Token);
        Assert(!atem.Snapshot.IsConnected && !seen.Contains(10), "ATEM resynchroniza sin publicar PGM incompleto");
        await atem.StopAsync();
    }

    private static void TestClipDisplayName()
    {
        Assert(PlayerControl.DisplayClipName(@"C:\Media\Noticias\pieza larga.mp4") == "pieza larga.mp4", "Clip Windows sin ruta");
        Assert(PlayerControl.DisplayClipName("file:///C:/Media/Cabecera/apertura.mp4") == "apertura.mp4", "Clip URI sin ruta");
    }

    private static async Task TestDemoCommandsAsync()
    {
        await using var service = new VmixConnectionService("vMix A");
        await service.StartAsync("demo", 8088);
        var input = service.Snapshot.Inputs.First(i => i.IsList);
        await service.CommandAsync("Play", input.Key);
        Assert(service.Snapshot.Inputs.First(i => i.Key == input.Key).IsPlaying, "Demo Play");
        await service.CommandAsync("NextItem", input.Key);
        Assert(service.Snapshot.Inputs.First(i => i.Key == input.Key).SelectedIndex == 1, "Demo Next");
        await service.CommandAsync("Pause", input.Key);
        Assert(!service.Snapshot.Inputs.First(i => i.Key == input.Key).IsPlaying, "Demo Pause");
        await service.CommandAsync("LoopOn", input.Key);
        Assert(service.Snapshot.Inputs.First(i => i.Key == input.Key).Loop, "Demo Loop feedback");
        await service.CommandAsync("AutoPlayNextOff", input.Key);
        Assert(service.Snapshot.Inputs.First(i => i.Key == input.Key).AutoNext == false, "Demo Auto Next feedback");
        await service.CommandAsync("AudioBusOn", input.Key, new Dictionary<string, string> { ["Value"] = "D" });
        Assert(service.Snapshot.Inputs.First(i => i.Key == input.Key).IsAudioBusEnabled("D"), "Demo Audio Bus On");
        await service.CommandAsync("AudioBusOff", input.Key, new Dictionary<string, string> { ["Value"] = "M" });
        Assert(!service.Snapshot.Inputs.First(i => i.Key == input.Key).IsAudioBusEnabled("M"), "Demo Audio Bus Off");
        var title = service.Snapshot.Inputs.First(i => i.IsTitle);
        await service.CommandAsync("OverlayInput2In", title.Key);
        Assert(service.Snapshot.IsInputOnOverlay(2, title.Number), "Demo Overlay In feedback");
        await service.CommandAsync("OverlayInput2Out");
        Assert(!service.Snapshot.IsInputOnOverlay(2, title.Number), "Demo Overlay Out feedback");
        Assert(ToolsWindow.FindService([service], "A") == service && ToolsWindow.FindService([service], "B") == null, "Herramientas con un solo vMix");
        await service.DisableAsync();
        Assert(!service.IsEnabled && service.Snapshot.Inputs.Count == 0, "vMix opcional desactivado");
    }

    private static void TestQueryEncoding()
    {
        var query = VmixConnectionService.BuildCommandQuery(new("SetText", "a&b",
            new Dictionary<string, string> { ["SelectedName"] = "Título.Text", ["Value"] = "" }));
        Assert(query.Contains("Value=") && query.Contains("Input=a%26b") && query.Contains("T%C3%ADtulo.Text"),
            "Permite vaciar campos y escapa parámetros");
        try { VmixConnectionService.ParseSnapshot("<html/>"); throw new Exception("Aceptó XML ajeno a vMix"); }
        catch (InvalidDataException) { }
    }

    private static void TestSettings()
    {
        var folder = Path.Combine(Path.GetTempPath(), "vmix-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "profile.json");
            var service = new SettingsService(path);
            File.WriteAllText(path, """{"Players":[null,{"SelectedMixes":[0,0,4,-1],"AtemMe":9}],"VmixA":null}""");
            var imported = service.Import(path);
            Assert(imported.Players.Count == 4 && imported.VmixA != null, "Normaliza perfiles parciales");
            Assert(imported.Players[1].SelectedMixes.SequenceEqual(new[] { 0 }) && imported.Players[1].AtemMe == 0,
                "Normaliza destinos y M/E");
            imported.VmixA!.Host = "first-host";
            Assert(service.Save(imported), "Guarda perfil atómicamente");
            imported.VmixA.Host = "second-host";
            Assert(service.Save(imported), "Actualiza perfil con backup");
            File.WriteAllText(path, "JSON roto");
            Assert(service.Load().VmixA.Host == "first-host", "Recupera backup válido");
            File.WriteAllText(path, """{"VmixA":{"Port":65536}}""");
            try { service.Import(path); throw new Exception("Aceptó puerto inválido"); }
            catch (InvalidDataException) { }
            var blocked = new SettingsService(folder); // A directory cannot be replaced by a settings file.
            Assert(!blocked.Save(new AppSettings()) && blocked.LastError.Length > 0, "Comunica fallo de guardado");
        }
        finally { Directory.Delete(folder, true); }
    }

    private static async Task TestAutomationAsync()
    {
        using var auto = new AtemPlaybackAutomation();
        var sent = new List<bool>();
        var fail = true;
        var now = DateTime.UtcNow;
        Task Send(bool play, CancellationToken _)
        {
            sent.Add(play);
            if (fail) throw new HttpRequestException("fallo simulado");
            return Task.CompletedTask;
        }
        await auto.ReconcileAsync("input:1", false, Send, now);
        Assert(sent.Count == 0, "AUTO no pausa inicialmente fuera de PGM");
        await auto.ReconcileAsync("input:1", true, Send, now);
        Assert(auto.IsPending && auto.LastError.Length > 0, "AUTO conserva Play fallido");
        fail = false;
        await auto.ReconcileAsync("input:1", true, Send, now.AddSeconds(1));
        Assert(sent.Count == 1, "AUTO limita reintentos");
        await auto.ReconcileAsync("input:1", true, Send, now.AddSeconds(3));
        await auto.ReconcileAsync("input:1", true, Send, now.AddSeconds(4));
        Assert(sent.Count == 2 && !auto.IsPending, "AUTO reintenta y no repite éxito");
        fail = true;
        await auto.ReconcileAsync("input:1", false, Send, now.AddSeconds(5));
        await auto.ReconcileAsync("input:1", null, Send, now.AddSeconds(6));
        fail = false;
        await auto.ReconcileAsync("input:2", false, Send, now.AddSeconds(7));
        Assert(sent.SequenceEqual(new[] { true, true, false, false }) && !auto.IsPending,
            "AUTO recupera Pause pendiente tras reconexión");
        await auto.ReconcileAsync("input:2", true, Send, now.AddSeconds(8));
        await auto.ReconcileAsync("input:2", null, Send, now.AddSeconds(9));
        await auto.ReconcileAsync("input:3", true, Send, now.AddSeconds(10));
        Assert(sent.Count == 6, "AUTO reafirma Play tras reconexión");
        auto.Reset();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = false;
        var pending = auto.ReconcileAsync("old", true, async (_, token) =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { cancelled = true; throw; }
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        auto.Reset();
        await pending;
        Assert(cancelled && !auto.IsPending, "Cambio de asignación cancela AUTO anterior");
    }

    private sealed class FakeVmixHandler : HttpMessageHandler
    {
        public readonly ConcurrentQueue<string> Commands = new();
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? OnCommand;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (string.IsNullOrEmpty(request.RequestUri!.Query))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""<vmix><inputs><input key="a" number="1" type="GT" title="Título" state="Paused"><text name="Text">original</text></input></inputs></vmix>""")
                });
            Commands.Enqueue(request.RequestUri.Host + request.RequestUri.Query);
            return OnCommand?.Invoke(request, token) ?? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private static async Task TestCommandBatchesAsync()
    {
        var handler = new FakeVmixHandler();
        await using var service = new VmixConnectionService("Prueba", handler);
        await service.StartAsync("first-host", 8088);
        handler.OnCommand = (request, _) => Task.FromResult(new HttpResponseMessage(
            request.RequestUri!.Query.Contains("Function=SetText") ? HttpStatusCode.InternalServerError : HttpStatusCode.OK));
        try
        {
            await service.ExecuteBatchAsync([new("PauseRender", "a"), new("SetText", "a")], cleanup: new("ResumeRender", "a"));
            throw new Exception("No propagó el fallo de SetText");
        }
        catch (HttpRequestException) { }
        Assert(handler.Commands.Last().Contains("Function=ResumeRender"), "ResumeRender tras fallo de SetText");
        handler.Commands.Clear();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.OnCommand = async (request, token) =>
        {
            if (request.RequestUri!.Query.Contains("Function=Restart"))
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        };
        var batch = service.ExecuteBatchAsync([new("Restart", "a"), new("Play", "a")]);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var other = service.CommandAsync("Pause", "a");
        release.SetResult();
        await Task.WhenAll(batch, other);
        Assert(handler.Commands.Select(command => command.Split("Function=")[1].Split('&')[0])
            .SequenceEqual(new[] { "Restart", "Play", "Pause" }), "No intercala órdenes dentro de GO");
    }

    private static async Task TestEndpointChangeAsync()
    {
        var handler = new FakeVmixHandler();
        await using var service = new VmixConnectionService("Prueba", handler);
        await service.StartAsync("old-host", 8088);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.OnCommand = async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        };
        var inflight = service.CommandAsync("Play", "a");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var queued = service.CommandAsync("Restart", "a");
        await service.StartAsync("new-host", 8088);
        foreach (var task in new[] { inflight, queued })
        {
            try { await task; throw new Exception("No canceló orden de sesión antigua"); }
            catch (OperationCanceledException) { }
        }
        Assert(service.IsConnected && service.Host == "new-host" && handler.Commands.Count == 1,
            "No envía órdenes antiguas al nuevo equipo");
        handler.OnCommand = null;
        await service.CommandAsync("Pause", "a");
        Assert(handler.Commands.Last().StartsWith("new-host"), "Nueva sesión operativa");
    }

    internal static async Task RunUiAsync()
    {
        Assert(MainWindow.IsEditingText(new TextBox()) && MainWindow.IsEditingText(new PasswordBox()) &&
            MainWindow.IsEditingText(new ComboBox { IsEditable = true }) && !MainWindow.IsEditingText(new Button()),
            "Atajos respetan campos de texto");
        await using var service = new VmixConnectionService("vMix A");
        await using var atem = new AtemConnectionService();
        await service.StartAsync("demo", 8088);
        var oldProfile = new PlayerConfig { VmixName = "vMix A", InputKey = "demo-A-1" };
        var newProfile = new PlayerConfig { VmixName = "vMix A", InputKey = "demo-A-2" };
        var player = new PlayerControl();
        var window = new Window { Content = player, Width = 500, Height = 850, ShowInTaskbar = false };
        try
        {
            player.Configure(1, oldProfile, [service], atem, () => { });
            window.Show();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            player.Configure(1, newProfile, [service], atem, () => { });
            Assert((player.InputSelector.SelectedItem as InputChoice)?.Input.Key == "demo-A-2", "Perfil nuevo visible");
            player.InputSelector.SelectedItem = player.InputSelector.Items.Cast<InputChoice>().First(c => c.Input.Key == "demo-A-3");
            Assert(newProfile.InputKey == "demo-A-3" && oldProfile.InputKey == "demo-A-1", "Edición vinculada al perfil nuevo");
            player.InputSelector.SelectedItem = player.InputSelector.Items.Cast<InputChoice>().First(c => c.Input.Key == "demo-A-1");
            player.ClipList.SelectedIndex = 5;
            await service.CommandAsync("LoopOn", "demo-A-1");
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Assert(player.ClipList.SelectedIndex == 5, "Polling conserva selección del operador");
            await service.CommandAsync("NextItem", "demo-A-1");
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Assert(player.ClipList.SelectedIndex == 1, "Sigue cambio real de clip");
        }
        finally { window.Close(); }
    }

    private static byte[] Command(string name, byte[] body)
    {
        var result = new byte[8 + body.Length];
        Write16(result, 0, result.Length);
        Encoding.ASCII.GetBytes(name).CopyTo(result, 4);
        body.CopyTo(result, 8);
        return result;
    }

    private static void Write16(byte[] data, int offset, int value)
    {
        data[offset] = (byte)(value >> 8);
        data[offset + 1] = (byte)value;
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException($"Prueba fallida: {name}");
    }
}
