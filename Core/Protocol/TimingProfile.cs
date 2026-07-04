namespace Core.Protocol;

// Application-layer timing the stack advertises / honours, in milliseconds. GM
// uses GMW3110 section 6.2 P2C / P3C; UDS uses P2 / P2* / S3. The TesterPresent
// ticker keys the session timeout off SessionTimeoutMs; the response pacer
// (Core/Services/ResponseTiming) keys off P2Ms / P2StarMs. The GM values mirror
// Common.Protocol.Timing so there is a single source for them.
//
// P2Ms is the ECU-SIDE response budget (the time by which the ECU must complete
// its first response frame), NOT the tester-side timeout: GMW3110 splits these
// into P2CE (ECU, 100 ms max) and P2CT (tester, 150 ms) with deliberate headroom
// between them, and ISO 14229 names the ECU side P2_server. The pacer emits the
// 7F sid 78 heartbeat against this ECU budget so it lands before the tester's
// (larger) timeout lapses. P2StarMs is the extended budget after a 78.
public sealed record TimingProfile(int P2Ms, int P2StarMs, int SessionTimeoutMs)
{
    // GMW3110 section 6.2: P2CE 100 ms (ECU budget), P2CT* 5100 ms, P3Cnom 5000 ms.
    public static readonly TimingProfile Gm = new(
        P2Ms:             Common.Protocol.Timing.P2CE,        // 100 (ECU-side P2CE, not the 150 tester P2CT)
        P2StarMs:         Common.Protocol.Timing.P2CT_Star,   // 5100
        SessionTimeoutMs: Common.Protocol.Timing.P3Cnom);     // 5000

    // ISO 14229 modelled-Ford-PCM timing: P2_server 50 ms, P2* 5000 ms, S3 5000 ms.
    // Ford P2_MAX is tighter than GM's P2CE (Ford-vs-GM divergence, cross-cutting).
    public static readonly TimingProfile Uds = new(P2Ms: 50, P2StarMs: 5000, SessionTimeoutMs: 5000);
}
