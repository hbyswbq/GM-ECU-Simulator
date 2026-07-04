using Common.Replay;
using Core.Replay;
using Xunit;

namespace EcuSimulator.Tests.Replay;

// Covers BinReplayCoordinator's playback state machine end-to-end through the
// IBinSource seam (the abstraction IBinSource.cs documents as existing "so the
// coordinator can be unit-tested with an in-memory FakeBinSource"). The
// coordinator is on the live dispatch/scheduler hot path - VirtualBus calls
// MaybeStart on every $22/$AA and TesterPresentTicker calls MaybeStop - and its
// lock-free CAS/latch logic had no regression net before this file.
//
// Value-to-row convention: FakeBinSource via Linear() stores channel-0 value
// 100 + row, so (Sample - 100) reads back the resolved row index.
public sealed class BinReplayCoordinatorTests
{
    // Minimal in-memory IBinSource. Row r has elapsed r*stepMs and channel-0
    // value 100 + r, so tests can map a sampled value straight back to a row.
    private sealed class FakeBinSource : IBinSource
    {
        private readonly long[] elapsed;
        private readonly float[][] cols;

        public FakeBinSource(long[] elapsedMs, float[][] columns)
        {
            elapsed = elapsedMs;
            cols = columns;
        }

        public int RowCount => elapsed.Length;
        public int ChannelCount => cols.Length;
        public long GetElapsedMs(int row) => elapsed[row];
        public float GetValue(int channelIndex, int row) => cols[channelIndex][row];
        public IReadOnlyList<BinChannelHeader> ChannelHeaders { get; } = Array.Empty<BinChannelHeader>();

        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    private static FakeBinSource Linear(int rows, long stepMs = 100)
    {
        var elapsed = new long[rows];
        var col = new float[rows];
        for (int i = 0; i < rows; i++) { elapsed[i] = i * stepMs; col[i] = 100 + i; }
        return new FakeBinSource(elapsed, new[] { col });
    }

    // ---- State machine: NoBin / Armed / Running / Stopped, plus re-arm ----

    [Fact]
    public void NoSource_is_NoBin_and_samples_zero()
    {
        var c = new BinReplayCoordinator();
        Assert.Equal(BinReplayState.NoBin, c.State);
        Assert.Equal(0.0, c.Sample(0, 0));
    }

    [Fact]
    public void Load_arms_and_Armed_shows_row0()
    {
        var c = new BinReplayCoordinator();
        c.Load(Linear(10));
        Assert.Equal(BinReplayState.Armed, c.State);
        // Armed: any time before the first MaybeStart resolves to row 0.
        Assert.Equal(100.0, c.Sample(0, 999_999), 3);
    }

    [Fact]
    public void Start_runs_and_advances_with_bus_time()
    {
        var c = new BinReplayCoordinator();
        c.Load(Linear(10));        // rows 0..9, elapsed 0..900
        c.MaybeStart(1000);
        Assert.Equal(BinReplayState.Running, c.State);
        Assert.Equal(100.0, c.Sample(0, 1000), 3);   // replayMs 0   -> row 0
        Assert.Equal(103.0, c.Sample(0, 1300), 3);   // replayMs 300 -> row 3
    }

    [Fact]
    public void Stop_freezes_at_the_stop_offset()
    {
        var c = new BinReplayCoordinator();
        c.Load(Linear(10));
        c.MaybeStart(1000);
        c.MaybeStop(1500);
        Assert.Equal(BinReplayState.Stopped, c.State);
        // Frozen at replayMs 500 (row 5) regardless of how far the bus clock runs.
        Assert.Equal(105.0, c.Sample(0, 9999), 3);
    }

    [Fact]
    public void Restart_from_Stopped_clears_latches_and_replays_from_zero()
    {
        var c = new BinReplayCoordinator();
        c.Load(Linear(10));
        c.MaybeStart(1000);
        c.MaybeStop(1500);
        Assert.Equal(BinReplayState.Stopped, c.State);

        // A fresh request re-arms: stop latch cleared, clock restarts at this time.
        c.MaybeStart(2000);
        Assert.Equal(BinReplayState.Running, c.State);
        Assert.Equal(100.0, c.Sample(0, 2000), 3);   // back to row 0
        Assert.Equal(102.0, c.Sample(0, 2200), 3);   // replayMs 200 -> row 2
    }

    [Fact]
    public void MaybeStop_before_start_is_ignored()
    {
        var c = new BinReplayCoordinator();
        c.Load(Linear(5));
        c.MaybeStop(500);                            // not started yet -> no-op
        Assert.Equal(BinReplayState.Armed, c.State);
    }

    [Fact]
    public void Unload_returns_to_NoBin()
    {
        var c = new BinReplayCoordinator();
        c.Load(Linear(5));
        c.Unload();
        Assert.Equal(BinReplayState.NoBin, c.State);
        Assert.Equal(0, c.RowCount);
    }

    [Fact]
    public void StateChanged_fires_once_per_real_transition()
    {
        var c = new BinReplayCoordinator();
        int events = 0;
        c.StateChanged += _ => events++;

        c.Load(Linear(5));      // Armed   -> +1
        c.MaybeStart(0);        // Running -> +1
        c.MaybeStart(0);        // already running, idempotent -> no event
        c.MaybeStop(100);       // Stopped -> +1
        c.MaybeStop(200);       // already stopped, idempotent -> no event

        Assert.Equal(3, events);
    }

    // ---- Loop modes past the end of the recorded duration ----

    [Fact]
    public void HoldLast_pins_the_last_row_past_the_end()
    {
        var c = new BinReplayCoordinator { LoopMode = BinReplayLoopMode.HoldLast };
        c.Load(Linear(10));     // lastRowMs 900
        c.MaybeStart(0);
        Assert.Equal(109.0, c.Sample(0, 5000), 3);   // replayMs 5000 -> hold row 9
        Assert.Equal(BinReplayState.Running, c.State); // HoldLast keeps running
    }

    [Fact]
    public void Loop_wraps_modulo_the_duration()
    {
        var c = new BinReplayCoordinator { LoopMode = BinReplayLoopMode.Loop };
        c.Load(Linear(10));     // lastRowMs 900
        c.MaybeStart(0);
        Assert.Equal(101.0, c.Sample(0, 1000), 3);   // 1000 % 900 = 100 -> row 1
    }

    [Fact]
    public void Stop_mode_auto_transitions_to_Stopped_and_freezes_at_last_row()
    {
        var c = new BinReplayCoordinator { LoopMode = BinReplayLoopMode.Stop };
        c.Load(Linear(10));     // lastRowMs 900
        c.MaybeStart(0);
        Assert.Equal(BinReplayState.Running, c.State);

        // First sample past the end latches Stopped and freezes at the last row.
        Assert.Equal(109.0, c.Sample(0, 5000), 3);
        Assert.Equal(BinReplayState.Stopped, c.State);
        Assert.Equal(109.0, c.Sample(0, 99_999), 3); // stays frozen
    }

    // ---- Host-disconnect (NaN) freeze latch ----

    [Fact]
    public void NaN_stop_latches_freeze_offset_at_first_sample_after_stop()
    {
        var c = new BinReplayCoordinator();
        c.Load(Linear(10));
        c.MaybeStart(0);
        _ = c.Sample(0, 350);                        // mid-stream

        c.MaybeStop(double.NaN);                     // host disconnect: stopAt = long.MaxValue
        Assert.Equal(BinReplayState.Stopped, c.State);

        // The first sample after a NaN-stop freezes at THAT sample's bus time...
        Assert.Equal(105.0, c.Sample(0, 500), 3);    // freeze at replayMs 500 -> row 5
        // ...and every later sample stays pinned there.
        Assert.Equal(105.0, c.Sample(0, 9999), 3);
    }

    // ---- Sample bounds: channel index and empty source ----

    [Fact]
    public void Out_of_range_channel_returns_zero()
    {
        var c = new BinReplayCoordinator();
        c.Load(Linear(5));
        c.MaybeStart(0);
        Assert.Equal(0.0, c.Sample(99, 0));          // above ChannelCount
        Assert.Equal(0.0, c.Sample(-1, 0));          // negative (cast to uint guards both)
    }

    [Fact]
    public void Empty_source_samples_zero()
    {
        var c = new BinReplayCoordinator();
        c.Load(new FakeBinSource(Array.Empty<long>(), new[] { Array.Empty<float>() }));
        c.MaybeStart(0);
        Assert.Equal(0.0, c.Sample(0, 0));           // RowCount 0 -> 0
    }

    // ---- FindRowAtOrBefore binary-search boundaries ----

    [Theory]
    [InlineData(-50, 0)]      // before row 0 -> clamp to 0
    [InlineData(0, 0)]        // exact row 0
    [InlineData(150, 1)]      // between rows -> the one at-or-before
    [InlineData(200, 2)]      // exact interior row
    [InlineData(399, 3)]      // just before a row boundary
    [InlineData(99_999, 4)]   // past the last row -> last row
    public void FindRowAtOrBefore_resolves_boundaries(long targetMs, int expectedRow)
    {
        var src = Linear(5);  // elapsed 0,100,200,300,400
        Assert.Equal(expectedRow, BinReplayCoordinator.FindRowAtOrBefore(src, targetMs));
    }
}
