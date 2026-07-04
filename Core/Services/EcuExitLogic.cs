using Common.Protocol;
using Core.Bus;
using Core.Ecu;
using Core.Scheduler;

namespace Core.Services;

// Implements GMW3110 §8.5.6.2 Exit_Diagnostic_Services(). Called by:
//   - $20 ReturnToNormalMode (response is requested by tester)
//   - P3C TesterPresent timeout (response is unsolicited)
//
// The node clears all enhanced state but RETAINS dynamic DPID definitions
// per the spec ("the node shall retain all dynamically defined message
// (DPID) information"). $2D-defined dynamic PIDs are cleared since the spec
// doesn't list them as retained.
public static class EcuExitLogic
{
    // The ChannelSession argument may be null when the caller is a P3C
    // timeout - in that case the unsolicited $60 needs to land on whichever
    // channel(s) that ECU was previously talking to. We store the last
    // channel a periodic was scheduled on; if there was no enhanced traffic
    // there's no $20 response to send.
    public static void Run(EcuNode node, DpidScheduler scheduler, ChannelSession? respondOn)
    {
        // Capture before ClearProgrammingState wipes it. Per GMW3110 §8.5.6.2
        // pseudo-code, the $60 positive response is only sent on the
        // `programming_mode_active = NO` branch. §8.5 paragraph "When using
        // this service to end a programming session, ... A valid request for
        // this service which concludes a programming event shall not be
        // followed by a positive response." And §8.5.1 "An ECU shall send an
        // unsolicited service $20 positive response message any time a
        // TesterPresent ($3E) timeout (P3C) occurs and a programming session
        // is not active." Both rules share the same gate.
        bool wasProgrammingActive = node.State.ProgrammingModeActive;

        // 1. Reset P3C state.
        node.State.TesterPresent.Deactivate();

        // 2. Reset DPID scheduler for this node (clears Slow/Med/Fast entries).
        scheduler.Stop(node, Array.Empty<byte>());

        // 3. Clear dynamic PIDs from $2D. Static PIDs (the user-configured ones)
        //    stay. We track the dynamic set on the node so this is a clean diff.
        lock (node.State.DynamicallyDefinedPids)
        {
            foreach (var id in node.State.DynamicallyDefinedPids)
                node.RemovePidByAddress(id);
            node.State.DynamicallyDefinedPids.Clear();
        }

        // 3a. Bootloader capture: per-$36 fragments are already on disk
        //     (BootloaderCaptureWriter.WriteEachTransferData fires inline
        //     from Service36Handler). But any flash regions the kernel
        //     declared via $31 EraseMemoryByAddress still need to be
        //     flushed - their $FF-backed buffers got $36 writes mirrored
        //     in but haven't been dumped yet. Functional $20 passes
        //     respondOn=null; fall back to LastEnhancedChannel for the
        //     bus handle. Skip when no channel is reachable (unit-test
        //     paths construct ECUs with no bus attached at all).
        var captureBus = respondOn?.Bus ?? node.State.LastEnhancedChannel?.Bus;
        if (captureBus is not null)
        {
            // Session-end bracket close: a kernel that was pushed but never
            // followed by another $34 (e.g. cal-only flow ended via $20, or
            // P3C timed out mid-transfer) still gets a tagged dump here.
            BootloaderCaptureWriter.WriteCompletedBracketIfKernel(node, captureBus, "end");
            BootloaderCaptureWriter.WriteFlashRegions(node, captureBus);
        }

        // 3b. Clear $28 / $A5 / $34 / $36 programming-session state. Per
        //     GMW3110 §8.17 "The tester can end a programming event by sending
        //     a ReturnToNormalMode ($20) request message, or by allowing a P3C
        //     timeout to occur." Both paths funnel through here.
        node.State.ClearProgrammingState();

        // 3b-2. Re-lock SecurityAccess. GMW3110 §8.5.6.2 Exit_Diagnostic_Services()
        //       re-locks on both branches of $20 / P3C timeout: programming_mode_active
        //       = NO sets "Security_Access_Unlocked  FALSE", and = YES performs a
        //       software reset that re-locks via power-on. A fresh $27 handshake is
        //       therefore required before any security-gated service ($34/$31/$3B) is
        //       accepted again - without this the ECU would wrongly stay unlocked
        //       across a session exit, which a real tester would never observe.
        node.State.ResetSecurity();

        // 3c. Restore the baseline stacks if a runtime SPS-kernel handover is active. After a
        //     $36 sub $80 DownloadAndExecute the ECU's stacks were replaced by the transient
        //     kernel binding; $20 / P3C timeout is the documented "kernel hands control back to
        //     the boot ROM" point, so the ECU answers as its baseline (GMW3110 / Ford) module
        //     again from here on. ExitKernelMode is a no-op when no kernel handover is active, so
        //     a configured ECU (e.g. ford-uds, loaded from config - which routes ResetEcuState /
        //     the transport flip through here too) keeps its stacks untouched.
        node.ExitKernelMode();

        // 3d. Normal communication resumes. ClearProgrammingState above reset NormalCommunicationDisabled, so a
        //     $28 that had silenced this node's autonomous CAN broadcast is now lifted - rebuild the emitter to
        //     bring the broadcasts back. RebuildIfRunning re-evaluates every node and is a no-op when no host
        //     session is emitting, so this covers all three callers ($20, the P3C timeout, and Reset ECU State)
        //     without resurrecting traffic on an idle bus.
        captureBus?.BroadcastScheduler.RebuildIfRunning();

        // 4. Send $60 positive response only when the spec demands it: caller
        //    provided a channel AND a programming session was NOT being torn
        //    down. Concluding a programming event is silent on the wire.
        if (respondOn != null && !wasProgrammingActive)
        {
            // SendNow, not EnqueueResponse: this $60 is frequently an ECU-initiated
            // teardown notification (P3C/S3 timeout), not a tester-timed response,
            // and it fires from the ticker thread where the node's sticky response
            // pacing must not apply - a deferred or 7F 20 78-prefixed teardown is
            // wire-incorrect. Bypassing pacing here mirrors FlashTiming's stance.
            node.State.Fragmenter.SendNow(respondOn, node.UsdtResponseCanId,
                [Service.Positive(Service.ReturnToNormalMode)]);
        }
    }
}
