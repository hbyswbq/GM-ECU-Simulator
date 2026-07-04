using Core.Bus;
using Core.Ecu;
using Core.Services;

namespace Core.Scheduler;

// Background ticker that increments every ECU's TesterPresent timer and
// fires Exit_Diagnostic_Services() when one of them passes its session
// timeout. The threshold is per-node (EcuNode.SessionTimeoutMs) so it tracks
// the ECU's active protocol stack - GMW3110 P3Cnom (5000 ms) or the UDS S3
// timer - and honours any per-ECU SessionTimeoutOverrideMs. Per GMW3110
// §6.2.4 a GM ECU must time out at >= P3Cnom (5000 ms) and <= P3Cmax
// (5100 ms); we tick at 50 ms granularity which keeps the timeout inside
// that 100 ms tolerance band.
public sealed class TesterPresentTicker : IDisposable
{
    private const int TickPeriodMs = 50;

    private readonly VirtualBus bus;
    private readonly DpidScheduler scheduler;
    private readonly System.Threading.Timer timer;
    private long lastTickMs;

    public TesterPresentTicker(VirtualBus bus, DpidScheduler scheduler)
    {
        this.bus = bus;
        this.scheduler = scheduler;
        timer = new System.Threading.Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start()
    {
        lastTickMs = 0;
        timer.Change(TickPeriodMs, TickPeriodMs);
    }

    private void Tick()
    {
        long nowMs = Environment.TickCount64;
        int delta = lastTickMs == 0 ? TickPeriodMs : (int)(nowMs - lastTickMs);
        lastTickMs = nowMs;

        bool anyTimedOut = false;
        foreach (var node in bus.Nodes)
        {
            // Atomic check-and-advance under the state's lock so a $3E reset
            // landing mid-tick is never swallowed.
            if (node.State.TesterPresent.TickAndCheckTimeout(delta, node.SessionTimeoutMs))
            {
                EcuExitLogic.Run(node, scheduler, node.State.LastEnhancedChannel);
                anyTimedOut = true;
            }
        }

        // Bin-replay stop trigger: only worth checking on ticks where at
        // least one ECU just exited - otherwise the all-idle status hasn't
        // changed since the previous tick. MaybeStop is idempotent (CAS).
        if (anyTimedOut && bus.Replay != null)
        {
            bool allIdle = true;
            foreach (var node in bus.Nodes)
            {
                if (node.State.TesterPresent.State == TesterPresentTimerState.Active)
                {
                    allIdle = false;
                    break;
                }
            }
            if (allIdle) bus.Replay.MaybeStop(bus.NowMs);
        }
    }

    public void Dispose() => timer.Dispose();
}
