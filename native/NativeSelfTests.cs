using System.Text;

namespace VMixPlayerController;

internal static class NativeSelfTests
{
    public static async Task<string> RunAsync()
    {
        try
        {
            TestVmixParser();
            TestAtemParser();
            TestClipDisplayName();
            await TestDemoCommandsAsync();
            return "PASS · feedback vMix/Overlays/audio · protocolo ATEM · nombres de clips · comandos demo · herramientas";
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
        var handshake = AtemConnectionService.BuildHandshake(0x1234);
        Assert(handshake.Length == 20 && handshake[2] == 0x12 && handshake[3] == 0x34, "ATEM handshake");
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
