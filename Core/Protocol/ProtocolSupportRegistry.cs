namespace Core.Protocol;

// The canonical, user-facing registry of what this app supports: every diagnostic
// protocol it presents, every service/mode each protocol defines (whether we answer
// it or not), and - the one fact the catalogs DON'T carry - whether a service has a
// positive (non-NRC) response path in code or is always NRC'd.
//
// Single source of truth, by construction:
//   - SID + name + display group are REUSED from StandardCatalogs (the gospel,
//     DESIGN doc principle 5) and the built stacks' Catalog - this registry never
//     re-describes a service, so a rename in the catalog flows here automatically.
//   - The only NEW axis is Implemented: the per-protocol set of SIDs that have a
//     positive response path. Each set is declared ONCE below with a pointer to the
//     dispatch that backs it, and is LOCKED to real dispatch behaviour by
//     Tests.Unit/Protocol/ProtocolSupportRegistryTests (an Implemented=false SID is
//     asserted to NRC end-to-end; a true one is asserted not to NRC
//     serviceNotSupported). The tests are what make this canonical rather than a
//     parallel list that can drift from the switch statements.
//
// "Implemented" means a code path CAN produce a positive response, not that it
// always succeeds. $23 ReadMemoryByAddress on the Ford stack NRCs when no flash bin
// is loaded but is still implemented; $A9 ReadDiagnosticInformation on GMW3110 has no
// case at all and always NRC-$11s, so it is not.
//
// The Protocols window (GmEcuSimulator/Views/ProtocolsWindow) renders this directly:
// one section per ProtocolSupport, a read-only checkbox per service driven by
// Implemented.
public static class ProtocolSupportRegistry
{
    // ---- GMW3110 / GMLAN: SIDs with a positive path in Gmw3110Dispatch ----------------------
    // The cases present in Core/Protocol/Gmw3110Dispatch.Dispatch. The three GMW3110-catalog SIDs
    // NOT here ($12 ReadFailureRecordData, $23 ReadMemoryByAddress, $A9 ReadDiagnosticInformation)
    // hit the switch's default-false return and the bus NRC-$11s them - the canonical "we declare
    // it, we don't answer it" set. ($35 RequestUpload is NOT a base GMW3110 service; it is answered
    // only in kernel mode - see the UdsKernel section below and Service35Handler.)
    private static readonly HashSet<byte> Gmw3110Implemented = new()
    {
        0x10, 0x1A, 0x20, 0x22, 0x27, 0x28, 0x2C, 0x2D, 0x34, 0x36, 0x3B, 0x3E,
        0xA2, 0xA5, 0xAA, 0xAE,
    };

    // ---- SAE J1979 (OBD-II): SIDs with a positive path in ProtocolStacks.DispatchJ1979 ----------
    // Only $01 ShowCurrentData and $09 RequestVehicleInformation have handlers (Service01Handler /
    // Service09Handler); the other eight legislated modes decline and the bus NRC-$11s. These two
    // handlers are SHARED - the Ford capture stack delegates its $01/$09 to the very same code.
    private static readonly HashSet<byte> J1979Implemented = new() { 0x01, 0x09 };

    // ---- UDS / Ford PCM: SIDs with a positive path in FordUdsDispatch (+ the shared $22) ----------
    // The Ford capture stack answers $22 (Service22Handler, via ProtocolStacks.DispatchUds),
    // $23/$27/$11/$3E/$34/$36/$37 and the Ford-proprietary $A0/$A1/$B1 directly; every other UDS
    // service is logged then NRC-$11'd. ($01/$09 are OBD modes - they live in the J1979 section,
    // not here.) See Core/Protocol/FordUdsDispatch.Dispatch.
    private static readonly HashSet<byte> FordImplemented = new()
    {
        0x11, 0x22, 0x23, 0x27, 0x34, 0x36, 0x37, 0x3E, 0xA0, 0xA1, 0xB1,
    };

    // ---- SPS programming kernel: SIDs with a positive path in UdsKernelDispatch (+ the shared $22) ----
    // The transient kernel ($36 sub $80 .. $20/P3C) answers its whole narrow catalog: $31/$3E/$20/
    // $34/$35/$36 in UdsKernelDispatch, plus $22 via ProtocolStacks.DispatchUdsKernel. ($35 is the
    // 6Speed.T43 read-kernel's flash-read command.)
    private static readonly HashSet<byte> KernelImplemented = new() { 0x20, 0x22, 0x31, 0x34, 0x35, 0x36, 0x3E };

    // The protocol sections, in the order the window shows them: legislated OBD first, then the GM
    // enhanced-diag dialect, the Ford UDS capture stack, and finally the transient SPS kernel.
    public static IReadOnlyList<ProtocolSupport> Protocols { get; } = BuildProtocols();

    private static IReadOnlyList<ProtocolSupport> BuildProtocols()
    {
        return new[]
        {
            new ProtocolSupport(
                "J1979", "SAE J1979 (OBD-II)",
                "Legislated emissions diagnostics. Make-agnostic, so these handlers are shared - GM " +
                "reaches them on an OBD CAN id and the Ford capture stack delegates $01/$09 to the same code.",
                FromCatalog(StandardCatalogs.J1979, J1979Implemented)),

            new ProtocolSupport(
                "GMW3110", "GMW3110-2010 (GMLAN enhanced diagnostics)",
                "The GM dialect on real E38/E67 OBD-dispatcher silicon: 14 KWP-derived core services plus " +
                "the 5 GMLAN-enhanced $A0+ modes. NOT ISO-14229 UDS.",
                FromCatalog(StandardCatalogs.Gmw3110, Gmw3110Implemented)),

            new ProtocolSupport(
                "UDS", "UDS / ISO 14229 (Ford PCM capture)",
                "The Ford-only UDS surface plus the Ford-proprietary $A0/$A1/$B1 DMR services. The capture " +
                "stack logs every request; ticked services answer, the rest are NRC'd. (OBD modes appear in " +
                "the J1979 section above.)",
                // The Ford catalog is OBD + UDS + proprietary; the OBD modes are shown in the J1979
                // section, so drop them here and keep the UDS gospel (null group) + proprietary group.
                FromCatalog(StandardCatalogs.Ford, FordImplemented,
                            d => d.Group != StandardCatalogs.FordObdGroup)),

            new ProtocolSupport(
                "UDS-Kernel", "GM SPS programming kernel (transient)",
                "The narrow UDS-flavoured service table a downloaded SPS kernel presents after $36 sub $80 " +
                "DownloadAndExecute, until $20 or the P3C timeout hands control back.",
                FromCatalog(ProtocolStacks.UdsKernel.Catalog, KernelImplemented)),
        };
    }

    // Project a catalog's descriptors into ServiceSupport rows, stamping Implemented from the set.
    // The optional filter drops descriptors that belong to another section (Ford's OBD modes).
    private static IReadOnlyList<ServiceSupport> FromCatalog(
        ServiceCatalog catalog, HashSet<byte> implemented, Func<ServiceDescriptor, bool>? include = null)
    {
        var rows = new List<ServiceSupport>();
        foreach (var d in catalog.Services)   // already SID-ordered
        {
            if (include != null && !include(d)) continue;
            rows.Add(new ServiceSupport(d.Sid, d.Name, d.Group, implemented.Contains(d.Sid)));
        }
        return rows;
    }
}

// One protocol section in the registry: a display title, a one-line summary, and the full service
// list. Groups exposes the same services partitioned by their display group (preserving first-seen
// order) so the window can render sub-headers (e.g. Ford Proprietary) like the editor does.
public sealed record ProtocolSupport(
    string Id, string Title, string Summary, IReadOnlyList<ServiceSupport> Services)
{
    public int ServiceCount => Services.Count;
    public int ImplementedCount => Services.Count(s => s.Implemented);

    // "11 of 19 services answered" - the at-a-glance support stat the window shows per section.
    public string SupportSummary => $"{ImplementedCount} of {ServiceCount} services answered";

    public IReadOnlyList<ServiceGroup> Groups
    {
        get
        {
            // Build groups in first-seen order. A null Group (the ungrouped core block) is a valid
            // key here, so we can't use a Dictionary keyed on string? - Dictionary.TryGetValue(null)
            // throws ArgumentNullException. Append-or-extend against the ordered list instead.
            var groups = new List<(string? Name, List<ServiceSupport> Services)>();
            foreach (var s in Services)
            {
                int i = groups.FindIndex(g => g.Name == s.Group);
                if (i < 0)
                {
                    groups.Add((s.Group, new List<ServiceSupport> { s }));
                }
                else
                {
                    groups[i].Services.Add(s);
                }
            }
            return groups.Select(g => new ServiceGroup(g.Name, g.Services)).ToArray();
        }
    }
}

// A run of services that share a display group. Name is null for the ungrouped core block (the
// window then omits the sub-header). HasHeader keeps that test out of the XAML.
public sealed record ServiceGroup(string? Name, IReadOnlyList<ServiceSupport> Services)
{
    public bool HasHeader => !string.IsNullOrEmpty(Name);
}

// One service/mode row: the wire SID, its standard-given name, its optional display group, and
// whether this app has a positive (non-NRC) response path for it. Implemented drives the read-only
// checkbox; SidLabel is the "$10" the window shows.
public sealed record ServiceSupport(byte Sid, string Name, string? Group, bool Implemented)
{
    public string SidLabel => $"${Sid:X2}";
}
