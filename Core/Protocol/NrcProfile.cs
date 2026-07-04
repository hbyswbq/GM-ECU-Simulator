namespace Core.Protocol;

// The negative-response-code vocabulary a stack speaks. The divergence that
// matters: GM (GMW3110 / KWP2000) signals a malformed request with NRC $12,
// Ford/UDS with the distinct $13 (Ford-vs-GM-Service-Divergence.md, cross-
// cutting section). Each stack carries its own profile so a handler can never
// answer in the wrong dialect's NRC vocabulary - the coupling the old GM-
// flavoured CommonServices layer baked in (DESIGN doc section 1).
//
// Values reference Common.Protocol.Nrc where the byte is shared. Step 1 models
// only the fields the dispatch / NRC-fallthrough paths need; more move in as
// each stack's handlers do (steps 2 and 4).
public sealed record NrcProfile(
    byte ServiceNotSupported,
    byte SubFunctionNotSupported,
    byte BadLength,
    byte RequestOutOfRange,
    byte SecurityAccessDenied)
{
    // GMW3110 / KWP2000 (GM dialect): the GM table folds bad-length into
    // $12 (SubFunctionNotSupported-InvalidFormat).
    public static readonly NrcProfile Gm = new(
        ServiceNotSupported:     Common.Protocol.Nrc.ServiceNotSupported,                  // $11
        SubFunctionNotSupported: Common.Protocol.Nrc.SubFunctionNotSupportedInvalidFormat, // $12
        BadLength:               Common.Protocol.Nrc.SubFunctionNotSupportedInvalidFormat, // $12
        RequestOutOfRange:       Common.Protocol.Nrc.RequestOutOfRange,                    // $31
        SecurityAccessDenied:    Common.Protocol.Nrc.SecurityAccessDenied);               // $33

    // ISO 14229 (UDS / Ford): subFunctionNotSupported is $12 and incorrect-
    // MessageLengthOrInvalidFormat is the separate $13.
    public static readonly NrcProfile Uds = new(
        ServiceNotSupported:     0x11,
        SubFunctionNotSupported: 0x12,
        BadLength:               0x13,
        RequestOutOfRange:       0x31,
        SecurityAccessDenied:    0x33);
}
