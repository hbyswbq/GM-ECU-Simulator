using Common.Protocol;
using Common.Signals;
using Core.Bus;
using Core.Ecu;

namespace Core.Services;

// $01 ShowCurrentData per SAE J1979 (OBD-II Mode $01). Lives on the UDS-stack dispatcher on real GM silicon (see
// memory file project_dual_diag_stack_e38_e67.md - $01 is index 0 of the 27-entry UDS SID table). The GMW3110
// GMLAN-enhanced dispatcher does not implement it, so a tester on a GMW3110-only CAN ID gets NRC $11 - that gating
// is now structural: a GM node on a non-OBD CAN id binds only the restricted enhanced SID set, so $01 is simply not
// owned there and EcuNode.Resolve NRC-$11s it before this handler is reached (see ProtocolStacks.SynthesizeFor).
//
// This is the signal-backed projection of the new ECU strategy: every value comes from the ECU's EngineModel (live,
// scenario-driven signals) or its DiscreteState (status PIDs), encoded with the LEGISLATED J1979 formula in
// J1979Catalogue. The support-list PIDs ($00/$20/...) are computed from the ECU's advertised subset, never stored.
//
// USDT request:   byte[0] = 0x01, bytes[1..] = N x 1-byte PID ids
// USDT response:  byte[0] = 0x41, bytes[1..] = K x { PID id, value bytes }, K = supported count
//
// Per J1979 multi-PID requests are supported; unsupported PIDs are silently dropped; if NONE are supported a
// physical request gets NRC $31 ROOR and a functional broadcast stays silent (the GMW3110 §8.6.4 convention).
public static class Service01Handler
{
    public static void Handle(EcuNode node, ReadOnlySpan<byte> usdtPayload, ChannelSession ch,
                              double timeMs, bool isFunctional)
    {
        if (usdtPayload.Length < 2 || usdtPayload[0] != Service.Obd01ShowCurrentData)
        {
            if (!isFunctional)
                ServiceUtil.EnqueueNrc(node, ch, Service.Obd01ShowCurrentData,
                                        Nrc.SubFunctionNotSupportedInvalidFormat);
            return;
        }

        var supported = node.Mode1Supported;

        // Resolve each requested PID to something answerable. A bitmask PID ($00/$20/...) is answered when the map
        // reaches into or past its block; a data PID is answered only when it is in this ECU's advertised subset.
        // Anything else is dropped from the response (J1979's silent-omit rule), tracked by Def == null = bitmask.
        int requested = usdtPayload.Length - 1;
        var items = new List<(byte Pid, int Length, J1979Pid? Def)>(requested);
        int payloadLen = 0;
        for (int i = 0; i < requested; i++)
        {
            byte pid = usdtPayload[1 + i];
            if (J1979Catalogue.IsBitmaskPid(pid))
            {
                if (J1979Catalogue.BitmaskAnswerable(pid, supported))
                {
                    items.Add((pid, 4, null));
                    payloadLen += 1 + 4;
                }
            }
            else if (supported.Contains(pid) && J1979Catalogue.Get(pid) is { } def)
            {
                items.Add((pid, def.Length, def));
                payloadLen += 1 + def.Length;
            }
        }

        if (items.Count == 0)
        {
            if (!isFunctional)
                ServiceUtil.EnqueueNrc(node, ch, Service.Obd01ShowCurrentData, Nrc.RequestOutOfRange);
            return;
        }

        var resp = new byte[1 + payloadLen];
        resp[0] = Service.Positive(Service.Obd01ShowCurrentData);
        int pos = 1;
        foreach (var (pid, length, def) in items)
        {
            resp[pos++] = pid;
            var dest = resp.AsSpan(pos, length);
            if (def is null)
                J1979Catalogue.ComputeSupportMask(pid, supported, dest);
            else
                def.Encode(node.EngineModel, node.DiscreteState, timeMs, dest);
            pos += length;
        }

        node.State.Fragmenter.EnqueueResponse(ch, node.UsdtResponseCanId, resp);
    }
}
