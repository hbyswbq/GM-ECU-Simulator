using Common.PassThru;
using Common.Protocol;
using Core.Bus;
using Core.Ecu;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.Protocol;

// Bus-level behaviour of the GM stack routing and the dual-dispatcher model (step 3). The
// real E38/E67 OBD-vs-enhanced split is now STRUCTURAL: a GM node on an OBD id ($7E0..$7E7)
// binds the full GMW3110 set + J1979; a GM node on a non-OBD (GMLAN enhanced-diag) id binds
// only the restricted 9-SID set ($1A/$20/$27/$28/$34/$36/$3E/$A2/$A5). So the OBD-only SIDs
// ($01/$22/...) are simply not owned on a non-OBD node and Resolve NRC-$11s them - the
// behaviour the deleted RequireUdsStack gate used to produce per-handler.
public sealed class GmStackDispatchTests
{
    private static byte[] WrapCanFrame(uint canId, byte[] data)
    {
        var f = new byte[4 + data.Length];
        f[0] = (byte)((canId >> 24) & 0xFF);
        f[1] = (byte)((canId >> 16) & 0xFF);
        f[2] = (byte)((canId >> 8) & 0xFF);
        f[3] = (byte)(canId & 0xFF);
        data.CopyTo(f, 4);
        return f;
    }

    private static (VirtualBus bus, ChannelSession ch) BusWith(EcuNode node)
    {
        var bus = new VirtualBus();
        bus.AddNode(node);
        var ch = new ChannelSession { Id = 1, Protocol = ProtocolID.CAN, Baud = 500_000, Bus = bus };
        return (bus, ch);
    }

    [Fact]
    public void Gm_ObdId_01_AnswersPositive()
    {
        // Default GM node on the OBD $7E0 pair: $01 $00 (PID-support bitmask) -> positive $41 $00.
        var (bus, ch) = BusWith(NodeFactory.CreateNode());

        bus.DispatchHostTx(WrapCanFrame(NodeFactory.PhysReq, new byte[] { 0x02, 0x01, 0x00 }), ch);

        var resp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(0x41, resp[0]);
        Assert.Equal(0x00, resp[1]);
    }

    [Fact]
    public void Gm_NonObdId_01_GetsNrc11_NotPositive()
    {
        // GM node configured on the GMW3110 spec-example $241 (a non-OBD id). $01 must NRC $11,
        // matching real E38/E67 silicon - NOT answer positively. This is the regression the
        // step-2 adversarial review caught; step 3 makes it structural (no J1979 binding here).
        var (bus, ch) = BusWith(EnhancedNode());

        bus.DispatchHostTx(WrapCanFrame(0x241, new byte[] { 0x02, 0x01, 0x00 }), ch);

        Assert.Equal(
            new byte[] { Service.NegativeResponse, Service.Obd01ShowCurrentData, Nrc.ServiceNotSupported },
            TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void Gm_NonObdId_22_GetsNrc11_NotInEnhancedSet()
    {
        // $22 is an OBD-only SID, absent from the restricted enhanced-diag allow-list, so a GM
        // node on a non-OBD id does not own it and Resolve NRC-$11s it. (An enhanced-set SID
        // like $1A WOULD be answered on this same node - see Gm_NonObdId_1A_IsAnswered.)
        var (bus, ch) = BusWith(EnhancedNode());

        bus.DispatchHostTx(WrapCanFrame(0x241, new byte[] { 0x03, 0x22, 0xF4, 0x0C }), ch);

        Assert.Equal(
            new byte[] { Service.NegativeResponse, Service.ReadDataByParameterIdentifier, Nrc.ServiceNotSupported },
            TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void Gm_NonObdId_1A_IsAnswered()
    {
        // $1A IS in the restricted enhanced-diag set, so a non-OBD GM node answers it (proving
        // the restricted binding handles the 9-SID set, not just NRCs everything). $1A $B0 reads
        // the diagnostic address; the response is "5A B0 <diagAddr>".
        var node = EnhancedNode();
        node.DiagnosticAddress = 0x11;
        var (bus, ch) = BusWith(node);

        bus.DispatchHostTx(WrapCanFrame(0x241, new byte[] { 0x02, 0x1A, 0xB0 }), ch);

        Assert.Equal(new byte[] { 0x5A, 0xB0, 0x11 }, TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void Gm_NonObdId_01_Functional_StaysSilent()
    {
        // A functional $01 broadcast reaches the enhanced node too, but $01 is not owned there,
        // so it stays silent (never NRCs on functional) - matching the old gate's functional path.
        var (bus, ch) = BusWith(EnhancedNode());

        bus.DispatchHostTx(WrapCanFrame(GmlanCanId.Obd2FunctionalRequest, new byte[] { 0x02, 0x01, 0x00 }), ch);

        TestFrame.AssertEmpty(ch);
    }

    private static EcuNode EnhancedNode() => new()
    {
        Name = "GmEnhanced",
        PhysicalRequestCanId = 0x241,
        UsdtResponseCanId = 0x641,
        UudtResponseCanId = 0x541,
    };
}
