using Common.PassThru;
using Common.Protocol;
using Common.Waveforms;
using Core.Bus;
using Core.Ecu;
using Core.Protocol;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.Ecu;

// $22 ReadDataByIdentifier is identical across the GMW3110 and UDS stacks, so a
// Ford ECU must answer it the same way - via the persona-agnostic Service22Handler -
// not NRC it. Since the protocol-stack migration, the Ford UDS stack's dispatch
// (ProtocolStacks.DispatchUds) serves $22 directly (the old shared CommonServices
// fallback is gone); these tests pin that down end to end.
//
// They drive the real inbound path (DispatchHostTx -> reassembler -> DispatchUsdt
// -> Resolve -> the Ford catch-all UDS binding). The Ford capture state is
// process-wide static, so join its collection.
[Collection(FordUdsPersonaCollection.Name)]
public sealed class CommonServicesDispatchTests
{
    private const ushort PhysReq = NodeFactory.PhysReq;

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

    private static (VirtualBus bus, ChannelSession ch) FordBus(EcuNode node)
    {
        var bus = new VirtualBus();
        node.PersonaId = "ford-uds";
        bus.AddNode(node);
        var ch = new ChannelSession { Id = 1, Protocol = ProtocolID.CAN, Baud = 500_000, Bus = bus };
        return (bus, ch);
    }

    [Fact]
    public void FordPersona_22_ConfiguredDid_AnswersPositiveViaCommonLayer()
    {
        var node = NodeFactory.CreateNode();
        node.AddPid(new Pid
        {
            Address = 0x0200,
            Size = PidSize.Byte,
            DataType = PidDataType.Unsigned,
            Scalar = 1.0,
            Offset = 0.0,
            WaveformConfig = new WaveformConfig { Shape = WaveformShape.Constant, Offset = 0x5A },
        });
        var (bus, ch) = FordBus(node);

        // ForScan's connect probe shape: $22 DID 0x0200.
        bus.DispatchHostTx(WrapCanFrame(PhysReq, new byte[] { 0x03, 0x22, 0x02, 0x00 }), ch);

        Assert.Equal(new byte[] { 0x62, 0x02, 0x00, 0x5A }, TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void FordPersona_22_UnknownDid_GivesNrc31_NotServiceNotSupported()
    {
        // No PID configured: $22 must reach Service22Handler and return the
        // spec-correct NRC $31 RequestOutOfRange - NOT the persona's old
        // blanket $11 ServiceNotSupported.
        var (bus, ch) = FordBus(NodeFactory.CreateNode());

        bus.DispatchHostTx(WrapCanFrame(PhysReq, new byte[] { 0x03, 0x22, 0x02, 0x00 }), ch);

        Assert.Equal(
            new byte[] { Service.NegativeResponse, Service.ReadDataByParameterIdentifier, Nrc.RequestOutOfRange },
            TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void FordPersona_NotPresentSid_IsLoggedThenNrc11()
    {
        // A SID the Ford persona doesn't implement ($99, absent from the Ford catalog) is logged by
        // the capture stack, then declined so the bus NRC-$11s it - matching a real Ford PCM, which
        // answers serviceNotSupported to any unsupported physically-addressed service rather than
        // sitting silent (which would force the tester through its P2 timeout). Catalog membership is
        // an internal modelling artifact with no on-wire correlate, so an out-of-catalog probe gets the
        // same NRC $11 an in-catalog-but-unhandled SID does.
        var (bus, ch) = FordBus(NodeFactory.CreateNode());

        bus.DispatchHostTx(WrapCanFrame(PhysReq, new byte[] { 0x01, 0x99 }), ch);

        Assert.Equal(
            new byte[] { Service.NegativeResponse, 0x99, Nrc.ServiceNotSupported },
            TestFrame.DequeueSingleFrameUsdt(ch));
    }
}
