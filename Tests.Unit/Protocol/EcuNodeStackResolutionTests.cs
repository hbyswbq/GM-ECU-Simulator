using Core.Protocol;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.Protocol;

// EcuNode.Stacks synthesis + (canId, sid) resolution (DESIGN doc section 5). Step
// 1 is plumbing only - nothing in VirtualBus consults Resolve yet - so these
// assert the resolution data model, not on-wire behaviour.
public sealed class EcuNodeStackResolutionTests
{
    [Fact]
    public void GmNode_SynthesizesJ1979PlusGmw3110()
    {
        var node = NodeFactory.CreateNode();   // default persona = gmw3110
        Assert.Equal(
            new[] { "J1979", "GMW3110" },
            node.Stacks.Select(b => b.Stack.Standard).ToArray());
    }

    [Fact]
    public void GmNode_Resolves22ToGmw3110_And01ToJ1979()
    {
        var node = NodeFactory.CreateNode();

        // $22 is GMW3110 (J1979 has no $22, so the wildcard J1979 binding does not grab it).
        Assert.Equal("GMW3110", node.Resolve(NodeFactory.PhysReq, 0x22)?.Stack.Standard);
        // $01 is J1979 (GMW3110 catalog has no $01, so its wildcard binding does not grab it).
        Assert.Equal("J1979", node.Resolve(NodeFactory.PhysReq, 0x01)?.Stack.Standard);
    }

    [Fact]
    public void GmNode_UnownedSid_ResolvesNull()
        => Assert.Null(NodeFactory.CreateNode().Resolve(NodeFactory.PhysReq, 0x99));

    [Fact]
    public void GmNode_ResolvesOnFunctionalIds()
    {
        var node = NodeFactory.CreateNode();
        Assert.Equal("GMW3110", node.Resolve(AddressingModel.Obd2Functional, 0x22)?.Stack.Standard);
        Assert.Equal("GMW3110", node.Resolve(AddressingModel.GmlanFunctional, 0x22)?.Stack.Standard);
    }

    [Fact]
    public void PrimaryForCanId_ReturnsFirstMatchingBindingWithGmNrc()
    {
        var node = NodeFactory.CreateNode();
        var primary = node.PrimaryForCanId(NodeFactory.PhysReq);
        Assert.NotNull(primary);
        Assert.Equal(0x11, primary!.Stack.Nrc.ServiceNotSupported);   // GM serviceNotSupported
    }

    [Fact]
    public void FordNode_SynthesizesSingleCatchAllFordBinding()
    {
        var node = NodeFactory.CreateNode();
        node.PersonaId = "ford-uds";

        // The Ford capture stack is a single catch-all logger binding on the comprehensive Ford
        // catalog (OBD + UDS + proprietary); no separate J1979 binding.
        Assert.Equal(new[] { "Ford" }, node.Stacks.Select(b => b.Stack.Standard).ToArray());
        Assert.True(node.Stacks[0].CatchAll);
        // CatchAll owns every SID (it must log all probes): catalog SIDs and even GM-only
        // $AA alike resolve to it (the Ford dispatch then answers or NRCs internally).
        Assert.Equal("Ford", node.Resolve(NodeFactory.PhysReq, 0x2E)?.Stack.Standard);   // WriteDataByIdentifier
        Assert.Equal("Ford", node.Resolve(NodeFactory.PhysReq, 0xAA)?.Stack.Standard);   // GM-only, still owned (catch-all)
        // The Ford stack carries the UDS NRC vocabulary: bad-length is $13, not GM's $12.
        Assert.Equal(0x13, node.Resolve(NodeFactory.PhysReq, 0x2E)!.Stack.Nrc.BadLength);
    }

    [Fact]
    public void RebuildStacks_PicksUpChangedCanId()
    {
        var node = NodeFactory.CreateNode();
        _ = node.Stacks;                       // force initial synthesis at 0x7E0
        node.PhysicalRequestCanId = 0x7E1;
        node.RebuildStacks();

        Assert.Equal("GMW3110", node.Resolve(0x7E1, 0x22)?.Stack.Standard);
        Assert.Null(node.Resolve(NodeFactory.PhysReq, 0x22));   // old id no longer bound
    }

    [Fact]
    public void KernelMode_SurvivesBaselineCacheInvalidation()
    {
        // A CAN-id (or PersonaId) setter nulls the baseline Stacks cache. If that fires while an
        // SPS kernel handover is active, the kernel must NOT be silently demoted to the baseline
        // dispatcher - the kernel binding is held separately from the baseline cache.
        var node = NodeFactory.CreateNode();
        node.EnterKernelMode(ProtocolStacks.KernelBindingFor(node));
        Assert.True(node.InKernelMode);

        node.PhysicalRequestCanId = 0x7E1;                 // invalidates the baseline cache
        Assert.True(node.InKernelMode);
        Assert.Equal(new[] { "UDS-Kernel" }, node.Stacks.Select(b => b.Stack.Standard).ToArray());

        // On exit the baseline re-synthesises fresh, picking up the new CAN id.
        node.ExitKernelMode();
        Assert.False(node.InKernelMode);
        Assert.Equal("GMW3110", node.Resolve(0x7E1, 0x22)?.Stack.Standard);
        Assert.Null(node.Resolve(NodeFactory.PhysReq, 0x22));   // old id no longer bound
    }

    [Fact]
    public void Stacks_IsMutable_ForRuntimeKernelBinding()
    {
        var node = NodeFactory.CreateNode();
        // A transient binding pushed at runtime (the SPS kernel case) is visible to Resolve.
        var kernel = new ProtocolStack("KERNEL",
            ServiceCatalog.Of((0x31, "RoutineControl")), NrcProfile.Gm, SessionModel.Gm,
            TimingProfile.Gm, new AddressingModel(Request: NodeFactory.PhysReq, Response: NodeFactory.UsdtResp));
        node.Stacks.Insert(0, new StackBinding(kernel, kernel.Addressing, AllServices.Instance));

        Assert.Equal("KERNEL", node.Resolve(NodeFactory.PhysReq, 0x31)?.Stack.Standard);
    }
}
