using System.Net;
using System.IO;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

namespace VMixPlayerController;

/// <summary>
/// Read-only ATEM Ethernet client. It performs the native UDP handshake and reads
/// input names (InPr) and Program sources (PrgI). No ATEM state is modified.
/// </summary>
public sealed class AtemConnectionService : IAsyncDisposable
{
    private readonly int atemPort;

    public AtemConnectionService() : this(9910) { }
    internal AtemConnectionService(int port) => atemPort = port;
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private CancellationTokenSource? cancellation;
    private Task? worker;
    private readonly Dictionary<int, AtemInput> inputs = [];
    private readonly Dictionary<int, int> programByMe = [];
    private ushort sessionId;

    public string Host { get; private set; } = "";
    public AtemSnapshot Snapshot { get; private set; } = AtemSnapshot.Empty;
    public event Action<AtemSnapshot>? SnapshotChanged;

    public async Task StartAsync(string host)
    {
        await lifecycleGate.WaitAsync();
        try
        {
            await StopCoreAsync();
            Host = host.Trim();
            if (!IPAddress.TryParse(Host, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
            {
                Publish(false, "IP NO VÁLIDA");
                return;
            }
            cancellation = new CancellationTokenSource();
            var token = cancellation.Token;
            worker = Task.Run(() => ConnectionLoopAsync(token));
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
        if (cancellation == null) return;
        cancellation.Cancel();
        try { if (worker != null) await worker.ConfigureAwait(false); } catch (OperationCanceledException) { }
        cancellation.Dispose();
        cancellation = null;
        worker = null;
        Publish(false, "DESCONECTADA");
    }

    private async Task ConnectionLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await RunConnectionAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                LogService.Write("WARN", $"ATEM: {ex.Message}");
                Publish(false, "REINTENTANDO");
                await Task.Delay(1800, token);
            }
        }
    }

    private async Task RunConnectionAsync(CancellationToken token)
    {
        inputs.Clear();
        programByMe.Clear();
        sessionId = (ushort)Random.Shared.Next(1, 32767);
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        udp.Client.ReceiveBufferSize = 1024 * 128;
        var endpoint = new IPEndPoint(IPAddress.Parse(Host), atemPort);
        var handshake = BuildHandshake(sessionId);
        await udp.SendAsync(handshake, handshake.Length, endpoint);
        Publish(false, "CONECTANDO");

        var initialized = false;
        var established = false;
        var dataSessionEstablished = false;
        ushort lastPacketId = 0;
        var lastValidPacket = Stopwatch.GetTimestamp();
        while (!token.IsCancellationRequested)
        {
            using var receiveTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            var remaining = TimeSpan.FromSeconds(initialized ? 5 : 3) - Stopwatch.GetElapsedTime(lastValidPacket);
            if (remaining <= TimeSpan.Zero) throw new TimeoutException("La ATEM no responde con un estado válido");
            receiveTimeout.CancelAfter(remaining);
            UdpReceiveResult result;
            try { result = await udp.ReceiveAsync(receiveTimeout.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException("La ATEM no responde"); }

            if (!result.RemoteEndPoint.Equals(endpoint)) continue;
            var packet = result.Buffer;
            if (packet.Length < 12) continue;
            var flags = packet[0] >> 3;
            var packetLength = ((packet[0] & 0x07) << 8) | packet[1];
            if (packetLength != packet.Length || packetLength < 12) continue;
            var packetSession = ReadUInt16(packet, 2);

            if ((flags & 0x02) != 0)
            {
                if (established)
                {
                    if (!dataSessionEstablished && packetSession == sessionId)
                    {
                        await SendAckAsync(udp, endpoint, lastPacketId);
                        continue;
                    }
                    throw new IOException("La ATEM ha reiniciado la sesión");
                }
                sessionId = packetSession;
                lastPacketId = ReadUInt16(packet, 10);
                established = true;
                lastValidPacket = Stopwatch.GetTimestamp();
                await SendAckAsync(udp, endpoint, lastPacketId);
                continue;
            }

            if (!established) continue;
            var changed = false;
            if ((flags & 0x01) != 0)
            {
                var packetId = ReadUInt16(packet, 10);
                // Some devices assign the final session identifier in their first data packet.
                if (!dataSessionEstablished) { sessionId = packetSession; dataSessionEstablished = true; }
                if (packetSession != sessionId) continue;
                var order = ClassifyPacket(lastPacketId, packetId);
                if (order > 1) throw new IOException("Falta un paquete ATEM; resincronizando el estado");
                if (order == 0)
                {
                    await SendAckAsync(udp, endpoint, lastPacketId);
                    continue;
                }
                lastPacketId = packetId;
                lastValidPacket = Stopwatch.GetTimestamp();
                changed = ParseCommandBlock(packet, 12, packetLength - 12, inputs, programByMe, ref initialized);
                await SendAckAsync(udp, endpoint, lastPacketId);
            }

            if (changed || initialized && !Snapshot.IsConnected)
                Publish(initialized, initialized ? $"CONECTADA · {inputs.Count} ENTRADAS" : "RECIBIENDO ESTADO");
        }
    }

    // ATEM sequence numbers wrap at 15 bits. Never replay an older PGM update.
    internal static int ClassifyPacket(ushort previous, ushort current)
    {
        if (current >= 32768) throw new InvalidDataException("Número de paquete ATEM no válido");
        var distance = (current - previous + 32768) % 32768;
        return distance == 0 || distance >= 16384 ? 0 : distance;
    }

    internal static bool ParseCommandBlock(byte[] packet, int offset, int length, IDictionary<int, AtemInput> targetInputs, IDictionary<int, int> targetProgramByMe, ref bool initialized)
    {
        if (offset < 0 || length < 0 || offset > packet.Length - length)
            throw new InvalidDataException("Bloque ATEM fuera de límites");
        var changed = false;
        var end = offset + length;
        while (offset + 8 <= end)
        {
            var commandLength = ReadUInt16(packet, offset);
            if (commandLength < 8 || offset + commandLength > end)
                throw new InvalidDataException("Comando ATEM incompleto");
            var name = Encoding.ASCII.GetString(packet, offset + 4, 4);
            var bodyOffset = offset + 8;
            var bodyLength = commandLength - 8;

            switch (name)
            {
                case "InPr" when bodyLength >= 36:
                {
                    var id = ReadUInt16(packet, bodyOffset);
                    var longName = ReadString(packet, bodyOffset + 2, 20);
                    var shortName = ReadString(packet, bodyOffset + 22, 4);
                    var internalPortType = packet[bodyOffset + 32];
                    if (internalPortType == 0 && id > 0)
                    {
                        targetInputs[id] = new AtemInput(id, string.IsNullOrWhiteSpace(longName) ? $"Entrada {id}" : longName, shortName);
                        changed = true;
                    }
                    break;
                }
                case "PrgI" when bodyLength >= 4:
                {
                    var me = packet[bodyOffset];
                    var source = ReadUInt16(packet, bodyOffset + 2);
                    if (!targetProgramByMe.TryGetValue(me, out var current) || current != source)
                    {
                        targetProgramByMe[me] = source;
                        changed = true;
                        LogService.Write("ATEM", $"M/E {me + 1} PGM → {source}");
                    }
                    break;
                }
                case "InCm":
                    initialized = true;
                    changed = true;
                    LogService.Write("INFO", "ATEM: estado inicial recibido");
                    break;
            }
            offset += commandLength;
        }
        if (offset != end) throw new InvalidDataException("Cabecera ATEM incompleta");
        return changed;
    }

    private async Task SendAckAsync(UdpClient udp, IPEndPoint endpoint, ushort packetId)
    {
        var ack = new byte[12];
        ack[0] = 0x80;
        ack[1] = 0x0c;
        WriteUInt16(ack, 2, sessionId);
        WriteUInt16(ack, 4, packetId);
        await udp.SendAsync(ack, ack.Length, endpoint);
    }

    private void Publish(bool connected, string status)
    {
        Snapshot = new(inputs.Values.OrderBy(i => i.Id).ToList(), new Dictionary<int, int>(programByMe), connected, status, DateTime.Now);
        SnapshotChanged?.Invoke(Snapshot);
    }

    internal static byte[] BuildHandshake(ushort id)
    {
        var packet = new byte[] { 0x10, 0x14, 0, 0, 0, 0, 0, 0, 0, 0x68, 0, 0, 1, 0, 0, 8, 0, 0, 0, 0 };
        WriteUInt16(packet, 2, id);
        return packet;
    }

    private static ushort ReadUInt16(byte[] data, int offset) => (ushort)((data[offset] << 8) | data[offset + 1]);
    private static void WriteUInt16(byte[] data, int offset, ushort value)
    {
        data[offset] = (byte)(value >> 8);
        data[offset + 1] = (byte)value;
    }
    private static string ReadString(byte[] data, int offset, int length)
    {
        var text = Encoding.ASCII.GetString(data, offset, length);
        var zero = text.IndexOf('\0');
        return (zero >= 0 ? text[..zero] : text).Trim();
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
