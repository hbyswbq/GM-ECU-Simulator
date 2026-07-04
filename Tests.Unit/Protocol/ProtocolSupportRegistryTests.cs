using System.Linq;
using Common.PassThru;
using Common.Protocol;
using Core.Bus;
using Core.Ecu;
using Core.Protocol;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.Protocol;

// Locks ProtocolSupportRegistry - the canonical user-facing "what does this app answer" registry
// the Protocols window renders - to two things it must never drift from:
//
//   1. The catalogs (the gospel). Every row's SID/name/group is REUSED from the source catalog;
//      these tests assert that, so a catalog rename can't leave the window showing a stale name.
//
//   2. Real dispatch behaviour. The headline claim of the window is "an unticked box means the
//      service is always NRC'd". UnimplementedServices_AlwaysNrcServiceNotSupported drives every
//      GMW3110 service the registry marks NOT implemented through the real bus and asserts it
//      NRC-$11s, and the implemented spot-checks assert the opposite. This is what makes the
//      registry canonical rather than a hand-maintained list that silently disagrees with the
//      switch statements in Gmw3110Dispatch / DispatchJ1979.
public sealed class ProtocolSupportRegistryTests
{
    private const ushort PhysReq = NodeFactory.PhysReq;
    private static byte SvcNotSupported => ProtocolStacks.Gmw3110.Nrc.ServiceNotSupported;

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

    // A bare GM node (default PersonaId "gmw3110") on the OBD CAN id $7E0, so it binds the full
    // GMW3110 + J1979 dispatchers - the same path VirtualBus.DispatchHostTx drives at runtime.
    private static (VirtualBus bus, ChannelSession ch) GmBus()
    {
        var bus = new VirtualBus();
        bus.AddNode(NodeFactory.CreateNode());
        var ch = new ChannelSession { Id = 1, Protocol = ProtocolID.CAN, Baud = 500_000, Bus = bus };
        return (bus, ch);
    }

    private static byte[] SendPhysical(VirtualBus bus, ChannelSession ch, byte sid)
    {
        // Minimal single-frame request: PCI length 1 + the SID byte. Ownership is by catalog
        // membership, not payload, so a one-byte request is enough to exercise the dispatch verdict.
        bus.DispatchHostTx(WrapCanFrame(PhysReq, new byte[] { 0x01, sid }), ch);
        return TestFrame.DequeueSingleFrameUsdt(ch);
    }

    [Fact]
    public void Protocols_AreTheExpectedSectionsInOrder()
    {
        Assert.Equal(
            new[] { "J1979", "GMW3110", "UDS", "UDS-Kernel" },
            ProtocolSupportRegistry.Protocols.Select(p => p.Id).ToArray());
    }

    [Fact]
    public void EveryServiceRow_ReusesItsCatalogDescriptor()
    {
        // The registry must never re-describe a service: each row's SID/name/group has to match the
        // descriptor in the catalog it was projected from. Map each section back to its source.
        foreach (var p in ProtocolSupportRegistry.Protocols)
        {
            var catalog = SourceCatalog(p.Id);
            foreach (var s in p.Services)
            {
                Assert.True(catalog.TryGet(s.Sid, out var d), $"{p.Id} row ${s.Sid:X2} absent from its catalog");
                Assert.Equal(d!.Name, s.Name);
                Assert.Equal(d.Group, s.Group);
            }
        }
    }

    [Fact]
    public void Groups_PartitionEveryServiceExactlyOnce_IncludingTheNullCoreGroup()
    {
        // The window binds ProtocolSupport.Groups; a null group key (the ungrouped core block) must
        // not throw and must round-trip every service. (Regression: Dictionary.TryGetValue(null).)
        foreach (var p in ProtocolSupportRegistry.Protocols)
        {
            var flattened = p.Groups.SelectMany(g => g.Services).ToArray();
            Assert.Equal(p.Services, flattened);
        }
    }

    [Fact]
    public void Uds_Section_OmitsObdModes_KeepsUdsGospelAndProprietary()
    {
        // The Ford catalog is OBD + UDS + proprietary; the OBD modes belong to the J1979 section, so
        // the UDS section must drop them and keep exactly the UDS gospel plus $A0/$A1/$B1.
        var uds = ProtocolSupportRegistry.Protocols.Single(p => p.Id == "UDS");
        foreach (var sid in StandardCatalogs.J1979.Sids)
            Assert.DoesNotContain(uds.Services, s => s.Sid == sid);
        foreach (var sid in StandardCatalogs.Uds.Sids)
            Assert.Contains(uds.Services, s => s.Sid == sid);
        foreach (byte sid in new byte[] { 0xA0, 0xA1, 0xB1 })
            Assert.Contains(uds.Services, s => s.Sid == sid);
    }

    [Fact]
    public void Gmw3110_ImplementedFlags_MatchTheDeclaredSet()
    {
        var gm = ProtocolSupportRegistry.Protocols.Single(p => p.Id == "GMW3110");
        // The 3 GMW3110-catalog SIDs with no case in Gmw3110Dispatch - always NRC.
        byte[] alwaysNrc = { 0x12, 0x23, 0xA9 };
        foreach (var s in gm.Services)
            Assert.Equal(!alwaysNrc.Contains(s.Sid), s.Implemented);
    }

    [Fact]
    public void J1979_ImplementedFlags_AreOnly01And09()
    {
        var obd = ProtocolSupportRegistry.Protocols.Single(p => p.Id == "J1979");
        foreach (var s in obd.Services)
            Assert.Equal(s.Sid is 0x01 or 0x09, s.Implemented);
    }

    [Fact]
    public void Gmw3110_UnimplementedServices_AlwaysNrcServiceNotSupported()
    {
        // The canonical lock: every GMW3110 service the registry shows unticked must, on a real bus,
        // answer with serviceNotSupported. If someone adds a handler without ticking the registry
        // (or vice-versa) this fails.
        var gm = ProtocolSupportRegistry.Protocols.Single(p => p.Id == "GMW3110");
        foreach (var s in gm.Services.Where(s => !s.Implemented))
        {
            var (bus, ch) = GmBus();
            var rsp = SendPhysical(bus, ch, s.Sid);
            Assert.Equal(new byte[] { Service.NegativeResponse, s.Sid, SvcNotSupported }, rsp);
        }
    }

    [Theory]
    [InlineData((byte)0x22)]   // ReadDataByParameterIdentifier -> Service22Handler (NRC $31 unconfigured, not $11)
    [InlineData((byte)0x3E)]   // TesterPresent -> positive
    [InlineData((byte)0x1A)]   // ReadDataByIdentifier -> Service1AHandler
    public void Gmw3110_ImplementedService_DoesNotNrcServiceNotSupported(byte sid)
    {
        var (bus, ch) = GmBus();
        var rsp = SendPhysical(bus, ch, sid);
        bool isServiceNotSupported =
            rsp.Length == 3 && rsp[0] == Service.NegativeResponse && rsp[2] == SvcNotSupported;
        Assert.False(isServiceNotSupported, $"${sid:X2} is marked implemented but NRC'd serviceNotSupported");
    }

    [Fact]
    public void J1979_UnimplementedMode_AlwaysNrcServiceNotSupported()
    {
        // $03 ShowStoredDtcs is owned by the J1979 binding but has no handler -> serviceNotSupported.
        var (bus, ch) = GmBus();
        var rsp = SendPhysical(bus, ch, 0x03);
        Assert.Equal(new byte[] { Service.NegativeResponse, 0x03, SvcNotSupported }, rsp);
    }

    private static ServiceCatalog SourceCatalog(string id) => id switch
    {
        "J1979"      => StandardCatalogs.J1979,
        "GMW3110"    => StandardCatalogs.Gmw3110,
        "UDS"        => StandardCatalogs.Ford,            // UDS gospel + proprietary (OBD filtered out)
        "UDS-Kernel" => ProtocolStacks.UdsKernel.Catalog,
        _            => throw new System.ArgumentOutOfRangeException(nameof(id), id, "unknown protocol id"),
    };
}
