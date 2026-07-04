namespace Core.Protocol;

// The CAN IDs a stack binding answers on and replies with. Dispatch resolves by
// (canId, sid), so Match() decides whether an inbound CAN ID belongs to this
// binding; Request and the two functional broadcast IDs are inbound, Response /
// Uudt are outbound only (DESIGN doc section 5).
//
// Two functional IDs because a GM ECU answers BOTH the ISO 15765-4 OBD broadcast
// $7DF AND GMLAN's $101 (+ $FE ext-addr) - see VirtualBus.DispatchObd2Functional
// / DispatchFunctional. A Ford ECU uses only $7DF (leave FunctionalAlt null).
public sealed record AddressingModel(
    ushort? Request,
    ushort? Response,
    ushort? Functional = null,
    ushort? FunctionalAlt = null,
    ushort? Uudt = null)
{
    public bool Match(uint canId) =>
        (Request is { } r && canId == r) ||
        (Functional is { } f && canId == f) ||
        (FunctionalAlt is { } fa && canId == fa);

    // OBD-II / GMLAN functional broadcast request IDs every GM (and OBD) binding
    // listens on, matching VirtualBus's two functional-dispatch entry points.
    public const ushort Obd2Functional = 0x7DF;
    public const ushort GmlanFunctional = 0x101;
}
