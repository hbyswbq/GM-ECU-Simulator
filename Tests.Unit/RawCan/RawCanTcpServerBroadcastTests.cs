using Common.Protocol;
using Core.Bus;
using Core.Ecu;
using Core.Protocol;
using EcuSimulator.Tests.TestHelpers;
using Shim.Ipc;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace EcuSimulator.Tests.RawCan;

// Regression coverage for the raw-CAN UUDT broadcast path. The diag tests
// (RawCanTcpServerDiagTests) prove request/response and the GMW3110 $AA periodic
// stream reach a gauge over the socket; those flow through the channel's own Rx
// queue. The Ford DMR rapid-packet loop instead pushes frames through
// VirtualBus.Broadcaster, which the raw-CAN transport did NOT wire (only the
// J2534/IpcSessionState path did). A gauge therefore got the $A1/$A0 handshake
// but never the 0x6A0 stream. These tests pin that RawCanTcpServer now binds a
// broadcaster for the life of the connection and routes broadcast frames to the
// gauge, and unbinds it on disconnect.
[Collection(FordUdsPersonaCollection.Name)]
public sealed class RawCanTcpServerBroadcastTests
{
    private const ushort PhysReq = NodeFactory.PhysReq;       // $7E0
    private const ushort UsdtResp = NodeFactory.UsdtResp;     // $7E8
    private const ushort EngineDmrCanId = 0x6A0;

    // Minimal gauge-side socket helper: send a USDT single frame, read raw frames.
    private sealed class Gauge : IAsyncDisposable
    {
        private readonly RawCanTcpServer server;
        private readonly TcpClient client;
        private readonly NetworkStream stream;

        private Gauge(RawCanTcpServer server, TcpClient client)
        {
            this.server = server;
            this.client = client;
            stream = client.GetStream();
            stream.ReadTimeout = 3000;
            stream.WriteTimeout = 3000;
        }

        public static async Task<Gauge> ConnectAsync(RawCanTcpServer server)
        {
            var c = new TcpClient();
            await c.ConnectAsync(IPAddress.Loopback, server.Port);
            return new Gauge(server, c);
        }

        public void SendUsdtSingleFrame(uint canId, ReadOnlySpan<byte> payload)
        {
            Assert.True(payload.Length <= 7);
            var sf = new byte[1 + payload.Length];
            sf[0] = (byte)(payload.Length & 0x0F);
            payload.CopyTo(sf.AsSpan(1));
            SendCanFrame(canId, sf);
        }

        public void SendCanFrame(uint canId, ReadOnlySpan<byte> data)
        {
            var internalFrame = new byte[4 + data.Length];
            internalFrame[0] = (byte)((canId >> 24) & 0xFF);
            internalFrame[1] = (byte)((canId >> 16) & 0xFF);
            internalFrame[2] = (byte)((canId >> 8) & 0xFF);
            internalFrame[3] = (byte)(canId & 0xFF);
            data.CopyTo(internalFrame.AsSpan(4));

            var wire = new byte[RawCanWire.FrameSize];
            RawCanWire.FromInternal(internalFrame, wire);
            stream.Write(wire, 0, wire.Length);
            stream.Flush();
        }

        public (uint canId, byte[] data) ReadCanFrame()
        {
            var wire = new byte[RawCanWire.FrameSize];
            stream.ReadExactly(wire, 0, wire.Length);     // throws on timeout/close
            int dlc = wire[0] & 0x0F;
            uint canId = ((uint)wire[1] << 24) | ((uint)wire[2] << 16) | ((uint)wire[3] << 8) | wire[4];
            return (canId, wire.AsSpan(5, dlc).ToArray());
        }

        // Reads frames until one arrives on the given CAN ID (drains other
        // traffic - the 0x97 heartbeat, the $A1/$A0 acks, etc.).
        public byte[] ReadUntilCanId(uint wantId, int maxFrames = 40)
        {
            for (int i = 0; i < maxFrames; i++)
            {
                var (canId, data) = ReadCanFrame();
                if (canId == wantId) return data;
            }
            throw new Xunit.Sdk.XunitException($"no frame on 0x{wantId:X} within {maxFrames} frames");
        }

        public async ValueTask DisposeAsync()
        {
            client.Close();
            await server.StopAsync();
        }
    }

    private static (VirtualBus bus, RawCanTcpServer server) StartWith(EcuNode node)
    {
        var bus = new VirtualBus();
        bus.AddNode(node);
        var server = new RawCanTcpServer(bus, port: 0, log: _ => { });
        server.Start();
        return (bus, server);
    }

    [Fact]
    public async Task Broadcaster_IsBound_RoutesFrameToGauge_AndUnbindsOnDisconnect()
    {
        // Deterministic: drive bus.Broadcaster directly (no timer) so the test
        // pins exactly the wiring the fix adds, independent of the DMR loop.
        var (bus, server) = StartWith(NodeFactory.CreateNode());
        var gauge = await Gauge.ConnectAsync(server);

        Assert.NotNull(bus.Broadcaster);   // bound for the life of the connection

        // A 0x6A0-shaped internal frame: 4-byte CAN ID + 8 data bytes.
        var frame = new byte[]
        {
            0x00, 0x00, 0x06, 0xA0,                         // CAN ID 0x6A0
            0x07, 0xE0, 0x08, 0xAA, 0xBB, 0xCC, 0xDD, 0x00, // [prefix][marker][slot][value..]
        };
        bus.Broadcaster!.BroadcastFrame(frame);

        var (canId, data) = gauge.ReadCanFrame();
        Assert.Equal(EngineDmrCanId, canId);
        Assert.Equal(new byte[] { 0x07, 0xE0, 0x08, 0xAA, 0xBB, 0xCC, 0xDD, 0x00 }, data);

        await gauge.DisposeAsync();        // closes socket + stops server (awaits handler teardown)
        Assert.Null(bus.Broadcaster);      // unbound on disconnect
    }

    [Fact]
    public async Task FordDmr_SetupOverSocket_StreamsRapidPacketsOn0x6A0()
    {
        // End-to-end over the socket exactly as the captured gauge session does:
        // $A1 defines a slot, $A0 starts the stream, then the rapid-packet frames
        // must arrive on 0x6A0 - the path that produced silence before the fix.
        FordUdsDispatch.StopBroadcast();   // clean slate (shared singleton state)
        FordUdsDispatch.ResetDmrSlotMap();

        var node = NodeFactory.CreateNode();
        node.PersonaId = "ford-uds";
        var (_, server) = StartWith(node);
        var gauge = await Gauge.ConnectAsync(server);
        try
        {
            // $A1: bind slot 0x01 -> RAM 0x003F7FA0 (verbatim from the gauge capture).
            // The ack returns as an ISO-TP single frame: PCI length 0x02 + [E1 01].
            gauge.SendUsdtSingleFrame(PhysReq, new byte[] { 0xA1, 0x01, 0x8C, 0x00, 0x3F, 0x7F, 0xA0 });
            Assert.Equal(new byte[] { 0x02, 0xE1, 0x01 }, gauge.ReadUntilCanId(UsdtResp));

            // $A0: start the broadcast. Ack is the single frame 0x02 + [E0 01].
            gauge.SendUsdtSingleFrame(PhysReq, new byte[] { 0xA0, 0x01 });
            Assert.Equal(new byte[] { 0x02, 0xE0, 0x01 }, gauge.ReadUntilCanId(UsdtResp));

            // The rapid-packet stream now flows on 0x6A0: [prefix][0xE0][slot][value..].
            var dmr = gauge.ReadUntilCanId(EngineDmrCanId);
            Assert.Equal(0xE0, dmr[1]);    // mandatory rapid-packet marker
            Assert.Equal(0x01, dmr[2]);    // the slot we bound
        }
        finally
        {
            await gauge.DisposeAsync();
            FordUdsDispatch.StopBroadcast();
        }
    }
}
