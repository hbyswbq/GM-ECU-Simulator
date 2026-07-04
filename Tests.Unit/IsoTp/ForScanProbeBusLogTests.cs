using System.Collections.Generic;
using Common.PassThru;
using Common.Wire;
using Core.Bus;
using Core.Protocol;
using EcuSimulator.Tests.TestHelpers;
using Shim.Ipc;
using Xunit;

namespace EcuSimulator.Tests.IsoTp;

// Diagnostic: does a host single-frame write ($22 0200 - ForScan's connect
// probe) to a ford-uds ECU at $7E0 produce an "Rx 7E0" line on bus.LogFrame,
// driven through the REAL RequestDispatcher.WriteMsgIso15765 -> DispatchHostTx
// path? The live "CAN frames" textbox is fed by bus.LogFrame; this isolates
// whether the host write reaches the bus log at all.
[Collection(EcuSimulator.Tests.TestHelpers.FordUdsPersonaCollection.Name)]
public class ForScanProbeBusLogTests
{
    private static (uint channelId, RequestDispatcher dispatcher, List<string> log) Open(VirtualBus bus)
    {
        var state = new IpcSessionState(bus);
        var dispatcher = new RequestDispatcher(state);

        var connect = new IpcWriter();
        connect.WriteU32(0);
        connect.WriteU32((uint)ProtocolID.ISO15765);
        connect.WriteU32(0);
        connect.WriteU32(500_000);
        var (_, resp) = dispatcher.Dispatch(IpcMessageTypes.ConnectRequest, connect.AsSpan());
        var rr = new IpcReader(resp);
        Assert.Equal((uint)ResultCode.STATUS_NOERROR, rr.ReadU32());

        var log = new List<string>();
        bus.LogFrame = (pretty, _, _, _) => log.Add(pretty);
        return (rr.ReadU32(), dispatcher, log);
    }

    private static ResultCode Write(RequestDispatcher d, uint channelId, byte[] frameData)
    {
        var w = new IpcWriter();
        w.WriteU32(channelId);
        w.WriteU32(1);
        w.WriteU32(1000);
        w.WritePassThruMsg(new PassThruMsg
        {
            ProtocolID = ProtocolID.ISO15765,
            TxFlags = TxFlag.ISO15765_FRAME_PAD,
            Data = frameData,
        });
        var (_, resp) = d.Dispatch(IpcMessageTypes.WriteMsgsRequest, w.AsSpan());
        return (ResultCode)new IpcReader(resp).ReadU32();
    }

    [Fact]
    public void ForScan_22_0200_probe_to_fordUds_ecu_logs_Rx_on_bus()
    {
        var bus = new VirtualBus();
        var node = NodeFactory.CreateNode();          // PhysReq $7E0 / UsdtResp $7E8
        node.PersonaId = "ford-uds";
        bus.AddNode(node);

        var (channelId, dispatcher, log) = Open(bus);

        // ForScan's connect probe: $22 DID 0x0200 to $7E0, no FC filter.
        byte[] frame = { 0x00, 0x00, 0x07, 0xE0, 0x22, 0x02, 0x00 };
        Write(dispatcher, channelId, frame);

        Assert.True(
            log.Exists(l => l.Contains(" Rx ") && l.Contains(" 7E0 ")),
            $"host write to $7E0 was not logged on bus.LogFrame. Full log:\n{string.Join("\n", log)}");
    }
}
