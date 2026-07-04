using Common.Protocol;
using Core.Bus;
using Core.Ecu;

namespace Core.Services;

// $AE RequestDeviceControl. GMW3110-2010 §8.21.
//
// Request: SID $AE + CPID (1 byte) + optional controlOptionRecord.
// Positive response: $EE + CPID. We do not echo the controlOptionRecord -
// real ECUs choose whether to include a controlReturnRecord per CPID, and
// the bytecode flows we drive (DPS 4.52) only test the SID echo + CPID, not
// the trailing bytes.
//
// CPID semantics observed in real archives:
//   $28 03  Close programming event (commit). See e38_dps_flow_2011_silverado.
//   $7E ... Op-code $25 (SECURITY_CODE) wrapper. DPS sends a fixed-format
//           record $AE $7E $80 FF FF FF FF; only the positive echo matters.
//   $40 ... Op-code $25 variant - same shape.
// All CPIDs get a permissive positive echo. Hosts that care about specific
// return records will surface as a goto mismatch in the utility-file log;
// at that point we plumb a CPID-specific path through the ECU config.
//
// Functional addressing: $AE is point-to-point in every spec example
// (§8.21.5.1 uses physical $241). On functional we stay silent.
//
// Real-silicon note (E38 12647991 / E67 12656942, static analysis 2026-05-19):
// $AE is in the GMW3110-2010 PDF but on the surveyed bins it lives ONLY on the
// UDS-stack dispatcher reached via OBD CAN IDs $7DF/$7E0/$101; the GMW3110
// GMLAN-enhanced-diag dispatcher returns NRC $11 for $AE.
public static class ServiceAEHandler
{
    public static bool Handle(EcuNode node, ReadOnlySpan<byte> usdtPayload, ChannelSession ch, bool isFunctional = false)
    {
        if (isFunctional) return false;

        if (usdtPayload.Length < 2 || usdtPayload[0] != Service.RequestDeviceControl)
        {
            ServiceUtil.EnqueueNrc(node, ch, Service.RequestDeviceControl, Nrc.SubFunctionNotSupportedInvalidFormat);
            return false;
        }

        byte cpid = usdtPayload[1];
        node.State.Fragmenter.EnqueueResponse(ch, node.UsdtResponseCanId,
            [Service.Positive(Service.RequestDeviceControl), cpid]);
        return true;
    }
}
