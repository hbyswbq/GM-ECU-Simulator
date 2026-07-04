using Common.Protocol;
using Core.Bus;
using Core.Ecu;

namespace Core.Services;

// $09 RequestVehicleInformation per SAE J1979 (OBD-II Mode $09). Legislated OBD is manufacturer-agnostic, so GM and
// Ford share this ONE handler: the J1979 stack dispatches it directly for GM (ProtocolStacks.DispatchJ1979) and the
// ford-uds capture dispatch delegates $09 here (FordUdsDispatch). It is the make-neutral replacement for the old
// Ford-only canned Mode-09 builders.
//
// Mode $09 store-sourced: VIN (InfoType $02) and Calibration ID (InfoType $04) come from the ECU's dedicated Mode $09
// store (EcuNode.GetMode09Info), NOT from the $1A identity block. Mode $09 is legislated OBD and is answered even by a
// persona that doesn't implement GMW3110 $1A at all (the Ford UDS capture stack), so it must not lean on the Mode1A
// dictionary as a backing source. The store is seeded per-persona at config-apply (ConfigStore): Ford from the flash
// bin (FordUdsDispatch.SeedMode09Identity) - which keeps a $09 VIN reply byte-for-byte identical to a $23 read of
// 0x000100C0, the cross-check PCMTec performs - and GM by projecting its own $1A $90/$C0 identity once via
// SeedFromIdentity below (on real GM silicon the Mode $09 VIN/CALID equal the $1A identity).
//
// USDT request:   byte[0] = 0x09, bytes[1..] = N x 1-byte InfoType ids
// USDT response:  byte[0] = 0x49, then per supported InfoType, in request order:
//   $00 supported-InfoType bitmask:  <00> <4-byte mask>            (a support PID carries no NODI)
//   $02 VIN:                         <02> <NODI=01> <17 VIN bytes>
//   $04 CALID:                       <04> <NODI=01> <16 CALID bytes, zero-padded / truncated>
//
// Per J1979 unsupported requested InfoTypes are silently dropped; if NONE are supported a physical request gets NRC $31
// RequestOutOfRange and a functional broadcast stays silent (mirrors Service01Handler / the GMW3110 §8.6.4 convention).
public static class Service09Handler
{
    // The two answered InfoTypes, keyed into the Mode $09 store.
    private const byte VinInfoType   = 0x02;
    private const byte CalIdInfoType = 0x04;
    private const int  VinLen        = 17;   // ISO 3779 VIN length
    private const int  CalIdLen      = 16;   // one 16-byte CALID item

    // The $1A identity DIDs a GM node projects into its Mode $09 store (see SeedFromIdentity). $90 is the canonical VIN
    // DID; $C0 (Operating Software ID) is the closest cross-make identity slot for the OBD "Calibration ID" string.
    private const byte VinDid   = 0x90;
    private const byte CalIdDid = 0xC0;

    public static void Handle(EcuNode node, ReadOnlySpan<byte> usdt, ChannelSession ch, bool isFunctional)
    {
        if (usdt.Length < 2 || usdt[0] != Service.RequestVehicleInformation)
        {
            if (!isFunctional)
                ServiceUtil.EnqueueNrc(node, ch, Service.RequestVehicleInformation,
                                       Nrc.SubFunctionNotSupportedInvalidFormat);
            return;
        }

        byte[]? vin   = node.GetMode09Info(VinInfoType);
        byte[]? calId = node.GetMode09Info(CalIdInfoType);

        // Build each requested InfoType's response block in request order; drop the unsupported ones (silent-omit).
        var body = new List<byte>(32);
        for (int i = 1; i < usdt.Length; i++)
        {
            switch (usdt[i])
            {
                case 0x00:                                  // supported-InfoType bitmask ($01..$20)
                    body.Add(0x00);
                    body.AddRange(SupportMask(vin is not null, calId is not null));
                    break;
                case 0x02 when vin is not null:             // VIN
                    body.Add(0x02);
                    body.Add(0x01);                         // NODI = 1 VIN
                    body.AddRange(Fit(vin, VinLen));
                    break;
                case 0x04 when calId is not null:           // Calibration ID
                    body.Add(0x04);
                    body.Add(0x01);                         // NODI = 1 CALID
                    body.AddRange(Fit(calId, CalIdLen));
                    break;
            }
        }

        if (body.Count == 0)
        {
            if (!isFunctional)
                ServiceUtil.EnqueueNrc(node, ch, Service.RequestVehicleInformation, Nrc.RequestOutOfRange);
            return;
        }

        var resp = new byte[1 + body.Count];
        resp[0] = Service.Positive(Service.RequestVehicleInformation);   // 0x49
        body.CopyTo(resp, 1);
        node.State.Fragmenter.EnqueueResponse(ch, node.UsdtResponseCanId, resp);
    }

    /// <summary>
    /// Seed a node's Mode $09 store by projecting its $1A identity. Used for personas that DO implement $1A (GM):
    /// on real GM silicon the Mode $09 VIN/CALID equal the $1A $90/$C0 identity, so we copy them once at config-apply
    /// (ConfigStore), AFTER the config's PID/identifier rows have loaded. Personas without $1A (the Ford UDS capture
    /// stack) seed their own Mode $09 store from the flash bin instead and never call this. A DID that carries no
    /// value leaves the corresponding InfoType unsupported.
    /// </summary>
    public static void SeedFromIdentity(EcuNode node)
    {
        var vin = IdentityOf(node, VinDid);
        if (vin is not null) node.SetMode09Info(VinInfoType, vin);
        var calId = IdentityOf(node, CalIdDid);
        if (calId is not null) node.SetMode09Info(CalIdInfoType, calId);
    }

    // Resolve a $1A identity DID from either store: the editor-grid Mode1A row (StaticBytes) wins, then the
    // bin/archive-primed identifier dictionary. Returns null when neither carries a non-empty value.
    private static byte[]? IdentityOf(EcuNode node, byte did)
    {
        var row = node.GetMode1APid(did)?.StaticBytes;
        if (row is { Length: > 0 }) return row;
        var ident = node.GetIdentifier(did);
        return ident is { Length: > 0 } ? ident : null;
    }

    // The InfoType $00 support bitmask for $01..$20. Bit (7-((pid-1)%8)) of byte ((pid-1)/8): $02 -> 0x40 and
    // $04 -> 0x10, both in the first byte.
    private static byte[] SupportMask(bool vin, bool calId)
    {
        var mask = new byte[4];
        if (vin)   mask[0] |= 0x40;   // InfoType $02 VIN
        if (calId) mask[0] |= 0x10;   // InfoType $04 CALID
        return mask;
    }

    // Right-size a backing value to the wire length: truncate if longer, zero-pad if shorter.
    private static byte[] Fit(byte[] src, int len)
    {
        if (src.Length == len) return src;
        var dst = new byte[len];
        Array.Copy(src, dst, Math.Min(src.Length, len));
        return dst;
    }
}
