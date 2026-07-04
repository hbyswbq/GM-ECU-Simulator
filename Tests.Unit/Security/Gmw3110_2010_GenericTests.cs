using Common.Protocol;
using Core.Security;
using Core.Services;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.Security;

// Drives the generic module through Service27Handler so the egress wrapper is
// exercised end-to-end. Uses a FakeSeedKeyAlgorithm whose seed + key + the
// success bit are set per-test.
public sealed class Gmw3110_2010_GenericTests
{
    private readonly FakeSeedKeyAlgorithm algo;
    private readonly Core.Ecu.EcuNode node;
    private readonly Core.Bus.ChannelSession ch;
    private long nowMs;

    public Gmw3110_2010_GenericTests()
    {
        algo = new FakeSeedKeyAlgorithm
        {
            SeedToReturn = new byte[] { 0x12, 0x34 },
            ExpectedKey = new byte[] { 0xAB, 0xCD },
            ComputeKeySucceeds = true,
        };
        node = NodeFactory.CreateNodeWithGenericModule(algo);
        ch = NodeFactory.CreateChannel();
        nowMs = 0;
    }

    private void Dispatch(params byte[] usdt) => Service27Handler.Handle(node, usdt, ch, nowMs);
    private byte[] Pop() => TestFrame.DequeueSingleFrameUsdt(ch);

    [Fact]
    public void RequestSeed_Level1_ReturnsSeed_AndStoresPending()
    {
        Dispatch(0x27, 0x01);

        Assert.Equal(new byte[] { Service.Positive(Service.SecurityAccess), 0x01, 0x12, 0x34 }, Pop());
        Assert.Equal(1, node.State.SecurityPendingSeedLevel);
        Assert.Equal(new byte[] { 0x12, 0x34 }, node.State.SecurityLastIssuedSeed);
        Assert.Equal(0, node.State.SecurityUnlockedLevel);
    }

    [Fact]
    public void RequestSeed_WhenAlreadyUnlocked_ReturnsSeedAllZeros()
    {
        node.State.SecurityUnlockedLevel = 1;

        Dispatch(0x27, 0x01);

        Assert.Equal(new byte[] { Service.Positive(Service.SecurityAccess), 0x01, 0x00, 0x00 }, Pop());
    }

    [Fact]
    public void SendKey_WithoutPriorRequestSeed_ReturnsNrc22()
    {
        Dispatch(0x27, 0x02, 0xAB, 0xCD);

        Assert.Equal(new byte[] { Service.NegativeResponse, Service.SecurityAccess, Nrc.ConditionsNotCorrectOrSequenceError }, Pop());
    }

    [Fact]
    public void SendKey_WithCorrectKey_Unlocks()
    {
        Dispatch(0x27, 0x01); Pop();
        Dispatch(0x27, 0x02, 0xAB, 0xCD);

        Assert.Equal(new byte[] { Service.Positive(Service.SecurityAccess), 0x02 }, Pop());
        Assert.Equal(1, node.State.SecurityUnlockedLevel);
        Assert.Equal(0, node.State.SecurityFailedAttempts);
        Assert.Null(node.State.SecurityLastIssuedSeed);
        Assert.Equal(0, node.State.SecurityPendingSeedLevel);
    }

    [Fact]
    public void SendKey_WithWrongKey_OneAndTwo_ReturnsNrc35_IncrementsAttempts()
    {
        Dispatch(0x27, 0x01); Pop();

        Dispatch(0x27, 0x02, 0x00, 0x00);
        Assert.Equal(new byte[] { Service.NegativeResponse, Service.SecurityAccess, Nrc.InvalidKey }, Pop());
        Assert.Equal(1, node.State.SecurityFailedAttempts);

        Dispatch(0x27, 0x02, 0x00, 0x00);
        Assert.Equal(new byte[] { Service.NegativeResponse, Service.SecurityAccess, Nrc.InvalidKey }, Pop());
        Assert.Equal(2, node.State.SecurityFailedAttempts);
    }

    [Fact]
    public void SendKey_ThirdWrongKey_ReturnsNrc36_ArmsLockout()
    {
        Dispatch(0x27, 0x01); Pop();
        Dispatch(0x27, 0x02, 0x00, 0x00); Pop();
        Dispatch(0x27, 0x02, 0x00, 0x00); Pop();

        Dispatch(0x27, 0x02, 0x00, 0x00);

        Assert.Equal(new byte[] { Service.NegativeResponse, Service.SecurityAccess, Nrc.ExceededNumberOfAttempts }, Pop());
        Assert.Equal(3, node.State.SecurityFailedAttempts);
        Assert.True(node.State.SecurityLockoutUntilMs > 0);
        Assert.Null(node.State.SecurityLastIssuedSeed); // pending seed invalidated on lockout
    }

    [Fact]
    public void RequestDuringLockout_ReturnsNrc37()
    {
        // Arm lockout manually.
        node.State.SecurityFailedAttempts = 3;
        node.State.SecurityLockoutUntilMs = 10_000;
        nowMs = 5_000; // inside the window

        Dispatch(0x27, 0x01);

        Assert.Equal(new byte[] { Service.NegativeResponse, Service.SecurityAccess, Nrc.RequiredTimeDelayNotExpired }, Pop());
    }

    [Fact]
    public void Lockout_SelfHealsAfterDeadlinePasses_AndResetsAttempts()
    {
        node.State.SecurityFailedAttempts = 3;
        node.State.SecurityLockoutUntilMs = 10_000;
        nowMs = 10_001; // deadline elapsed

        Dispatch(0x27, 0x01);

        // Processed normally - seed comes back.
        Assert.Equal(new byte[] { Service.Positive(Service.SecurityAccess), 0x01, 0x12, 0x34 }, Pop());
        Assert.Equal(0, node.State.SecurityFailedAttempts);
        Assert.Equal(0, node.State.SecurityLockoutUntilMs);
    }

    [Fact]
    public void SendKey_AlgorithmRefuses_ReturnsNrc35()
    {
        algo.ComputeKeySucceeds = false;
        Dispatch(0x27, 0x01); Pop();

        Dispatch(0x27, 0x02, 0xAB, 0xCD);

        Assert.Equal(new byte[] { Service.NegativeResponse, Service.SecurityAccess, Nrc.InvalidKey }, Pop());
    }

    [Fact]
    public void Malformed_TooShort_ReturnsNrc12()
    {
        // $27 alone (no subfunction) - payload length 1.
        Dispatch(0x27);

        Assert.Equal(new byte[] { Service.NegativeResponse, Service.SecurityAccess, Nrc.SubFunctionNotSupportedInvalidFormat }, Pop());
    }

    [Fact]
    public void Malformed_UnsupportedSubFunction_ReturnsNrc12()
    {
        // Sub-function 0x99 → level (0x99+1)/2 = 0x4D, not in SupportedLevels.
        Dispatch(0x27, 0x99);

        Assert.Equal(new byte[] { Service.NegativeResponse, Service.SecurityAccess, Nrc.SubFunctionNotSupportedInvalidFormat }, Pop());
    }

    [Fact]
    public void Malformed_SubFunctionZero_ReturnsNrc12()
    {
        Dispatch(0x27, 0x00);

        Assert.Equal(new byte[] { Service.NegativeResponse, Service.SecurityAccess, Nrc.SubFunctionNotSupportedInvalidFormat }, Pop());
    }

    [Fact]
    public void Malformed_RequestSeedWithExtraBytes_ReturnsNrc12()
    {
        // requestSeed (odd sub) must be exactly 2 bytes.
        Dispatch(0x27, 0x01, 0xFF);

        Assert.Equal(new byte[] { Service.NegativeResponse, Service.SecurityAccess, Nrc.SubFunctionNotSupportedInvalidFormat }, Pop());
    }

    // ----- SecurityModuleBehaviour.BypassAll -----
    //
    // Bypass behaviour now lives on the module (not the algorithm) and is
    // unconditional - no programming-session gating. These tests run against
    // a separate node wired with a bypass-configured generic module.

    private (Core.Ecu.EcuNode node, Core.Bus.ChannelSession ch) BypassNode()
    {
        var bypassAlgo = new FakeSeedKeyAlgorithm
        {
            SeedToReturn = new byte[] { 0xDE, 0xAD },     // ignored - bypass emits 00 00
            ExpectedKey = new byte[] { 0xAB, 0xCD },
            ComputeKeySucceeds = false,                   // would NRC if we fell through
        };
        return (NodeFactory.CreateNodeWithGenericModule(bypassAlgo, SecurityModuleBehaviour.BypassAll),
                NodeFactory.CreateChannel());
    }

    private static void Dispatch(Core.Ecu.EcuNode n, Core.Bus.ChannelSession c, params byte[] usdt)
        => Service27Handler.Handle(n, usdt, c, nowMs: 0);

    [Fact]
    public void Bypass_RequestSeed_EmitsZeroSeed_AndMarksUnlocked()
    {
        var (n, c) = BypassNode();

        Dispatch(n, c, 0x27, 0x01);

        Assert.Equal(new byte[] { Service.Positive(Service.SecurityAccess), 0x01, 0x00, 0x00 },
                     TestFrame.DequeueSingleFrameUsdt(c));
        Assert.Equal(new byte[] { 0x00, 0x00 }, n.State.SecurityLastIssuedSeed);
        Assert.Equal(1, n.State.SecurityUnlockedLevel);
    }

    [Fact]
    public void Bypass_SendKey_AcceptsAnyKey_IncludingZeros()
    {
        // The exact path 6Speed.T43 takes: hardcoded $27 $02 00 00.
        var (n, c) = BypassNode();

        Dispatch(n, c, 0x27, 0x01); TestFrame.DequeueSingleFrameUsdt(c);
        Dispatch(n, c, 0x27, 0x02, 0x00, 0x00);

        Assert.Equal(new byte[] { Service.Positive(Service.SecurityAccess), 0x02 },
                     TestFrame.DequeueSingleFrameUsdt(c));
        Assert.Equal(1, n.State.SecurityUnlockedLevel);
    }

    [Fact]
    public void Bypass_NoProgSessionRequired_StillShortCircuits()
    {
        // Behaviour=BypassAll is unconditional - no SecurityProgrammingShortcutActive
        // dependency. Programming-session state intentionally left false.
        var (n, c) = BypassNode();
        Assert.False(n.State.SecurityProgrammingShortcutActive);

        Dispatch(n, c, 0x27, 0x01);

        Assert.Equal(new byte[] { Service.Positive(Service.SecurityAccess), 0x01, 0x00, 0x00 },
                     TestFrame.DequeueSingleFrameUsdt(c));
    }

    [Fact]
    public void Bypass_OverridesLockout()
    {
        // Lockout left over from prior failed attempts should not block the
        // bypass path - it short-circuits before the lockout check runs.
        var (n, c) = BypassNode();
        n.State.SecurityFailedAttempts = 3;
        n.State.SecurityLockoutUntilMs = 999_999;

        Service27Handler.Handle(n, new byte[] { 0x27, 0x01 }, c, nowMs: 5_000);

        Assert.Equal(new byte[] { Service.Positive(Service.SecurityAccess), 0x01, 0x00, 0x00 },
                     TestFrame.DequeueSingleFrameUsdt(c));
    }

    [Fact]
    public void Strict_InProgSession_StillEnforcesAlgorithm()
    {
        // Strict behaviour ignores SecurityProgrammingShortcutActive entirely
        // - the prog-session flag is informational only under the new scheme.
        node.State.SecurityProgrammingShortcutActive = true;

        Dispatch(0x27, 0x01);
        Assert.Equal(new byte[] { Service.Positive(Service.SecurityAccess), 0x01, 0x12, 0x34 }, Pop());

        Dispatch(0x27, 0x02, 0x00, 0x00); // wrong key
        Assert.Equal(new byte[] { Service.NegativeResponse, Service.SecurityAccess, Nrc.InvalidKey }, Pop());
        Assert.Equal(0, node.State.SecurityUnlockedLevel);
    }
}
