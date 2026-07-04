namespace Core.Protocol;

// One service in a standard's catalog: the wire SID, its standard-given name,
// and an optional display group (e.g. the GMLAN-enhanced $A0+ block, so the
// editor can render it as a distinct section per DESIGN-Protocol-Stack-
// Architecture.md section 8).
//
// The catalog is the "gospel" - the full per-standard service contract defined
// once in code (DESIGN doc principle 5). Config stores only the SID allow-list
// and never re-describes a service.
//
// Step 1 of the protocol-stack migration carries names + grouping only; the
// per-SID handler binding is attached when each stack's dispatch is wired
// (steps 2 and 4). A record keeps catalog values immutable and cheaply
// comparable in the tests that lock catalog membership.
public sealed record ServiceDescriptor(byte Sid, string Name, string? Group = null);
