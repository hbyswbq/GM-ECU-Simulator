using Common.Protocol;
using Core.Bus;
using Core.Ecu;
using Core.Scheduler;
using Core.Services;
using Core.Services.Uds;

namespace Core.Protocol;

// The SPS-kernel dispatch: the UDS-flavoured service table a GM SPS programming
// kernel presents once it has been boot-loaded via $36 sub $80 DownloadAndExecute.
// Reached as the UDS-Kernel stack's dispatch implementation; the kernel binding is
// pushed by Service36Handler (EnterKernelMode) when the DownloadAndExecute lands and
// torn down by EcuExitLogic (ExitKernelMode) on $20 or P3C timeout.
//
// Scope is deliberately narrow - real kernels only answer a handful of
// services. Anything not listed here falls through to NRC $11
// ServiceNotSupported via the dispatch's default-false return, which matches
// what powerpcm_flasher and similar tools see on real hardware.
public sealed class UdsKernelDispatch
{
    public static readonly UdsKernelDispatch Instance = new();
    private UdsKernelDispatch() { }

    public bool Dispatch(EcuNode node, ReadOnlySpan<byte> usdt, ChannelSession ch,
                        bool isFunctional, byte sid, double nowMs, DpidScheduler scheduler,
                        DiagnosticStack stack)
    {
        _ = stack;  // kernel dispatch only runs after $36 sub $80; stack is
                    // whatever CAN ID the host used to hand control over.

        switch (sid)
        {
            case Iso14229.Service.RoutineControl:
                if (isFunctional) return true;
                if (Service31Handler.Handle(node, usdt, ch))
                    DispatchShared.ActivateP3C(node, ch);
                return true;
            case Service.TesterPresent:
                // ISO 14229 $3E is byte-identical to GMW3110 $3E. The kernel
                // still needs P3C keepalive, so reuse the shared handler.
                Service3EHandler.Handle(node, usdt, ch, isFunctional);
                return true;
            case Service.ReturnToNormalMode:
                // $20 is the documented way for the tester to ask the kernel
                // to hand control back to the boot ROM. EcuExitLogic restores
                // the baseline stacks (ExitKernelMode) as part of its cleanup.
                if (isFunctional) { EcuExitLogic.Run(node, scheduler, null); return true; }
                Service20Handler.Handle(node, usdt, ch, scheduler);
                return true;
            case Service.RequestDownload:
                // Some kernels accept a second $34/$36 pair to layer in
                // calibration after the OS upload. Forward to the same
                // handler the GMW3110 dispatch uses - the wire shape is
                // compatible for the cases SPS kernels send.
                if (isFunctional) return true;
                if (Service34Handler.Handle(node, usdt, ch))
                    DispatchShared.ActivateP3C(node, ch);
                return true;
            case Service.RequestUpload:
                // $35 flash-READ. Both reader tools issue $35 from within kernel
                // mode (they DownloadAndExecute a helper kernel first): the T43
                // read-kernel answers each $35 with a multi-frame block, while the
                // E38/E67 native path arms an upload that the following $36s drain.
                // Service35Handler branches on ReadFamily / T43ReadKernelActive.
                if (isFunctional) return true;
                if (Service35Handler.Handle(node, usdt, ch))
                    DispatchShared.ActivateP3C(node, ch);
                return true;
            case Service.TransferData:
                if (isFunctional) return true;
                // A PcmHammer/PCMHacking write kernel repurposes $36 as its own
                // self-contained write-block (ct/len/addr/data/sum16), NOT the
                // boot-ROM TransferData form Service36Handler decodes. The flavour
                // was fixed at the $36 sub $80 handover, so it can't change
                // mid-session - branch on it here, same as the read path branches
                // on T43ReadKernelActive.
                if (node.State.KernelIsPcmHammer)
                {
                    if (PcmHammerKernel.HandleWrite(node, usdt, ch))
                        DispatchShared.ActivateP3C(node, ch);
                    return true;
                }
                if (Service36Handler.Handle(node, usdt, ch))
                    DispatchShared.ActivateP3C(node, ch);
                return true;
            case PcmHammerKernel.KernelFlashQuery:
                // Mode $3D is the PcmHammer kernel's query/erase channel (probe /
                // CRC-32 / sector erase). Only that kernel answers it; a generic
                // SPS/read kernel falls through to NRC $11 exactly as before.
                if (!node.State.KernelIsPcmHammer) return false;
                if (isFunctional) return true;
                if (PcmHammerKernel.HandleQuery(node, usdt, ch))
                    DispatchShared.ActivateP3C(node, ch);
                return true;
            default:
                return false;
        }
    }
}
