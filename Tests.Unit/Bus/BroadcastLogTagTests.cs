using System.Collections.Generic;
using Core.Bus;
using Core.Ecu;
using Core.Transport;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.Bus;

// Locks in the "Hide broadcasts" classification: a host-ward frame whose CAN
// ID matches a configured DBC broadcast (EcuNode.Broadcasts) is tagged
// isBroadcast=true on the LogFrame sink; diagnostic response IDs are not. The
// UI's "Hide broadcasts" checkbox keys off exactly this flag.
public class BroadcastLogTagTests
{
    private static byte[] Frame(uint canId)
    {
        var f = new byte[CanFrame.IdBytes + 1];
        CanFrame.WriteId(f, canId);
        f[CanFrame.IdBytes] = 0x00;
        return f;
    }

    [Fact]
    public void Broadcast_canId_is_tagged_isBroadcast_diagnostic_is_not()
    {
        var bus = new VirtualBus();
        var node = NodeFactory.CreateNode();        // UsdtResp $7E8
        node.ReplaceBroadcasts(new[] { new BroadcastMessage { CanId = 0x12D, Enabled = true, PeriodMs = 16 } });
        bus.AddNode(node);

        var tagged = new List<(string pretty, bool isBroadcast)>();
        bus.LogFrame = (pretty, _, _, isBcast) => tagged.Add((pretty, isBcast));

        bus.LogRx(1, Frame(0x12D));                 // configured broadcast id
        bus.LogRx(1, Frame(0x7E8));                 // diagnostic response id

        var bcast = tagged.Find(t => t.pretty.Contains(" 12D "));
        var diag  = tagged.Find(t => t.pretty.Contains(" 7E8 "));
        Assert.True(bcast.isBroadcast, "broadcast CAN id should be tagged isBroadcast");
        Assert.False(diag.isBroadcast, "diagnostic response id must not be tagged isBroadcast");
    }

    [Fact]
    public void DeliveryPathFlag_TagsPersonaUudt_NotInBroadcasts()
    {
        // FordUdsDispatch's $A0 DMR stream emits on 0x6A0 - NOT in any ECU's
        // Broadcasts list, so the CAN-id heuristic alone misses it. The
        // delivery-path flag (set by IpcSessionState.BroadcastFrame) is what
        // tags it, so "Hide broadcasts" catches persona UUDT too.
        var bus = new VirtualBus();
        bus.AddNode(NodeFactory.CreateNode());      // no Broadcasts configured

        var tagged = new List<(string pretty, bool isBroadcast)>();
        bus.LogFrame = (pretty, _, _, isBcast) => tagged.Add((pretty, isBcast));

        bus.LogRx(1, Frame(0x6A0), isBroadcast: true);    // persona UUDT, flagged at source
        bus.LogRx(1, Frame(0x7E8), isBroadcast: false);   // directed diag response

        var uudt = tagged.Find(t => t.pretty.Contains(" 6A0 "));
        var diag = tagged.Find(t => t.pretty.Contains(" 7E8 "));
        Assert.True(uudt.isBroadcast, "persona UUDT delivered via broadcaster should be tagged isBroadcast");
        Assert.False(diag.isBroadcast, "directed diagnostic response must not be tagged");
    }
}
