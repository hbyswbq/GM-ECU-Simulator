using Common.Protocol;
using Core.Bus;
using Core.Ecu;
using Core.Scheduler;
using Core.Services;

namespace Core.Protocol;

// GMW3110-2010 dispatch switch - the GMW3110 protocol stack's dispatch implementation, reached
// via ProtocolStacks.DispatchGmw3110, and the single home of the GM SID -> handler mapping. It is
// no longer a "persona" (it implements no interface and is never swapped onto a node); the
// SPS-kernel handover is a transient stack binding, not a swap of this class.
//
// $01 is NOT here: it is a J1979 service, owned by the J1979 stack (ProtocolStacks
// .DispatchJ1979 -> Service01Handler). The OBD-vs-enhanced-dispatcher split that
// the old RequireUdsStack gate emulated is now STRUCTURAL (migration step 3): a GM
// node on a non-OBD CAN id binds only the restricted 9-SID enhanced set
// ($1A/$20/$27/$28/$34/$36/$3E/$A2/$A5), so the OBD-only SIDs ($10/$22/$2C/$2D/
// $3B/$AA/$AE) are simply not owned there and Resolve NRC-$11s them - no per-handler
// gate needed. See ProtocolStacks.SynthesizeFor and DESIGN doc section 5.
//
// What is INTENTIONALLY missing from this table:
//   - $31 RoutineControl. Not a GMW3110 service. Lives in UdsKernelDispatch,
//     active only after $36 sub $80 DownloadAndExecute hands the bus to the
//     SPS kernel.
public sealed class Gmw3110Dispatch
{
    public static readonly Gmw3110Dispatch Instance = new();
    private Gmw3110Dispatch() { }

    public bool Dispatch(EcuNode node, ReadOnlySpan<byte> usdt, ChannelSession ch,
                        bool isFunctional, byte sid, double nowMs, DpidScheduler scheduler,
                        DiagnosticStack stack)
    {
        switch (sid)
        {
            case Service.ReadDataByIdentifier:
                // §8.3 + DPS PM p.241: $1A $B0 functional is the canonical
                // "who's on the bus" probe; each ECU answers physically with
                // "5A B0 <diag_addr>". Dispatch both addressing modes - the
                // handler suppresses NRCs on functional for non-$B0 DIDs to
                // avoid bus storms (mirrors $A2 / $A5 policy).
                Service1AHandler.Handle(node, usdt, ch, isFunctional);
                return true;
            case Service.ReadDataByParameterIdentifier:
                // §8.6 explicitly supports functional addressing (Tables 87/89):
                // each ECU responds with the PIDs it supports; ECUs that support
                // none stay silent. The handler enforces the silent-on-functional
                // rule when no PIDs match.
                Service22Handler.Handle(node, usdt, ch, nowMs, isFunctional);
                return true;
            case Service.DefinePidByAddress:
                if (isFunctional) return true;
                if (Service2DHandler.Handle(node, usdt, ch))
                    DispatchShared.ActivateP3C(node, ch);
                return true;
            case Service.DynamicallyDefineMessage:
                if (isFunctional) return true;
                if (Service2CHandler.Handle(node, usdt, ch))
                    DispatchShared.ActivateP3C(node, ch);
                return true;
            case Service.ReadDataByPacketIdentifier:
                if (isFunctional) return true;
                if (ServiceAAHandler.Handle(node, usdt, ch, scheduler))
                    DispatchShared.ActivateP3C(node, ch);
                return true;
            case Service.TesterPresent:
                Service3EHandler.Handle(node, usdt, ch, isFunctional);
                return true;
            case Service.ReturnToNormalMode:
                if (isFunctional) { EcuExitLogic.Run(node, scheduler, null); return true; }
                Service20Handler.Handle(node, usdt, ch, scheduler);
                return true;
            case Service.InitiateDiagnosticOperation:
                // §8.2.5.1 (p. 79): the canonical disableAllDTCs flow is a
                // functional broadcast on $101/$FE with every responding node
                // sending $50 on its USDT response ID. 6Speed.T43 kernelprep()
                // relies on this and prints "101 10 02 command failed" when the
                // functional reply doesn't arrive within 100 ms.
                if (Service10Handler.Handle(node, usdt, ch))
                    DispatchShared.ActivateP3C(node, ch);
                return true;
            case Service.SecurityAccess:
                if (isFunctional) return true;
                if (Service27Handler.Handle(node, usdt, ch, (long)nowMs))
                    DispatchShared.ActivateP3C(node, ch);
                return true;
            case Service.DisableNormalCommunication:
                // §8.9: typically functional broadcast at $101 / $FE; both
                // physical and functional are accepted per §8.9.5.1.
                if (Service28Handler.Handle(node, usdt, ch, isFunctional))
                    DispatchShared.ActivateP3C(node, ch);
                return true;
            case Service.ReportProgrammedState:
                // §8.16: $A2 functional is the spec mechanism for enumerating
                // programmable ECUs on the bus. GM SPS / DPS broadcasts $A2 on
                // $101/$FE and counts each $E2 reply to populate its mapping
                // matrix. Dispatch the handler for both physical and functional.
                if (ServiceA2Handler.Handle(node, usdt, ch, isFunctional))
                    DispatchShared.ActivateP3C(node, ch);
                return true;
            case Service.ProgrammingMode:
                // §8.17.5.1 Table 169: $A5 $01/$02/$03 are sent on functional
                // $101/$FE; each programmable node responds on its physical
                // response ID. Dispatch in both addressing modes (parity with
                // $A2 enumeration above). The DPS PM page 241 wire trace
                // matches this exactly.
                if (ServiceA5Handler.Handle(node, usdt, ch, isFunctional))
                    DispatchShared.ActivateP3C(node, ch);
                return true;
            case Service.RequestDownload:
                if (isFunctional) return true;
                if (Service34Handler.Handle(node, usdt, ch))
                    DispatchShared.ActivateP3C(node, ch);
                return true;
            // NOTE: $35 RequestUpload (flash-READ) is intentionally absent from the
            // base table - it is not a real GMW3110 service. Both GM reader tools
            // issue it only after boot-loading a kernel, so it is owned by
            // UdsKernelDispatch (see Service35Handler); here it falls to NRC $11.
            case Service.TransferData:
                if (isFunctional) return true;
                if (Service36Handler.Handle(node, usdt, ch))
                    DispatchShared.ActivateP3C(node, ch);
                return true;
            case Service.RequestDeviceControl:
                // §8.21: $AE is point-to-point only (every spec example uses
                // a physical request ID). Handler stays silent on functional.
                if (ServiceAEHandler.Handle(node, usdt, ch, isFunctional))
                    DispatchShared.ActivateP3C(node, ch);
                return true;
            case Service.WriteDataByIdentifier:
                // §8.14 - point-to-point only in the worked example (§8.14.5.1
                // uses physical request $241). A functional broadcast write to
                // 17-byte VIN doesn't make sense (every ECU would write the same
                // VIN to its own slot), so we silently drop functional.
                if (isFunctional) return true;
                if (Service3BHandler.Handle(node, usdt, ch))
                    DispatchShared.ActivateP3C(node, ch);
                return true;
            default:
                return false;
        }
    }
}
