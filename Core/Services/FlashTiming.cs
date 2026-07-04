using System;
using Core.Bus;
using Core.Ecu;
using Core.Utilities;

namespace Core.Services;

// Shared flash-write response pacing for every ECU. A simulator answers
// instantly, so a flash completes in well under 10 s; a real PCM takes 30 s+.
// The two per-ECU knobs (EcuNode.FlashTransferDelayMs / FlashEraseDelayMs) let
// any flash path model realistic timing by simply DEFERRING the positive
// response - the ECU is "busy" (programming a block / erasing) and answers when
// done; the tester waits in its ReadMsgs call.
//
//   - TransferData ($36): the $76 is delayed by FlashTransferDelayMs.
//     ~960 blocks * 30 ms ~= 30 s. Keep the per-block delay well under the
//     tester's $36 read timeout (~2.5 s observed) - per-block pacing is the
//     lever, not one giant delay.
//   - Erase: the positive ($F1 for Ford $B1, $71 for GM SPS $31) is delayed by
//     FlashEraseDelayMs. The tester's erase read timeout is long (real erases
//     take seconds), so a single multi-second deferral is fine.
//
// We deliberately do NOT emit $7F nn 78 RequestCorrectlyReceived-ResponsePending
// during the erase: PCMTec treats a pending response to the $B1 erase as a hard
// error and aborts (observed 2026-06-06 - it bailed on the first $7F B1 78). A
// real Ford PCM just goes quiet during the erase and answers $F1 when finished,
// which is what a plain deferred response models.
//
// Both knobs default 0 = enqueue immediately, so unset behaviour is byte-
// identical to before (GM/UDS flows and their tests are unaffected). Delayed
// enqueues run on the shared TimerScheduler (Core/Utilities/Timers), never
// blocking the dispatch thread; the channel RX queue is concurrent so the
// timer-thread enqueue is safe. Each $36 is sequential (the tester waits for the
// $76 before the next $36) so only one one-shot timer is ever live at a time.
public static class FlashTiming
{
    /// <summary>
    /// Enqueue a TransferData ($36) positive response, deferred by the node's
    /// FlashTransferDelayMs (0 = immediate).
    /// </summary>
    public static void EnqueueTransferResponse(EcuNode node, ChannelSession ch, byte[] payload)
        => EnqueueDelayed(node, ch, payload, node.FlashTransferDelayMs);

    /// <summary>
    /// Enqueue an erase positive response, deferred by the node's
    /// FlashEraseDelayMs (0 = immediate). No ResponsePending heartbeats - see
    /// the class comment for why.
    /// </summary>
    public static void EnqueueEraseResponse(EcuNode node, ChannelSession ch, byte[] positivePayload)
        => EnqueueDelayed(node, ch, positivePayload, node.FlashEraseDelayMs);

    private static void EnqueueDelayed(EcuNode node, ChannelSession ch, byte[] payload, int delayMs)
    {
        // SendNow, not EnqueueResponse: flash pacing is self-contained, so it must
        // bypass the node's generic response-pacing hook (ResponseDelayMs / 78)
        // or a flash reply would be deferred twice and could grow an unwanted 78.
        if (delayMs <= 0)
        {
            node.State.Fragmenter.SendNow(ch, node.UsdtResponseCanId, payload);
            return;
        }

        // One-shot: no AutoRestart / AutoStop -> fires once then self-stops. The
        // TimerScheduler heap holds a reference while it's pending, so the local
        // going out of scope is safe.
        var timer = new TimerOnDelay
        {
            Preset = delayMs,
            DebugTimerName = "flash-delay",
        };
        timer.OnTimingDone += (_, _) =>
        {
            try { node.State.Fragmenter.SendNow(ch, node.UsdtResponseCanId, payload); }
            catch (Exception ex)
            {
                ch.Bus?.LogSim?.Invoke($"[flash-timing] delayed response error: {ex.Message}");
            }
        };
        timer.Start();
    }
}
