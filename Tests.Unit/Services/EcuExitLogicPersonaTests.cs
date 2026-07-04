using Core.Bus;
using Core.Protocol;
using Core.Services;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.Services;

// EcuExitLogic.Run reverts a runtime UDS-kernel handover back to GMW3110, but
// must NOT touch a *configured* persona. ResetEcuState (the connection-type
// flip / "Reset state" button) funnels through EcuExitLogic, so an
// unconditional persona reset silently reverted a loaded FordUdsPersona to
// gmw3110 - after which the capture sink NRC'd PCMTec's Mode $09 ($7F 09 11)
// instead of answering VIN/CalID. Regression guard for that.
[Collection(FordUdsPersonaCollection.Name)]
public sealed class EcuExitLogicPersonaTests
{
    [Fact]
    public void Run_RevertsRuntimeUdsKernelHandover_ToBaselineStacks()
    {
        var bus = new VirtualBus();
        var node = NodeFactory.CreateNode();
        bus.AddNode(node);

        // $36 sub $80 DownloadAndExecute replaces the stacks with the kernel binding at runtime;
        // $20 / P3C timeout is the documented hand-back point.
        node.EnterKernelMode(Core.Protocol.ProtocolStacks.KernelBindingFor(node));
        Assert.True(node.InKernelMode);

        EcuExitLogic.Run(node, bus.Scheduler, respondOn: null);

        Assert.False(node.InKernelMode);   // baseline GM stacks restored
        Assert.Equal(new[] { "J1979", "GMW3110" }, node.Stacks.Select(b => b.Stack.Standard).ToArray());
    }

    [Fact]
    public void Run_PreservesConfiguredFordUdsPersona()
    {
        var bus = new VirtualBus();
        var node = NodeFactory.CreateNode();
        bus.AddNode(node);

        // ford-uds is loaded from the config file (user state), not a runtime
        // handover - it must survive a reset/exit.
        node.PersonaId = "ford-uds";

        EcuExitLogic.Run(node, bus.Scheduler, respondOn: null);

        Assert.Equal("ford-uds", node.PersonaId);
    }

    [Fact]
    public void Run_LeavesStockGmw3110PersonaUntouched()
    {
        var bus = new VirtualBus();
        var node = NodeFactory.CreateNode();          // default persona is gmw3110
        bus.AddNode(node);

        EcuExitLogic.Run(node, bus.Scheduler, respondOn: null);

        Assert.Equal("gmw3110", node.PersonaId);
    }
}
