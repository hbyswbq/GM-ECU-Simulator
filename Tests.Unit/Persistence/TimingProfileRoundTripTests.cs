using Common.Persistence;
using Core.Persistence;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.Persistence;

// Guards the per-ECU response-timing knobs through ConfigStore's node<->dto
// round-trip so a saved profile survives a save/load cycle, and an older config
// (fields absent) loads with spec-correct defaults.
public sealed class TimingProfileRoundTripTests
{
    [Fact]
    public void TimingKnobs_RoundTripThroughConfigStore()
    {
        var node = NodeFactory.CreateNode();
        node.ResponseDelayMs = 250;
        node.Emit78WhenSlow = false;
        node.SessionTimeoutOverrideMs = 300;

        var dto = ConfigStore.EcuDtoFrom(node);
        Assert.Equal(250, dto.ResponseDelayMs);
        Assert.Equal(false, dto.Emit78WhenSlow);          // written only because it was turned off
        Assert.Equal(300, dto.SessionTimeoutOverrideMs);

        var back = ConfigStore.EcuNodeFrom(dto);
        Assert.Equal(250, back.ResponseDelayMs);
        Assert.False(back.Emit78WhenSlow);
        Assert.Equal(300, back.SessionTimeoutOverrideMs);
    }

    [Fact]
    public void Defaults_AreInstantAndSpecCorrect()
    {
        var node = NodeFactory.CreateNode();
        Assert.Equal(0, node.ResponseDelayMs);
        Assert.True(node.Emit78WhenSlow);
        Assert.Null(node.SessionTimeoutOverrideMs);

        var dto = ConfigStore.EcuDtoFrom(node);
        // Default true is the absent state - kept null so standard configs stay quiet.
        Assert.Null(dto.Emit78WhenSlow);
        Assert.Null(dto.SessionTimeoutOverrideMs);

        var back = ConfigStore.EcuNodeFrom(dto);
        Assert.Equal(0, back.ResponseDelayMs);
        Assert.True(back.Emit78WhenSlow);                 // absent -> true (spec-correct pending)
        Assert.Null(back.SessionTimeoutOverrideMs);
    }

    [Fact]
    public void DefaultEcu_JsonOmitsTimingKnobs_AndReloadsDefaults()
    {
        var cfg = new SimulatorConfig { Ecus = { ConfigStore.EcuDtoFrom(NodeFactory.CreateNode()) } };
        string json = ConfigSerializer.Serialize(cfg);

        // WhenWritingNull keeps a default ECU quiet in the file (no churn).
        Assert.DoesNotContain("responseDelayMs", json);
        Assert.DoesNotContain("emit78WhenSlow", json);
        Assert.DoesNotContain("sessionTimeoutOverrideMs", json);

        // An old file with the fields absent loads to the spec-correct defaults.
        var back = ConfigStore.EcuNodeFrom(ConfigSerializer.Deserialize(json).Ecus[0]);
        Assert.Equal(0, back.ResponseDelayMs);
        Assert.True(back.Emit78WhenSlow);
        Assert.Null(back.SessionTimeoutOverrideMs);
    }

    [Fact]
    public void SetTimingKnobs_SurviveJsonRoundTrip()
    {
        var node = NodeFactory.CreateNode();
        node.ResponseDelayMs = 250;
        node.Emit78WhenSlow = false;
        node.SessionTimeoutOverrideMs = 300;

        var cfg = new SimulatorConfig { Ecus = { ConfigStore.EcuDtoFrom(node) } };
        string json = ConfigSerializer.Serialize(cfg);
        Assert.Contains("responseDelayMs", json);
        Assert.Contains("emit78WhenSlow", json);
        Assert.Contains("sessionTimeoutOverrideMs", json);

        var back = ConfigStore.EcuNodeFrom(ConfigSerializer.Deserialize(json).Ecus[0]);
        Assert.Equal(250, back.ResponseDelayMs);
        Assert.False(back.Emit78WhenSlow);
        Assert.Equal(300, back.SessionTimeoutOverrideMs);
    }
}
