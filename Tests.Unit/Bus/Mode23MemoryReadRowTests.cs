using Common.PassThru;
using Common.Protocol;
using Core.Bus;
using Core.Ecu;
using Core.Protocol;
using Core.Transport;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.Bus;

// Mode23 PID rows backing $23 ReadMemoryByAddress. A row whose Address equals the
// requested memory address is answered from its value source (here StaticBytes),
// truncated or zero-padded to the request's length. The check lives in
// VirtualBus.DispatchUsdt before stack dispatch (TryAnswerMemoryReadFromRows), so
// it applies to every stack that carries $23 - GMW3110 (which otherwise declines
// $23) and Ford UDS alike - and a row WINS over the loaded flash bin and the
// RAM-read-zeros fallback. A request that matches no row falls through unchanged.
//
// These drive the real inbound path (DispatchHostTx with a single ISO-TP frame).
// The Ford in-bin fall-through reads the FordUdsDispatch singleton's flash bin, so
// this class joins the FordUdsPersona collection and resets the bin around tests.
[Collection(FordUdsPersonaCollection.Name)]
public sealed class Mode23MemoryReadRowTests
{
    // 23 <4-byte BE addr> <2-byte BE len> - the 7-byte ReadMemoryByAddress shape.
    private static byte[] ReadMemoryRequest(uint addr, ushort len) => new byte[]
    {
        0x23,
        (byte)(addr >> 24), (byte)(addr >> 16), (byte)(addr >> 8), (byte)addr,
        (byte)(len >> 8), (byte)len,
    };

    private static (VirtualBus bus, EcuNode node, ChannelSession ch) Setup(string personaId)
    {
        var bus = new VirtualBus();
        var node = NodeFactory.CreateNode();
        node.PersonaId = personaId;
        bus.AddNode(node);
        var ch = new ChannelSession { Id = 1, Protocol = ProtocolID.CAN, Baud = 500_000, Bus = bus };
        return (bus, node, ch);
    }

    private static void SendSingleFrame(VirtualBus bus, ChannelSession ch, byte[] usdt)
    {
        Assert.True(usdt.Length <= 7, "single-frame helper only carries up to 7 bytes");
        var frame = new byte[CanFrame.IdBytes + 1 + usdt.Length];
        CanFrame.WriteId(frame, NodeFactory.PhysReq);
        frame[CanFrame.IdBytes] = (byte)usdt.Length;
        usdt.CopyTo(frame, CanFrame.IdBytes + 1);
        bus.DispatchHostTx(frame, ch);
    }

    private static Pid Mode23Row(uint address, byte[] staticBytes) => new()
    {
        Mode = PidMode.Mode23,
        Address = address,
        Name = "mem",
        LengthBytes = staticBytes.Length,
        StaticBytes = staticBytes,
    };

    [Fact]
    public void Row_AnswersMatchingAddress_OnGmPersona_WhichOtherwiseDeclines23()
    {
        // GM ($gmw3110) doesn't dispatch $23 - bare it would NRC $11. With a Mode23
        // row at the address, the pre-stack check answers it positively.
        FordUdsDispatch.LoadFlashBin((byte[]?)null);
        var (bus, node, ch) = Setup("gmw3110");
        node.AddPid(Mode23Row(0x000100C0, new byte[] { 0xAA, 0xBB, 0xCC, 0xDD }));

        SendSingleFrame(bus, ch, ReadMemoryRequest(0x000100C0, 4));

        var resp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(new byte[] { 0x63, 0xAA, 0xBB, 0xCC, 0xDD }, resp);
        TestFrame.AssertEmpty(ch);
    }

    [Fact]
    public void Row_WinsOverLoadedFlashBin_OnFordPersona()
    {
        // Ford with a bin loaded would read the bin bytes; a Mode23 row at the same
        // address overrides it (explicit config wins).
        var bin = new byte[0x20000];
        System.Text.Encoding.ASCII.GetBytes("6FPA", 0, 4, bin, 0x100C0);
        FordUdsDispatch.LoadFlashBin(bin);
        try
        {
            var (bus, node, ch) = Setup("ford-uds");
            node.AddPid(Mode23Row(0x000100C0, new byte[] { 0x11, 0x22, 0x33, 0x44 }));

            SendSingleFrame(bus, ch, ReadMemoryRequest(0x000100C0, 4));

            var resp = TestFrame.DequeueSingleFrameUsdt(ch);
            Assert.Equal(new byte[] { 0x63, 0x11, 0x22, 0x33, 0x44 }, resp);  // row bytes, not "6FPA"
        }
        finally
        {
            FordUdsDispatch.LoadFlashBin((byte[]?)null);
        }
    }

    [Fact]
    public void Row_TruncatesAndZeroPadsToRequestedLength()
    {
        FordUdsDispatch.LoadFlashBin((byte[]?)null);
        var (bus, node, ch) = Setup("gmw3110");
        node.AddPid(Mode23Row(0x00004000, new byte[] { 0xAA, 0xBB, 0xCC, 0xDD }));

        // Shorter than the row -> truncated.
        SendSingleFrame(bus, ch, ReadMemoryRequest(0x00004000, 2));
        Assert.Equal(new byte[] { 0x63, 0xAA, 0xBB }, TestFrame.DequeueSingleFrameUsdt(ch));

        // Longer than the row -> zero-padded.
        SendSingleFrame(bus, ch, ReadMemoryRequest(0x00004000, 6));
        Assert.Equal(new byte[] { 0x63, 0xAA, 0xBB, 0xCC, 0xDD, 0x00, 0x00 }, TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void NonMatchingAddress_FallsThrough_ToGmServiceNotSupportedNrc()
    {
        // A row at one address must not answer a read of a different address - GM
        // declines $23, so the unmatched read NRCs $11 ServiceNotSupported.
        FordUdsDispatch.LoadFlashBin((byte[]?)null);
        var (bus, node, ch) = Setup("gmw3110");
        node.AddPid(Mode23Row(0x00004000, new byte[] { 0xAA, 0xBB }));

        SendSingleFrame(bus, ch, ReadMemoryRequest(0x00005000, 2));

        var resp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(new byte[] { 0x7F, 0x23, Nrc.ServiceNotSupported }, resp);
    }
}
