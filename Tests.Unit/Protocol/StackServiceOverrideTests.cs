using Common.Persistence;
using Core.Persistence;
using Core.Protocol;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.Protocol;

// Per-stack service-allow-list overrides (DESIGN doc section 6): EcuNode.SetServiceOverride narrows
// or widens which SIDs a bound standard answers, and ConfigStore round-trips it as EcuDto.Stacks,
// storing only the delta off the synthesized default. These lock the dispatch effect AND the
// render-full / store-delta persistence contract.
public sealed class StackServiceOverrideTests
{
    private const byte Sid1A = 0x1A;   // ReadDataByIdentifier (in the restricted enhanced set)
    private const byte Sid22 = 0x22;   // ReadDataByParameterIdentifier (OBD-dispatcher only)
    private const byte Sid2C = 0x2C;   // DynamicallyDefineMessage (GMW3110 catalog)

    [Fact]
    public void Override_NarrowsGmw3110Binding_NrcsRemovedSid()
    {
        var node = NodeFactory.CreateNode();   // OBD GM: J1979 + GMW3110 (wildcard)
        Assert.Equal("GMW3110", node.Resolve(NodeFactory.PhysReq, Sid2C)?.Stack.Standard);

        // Remove $2C from the GMW3110 allow-list (every other catalog SID stays).
        var keep = StandardCatalogs.Gmw3110.Sids.Where(s => s != Sid2C);
        node.SetServiceOverride("GMW3110", new SidAllowList(keep));

        Assert.Null(node.Resolve(NodeFactory.PhysReq, Sid2C));                               // unowned -> NRC $11
        Assert.Equal("GMW3110", node.Resolve(NodeFactory.PhysReq, Sid22)?.Stack.Standard);   // others unaffected
        Assert.Equal(0x11, node.PrimaryForCanId(NodeFactory.PhysReq)!.Stack.Nrc.ServiceNotSupported);
    }

    [Fact]
    public void ClearOverride_RestoresDefault()
    {
        var node = NodeFactory.CreateNode();
        node.SetServiceOverride("GMW3110", new SidAllowList(new byte[] { Sid22 }));
        Assert.Null(node.Resolve(NodeFactory.PhysReq, Sid2C));

        node.SetServiceOverride("GMW3110", null);
        Assert.Equal("GMW3110", node.Resolve(NodeFactory.PhysReq, Sid2C)?.Stack.Standard);
        Assert.Null(node.ServiceOverrides);   // dropping the last override clears the map entirely
    }

    [Fact]
    public void Override_DoesNotLeakAcrossStandards()
    {
        var node = NodeFactory.CreateNode();
        node.SetServiceOverride("GMW3110", new SidAllowList(new byte[] { Sid22 }));
        // The J1979 binding is untouched, so $01 still resolves there.
        Assert.Equal("J1979", node.Resolve(NodeFactory.PhysReq, 0x01)?.Stack.Standard);
    }

    [Fact]
    public void RoundTrip_ExplicitAllowList_PersistsDelta_And_Reapplies()
    {
        var node = NodeFactory.CreateNode();
        var keep = StandardCatalogs.Gmw3110.Sids.Where(s => s != Sid2C).ToArray();
        node.SetServiceOverride("GMW3110", new SidAllowList(keep));

        var dto = ConfigStore.EcuDtoFrom(node);
        var gm = Assert.Single(dto.Stacks!);
        Assert.Equal("GMW3110", gm.Standard);
        Assert.NotNull(gm.Services);
        Assert.DoesNotContain("0x2C", gm.Services!);   // the disabled SID is absent from the delta
        Assert.Contains("0x22", gm.Services!);

        var reloaded = ConfigStore.EcuNodeFrom(dto);
        Assert.Null(reloaded.Resolve(NodeFactory.PhysReq, Sid2C));
        Assert.Equal("GMW3110", reloaded.Resolve(NodeFactory.PhysReq, Sid22)?.Stack.Standard);
    }

    [Fact]
    public void RoundTrip_NoOverride_IsQuiet()
    {
        var dto = ConfigStore.EcuDtoFrom(NodeFactory.CreateNode());
        Assert.Null(dto.Stacks);   // a standard config carries no stacks[] at all
    }

    [Fact]
    public void RoundTrip_WildcardOverride_OnEnhancedNode_WidensToFullSet()
    {
        // A GM node on a non-OBD (GMLAN enhanced) CAN id binds only the restricted 9-SID set, so $22
        // is NOT answered by default.
        var node = NodeFactory.CreateNode();
        node.PhysicalRequestCanId = 0x241;
        Assert.Null(node.Resolve(0x241, Sid22));

        // The user enables every service -> the wildcard "*".
        node.SetServiceOverride("GMW3110", AllServices.Instance);
        Assert.Equal("GMW3110", node.Resolve(0x241, Sid22)?.Stack.Standard);

        var dto = ConfigStore.EcuDtoFrom(node);
        var gm = Assert.Single(dto.Stacks!);
        Assert.Equal("GMW3110", gm.Standard);
        Assert.Null(gm.Services);   // wildcard serialises as null services

        var reloaded = ConfigStore.EcuNodeFrom(dto);
        Assert.Equal("GMW3110", reloaded.Resolve(0x241, Sid22)?.Stack.Standard);
    }

    [Fact]
    public void RoundTrip_ThroughJson_PreservesOverride()
    {
        var node = NodeFactory.CreateNode();
        node.SetServiceOverride("GMW3110", new SidAllowList(new byte[] { Sid1A, Sid22 }));

        var cfg = new SimulatorConfig();
        cfg.Ecus.Add(ConfigStore.EcuDtoFrom(node));
        var back = ConfigSerializer.Deserialize(ConfigSerializer.Serialize(cfg));
        var reloaded = ConfigStore.EcuNodeFrom(back.Ecus[0]);

        Assert.Equal("GMW3110", reloaded.Resolve(NodeFactory.PhysReq, Sid22)?.Stack.Standard);
        Assert.Null(reloaded.Resolve(NodeFactory.PhysReq, Sid2C));   // not in the 2-SID allow-list
    }
}
