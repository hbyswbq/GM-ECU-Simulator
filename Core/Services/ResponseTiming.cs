using System;
using System.Collections.Generic;
using Common.Protocol;
using Core.Bus;
using Core.Protocol;
using Core.Utilities;

namespace Core.Services;

// Application-layer response pacing: models the time an ECU takes to produce a
// diagnostic response and honours the active stack's TimingProfile P2 / P2*
// (GMW3110 section 6.2 P2CE / P2CE*; ISO 14229 P2_server / P2*). A response the
// ECU can produce within its P2 budget simply arrives after the modelled
// processing time. A response that takes LONGER than P2 must not let the
// tester's (larger) deadline lapse: the ECU emits 7F <sid> 78
// RequestCorrectlyReceived-ResponsePending (RCR-RP) BEFORE the budget, which
// resets the tester to the extended P2* deadline, and repeats a 78 every P2*
// until the real response is ready.
//
// This is the generic-handler analogue of FlashTiming (which paces the specific
// $36 / $B1 flash responses and deliberately suppresses 78 because PCMTec aborts
// on a pending reply to the $B1 erase). The two never stack: FlashTiming sends
// via IsoTpFragmenter.SendNow, which bypasses pacing, so a flash response is
// deferred exactly once by its own knob.
//
// The pacer never blocks the dispatch thread: it chains one-shot TimerOnDelay
// instances on the shared TimerScheduler (Core/Utilities/Timers), mirroring
// FlashTiming. Each step arms the next, so only one timer is live at a time.
// A liveness gate (isLive) lets the owning IsoTpFragmenter cancel a pending
// chain when its target channel is torn down, so a deferred response never fires
// onto a removed channel (the same hazard IsoTpFragmenter.AbortIfActiveOn guards
// for an in-flight ISO-TP send).
public sealed class ResponsePacing
{
    /// <summary>Modelled time (ms) for the ECU to produce this response. &lt;= 0 means send now.</summary>
    public required int ProcessingMs { get; init; }

    /// <summary>ECU-side P2 / P2* budget for this response (the resolved stack's profile).</summary>
    public required TimingProfile Timing { get; init; }

    /// <summary>Emit 7F sid 78 RCR-RP heartbeats when ProcessingMs exceeds P2. Default true (spec-correct).</summary>
    public bool Emit78 { get; init; } = true;

    /// <summary>Optional diagnostic sink (the bus LogSim).</summary>
    public Action<string>? Log { get; init; }
}

public static class ResponseTiming
{
    // Fraction of a budget at which a heartbeat is scheduled so it reaches the
    // tester comfortably ahead of the deadline even under timer jitter: the
    // first 78 fires at 0.75*P2 (before the ECU budget AND well before the larger
    // tester timeout), and each subsequent 78 at 0.75*P2* (a quarter-window
    // margin before the extended deadline lapses).
    private const double Headroom = 0.75;

    // GMW3110 MSSC: $20 ReturnToNormalMode, $28 DisableNormalCommunication and
    // $A5 ProgrammingMode must NEVER be answered with a 7F sid 78 RCR-RP. A slow
    // response to one of these is deferred whole (it just goes quiet) instead.
    private static readonly HashSet<byte> No78Sids = new() { 0x20, 0x28, 0xA5 };

    /// <summary>
    /// Pace one diagnostic response. <paramref name="sendNow"/> performs the raw
    /// (immediate) ISO-TP send - the fragmenter's SendNow. <paramref name="payload"/>
    /// is the final USDT bytes (a positive response or a terminal NRC); the
    /// request SID for any 78 frame is derived from it (positive = byte0 - 0x40;
    /// NRC = byte1). <paramref name="isLive"/>, when supplied, is checked before
    /// every step: a false return abandons the rest of the chain (the channel was
    /// torn down or a newer response superseded this one).
    /// </summary>
    public static void Pace(ResponsePacing pacing, ChannelSession ch, uint canId, byte[] payload,
                            Action<ChannelSession, uint, byte[]> sendNow, Func<bool>? isLive = null)
    {
        int proc = pacing.ProcessingMs;
        if (proc <= 0 || payload.Length == 0)
        {
            sendNow(ch, canId, payload);
            return;
        }

        int p2 = Math.Max(1, pacing.Timing.P2Ms);

        // Request SID behind this response: positive = byte0 - 0x40; NRC (7F) = byte1.
        // A degenerate <2-byte NRC can't form a 78, so fall back to deferring whole.
        bool isNrc = payload[0] == Service.NegativeResponse;
        bool canDeriveSid = !isNrc || payload.Length >= 2;
        byte reqSid = isNrc ? (payload.Length >= 2 ? payload[1] : (byte)0) : (byte)(payload[0] - 0x40);

        // Fast enough, 78 suppressed, a 78-forbidden SID ($20/$28/$A5), or a
        // payload we can't derive a SID from: defer the whole response by the
        // modelled processing time (the ECU "goes quiet then answers when done").
        if (proc <= p2 || !pacing.Emit78 || !canDeriveSid || No78Sids.Contains(reqSid))
        {
            Chain(new List<Step> { new(proc, () => sendNow(ch, canId, payload)) }, isLive, pacing.Log);
            return;
        }

        // Slow path: first 78 before the P2 budget, more 78s every ~P2* until the
        // real response lands.
        byte[] pending = { Service.NegativeResponse, reqSid, Nrc.RequestCorrectlyReceivedResponsePending };

        int firstPending = Math.Max(1, (int)(p2 * Headroom));
        int step = Math.Max(1, (int)(Math.Max(1, pacing.Timing.P2StarMs) * Headroom));
        var steps = new List<Step>();
        int t = firstPending;
        steps.Add(new(firstPending, () => sendNow(ch, canId, pending)));
        while (t + step < proc)
        {
            steps.Add(new(step, () => sendNow(ch, canId, pending)));
            t += step;
        }
        steps.Add(new(Math.Max(1, proc - t), () => sendNow(ch, canId, payload)));
        Chain(steps, isLive, pacing.Log);
    }

    private readonly record struct Step(int DelayMs, Action Action);

    // Execute steps sequentially: arm a one-shot for steps[i].DelayMs; on fire,
    // run its action then arm steps[i+1]. Runs off the dispatch thread on the
    // shared TimerScheduler. isLive (when supplied) gates every step so a cancelled
    // chain stops without sending; a throwing step is logged and does not stall
    // the chain (the next step still arms).
    private static void Chain(IReadOnlyList<Step> steps, Func<bool>? isLive, Action<string>? log)
    {
        void Arm(int i)
        {
            if (i >= steps.Count) return;
            if (isLive is not null && !isLive()) return;
            var timer = new TimerOnDelay
            {
                Preset = Math.Max(1, steps[i].DelayMs),
                DebugInstanceName = "ResponseTiming",
                DebugTimerName = "p2-pace",
            };
            timer.OnTimingDone += (_, _) =>
            {
                if (isLive is not null && !isLive()) return;
                try { steps[i].Action(); }
                catch (Exception ex) { log?.Invoke($"[response-timing] step {i} error: {ex.Message}"); }
                Arm(i + 1);
            };
            timer.Start();
        }
        Arm(0);
    }
}
