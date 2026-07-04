namespace Core.Protocol;

// The per-standard service catalogs - the "gospel" defined once in code (DESIGN
// doc principle 5). Each dialect declares its OWN full service set directly via
// ServiceCatalog.Of, so what a standard speaks is read off one literal list - no
// derive-then-patch indirection. KWP2000 is kept below purely as a REFERENCE
// ancestor (it is not a runtime stack and nothing derives from it); the trailing
// comment on each GMW3110 / UDS entry records how that SID relates to its KWP
// form (renamed / inherited / GM-or-UDS-only / dropped) so the lineage the old
// Add/Replace/Remove chain used to encode is still visible at a glance.
//
// Membership is locked by Tests.Unit/Protocol/StandardCatalogMembershipTests.
// The catalogs are the full, silicon/MDX-correct sets (the design doc defers the
// complete catalogs to code):
//   - GMW3110 == the 19-SID OBD dispatcher confirmed on real E38/E67 silicon
//     (memory project_dual_diag_stack_e38_e67): clear is J1979 $04, DTC reads are
//     $A9, device control is $AE, there is no enable/upload/write-memory service.
//   - UDS keeps $2F InputOutputControlByIdentifier (the modelled Ford PCM enables
//     it) and is the gospel superset; a specific Ford ECU enables a subset via
//     its allow-list.
//
// Catalogs carry names + grouping only; per-SID handlers are attached when each
// stack's Dispatch is wired (steps 2 and 4).
public static class StandardCatalogs
{
    // ---- GMW3110 / GMLAN (GM) - KWP2000 + J2190 harmonized + GMLAN $A0+ enhanced ----
    // Lineage tag per entry:
    // (= KWP) inherited as-is, (KWP $XX) renamed from that KWP SID, (GM) GM-only.
    public static readonly ServiceCatalog Gmw3110 = ServiceCatalog.Of(
        (0x10, "InitiateDiagnosticOperation"),    // KWP $10 StartDiagnosticSession - DTC/comms flags, not a session
        (0x12, "ReadFailureRecordData"),          // GM-only
        (0x1A, "ReadECUIdentification"),          // = KWP
        (0x20, "ReturnToNormalMode"),             // GM-only (the $28 re-enable counterpart)
        (0x22, "ReadDataByParameterIdentifier"),  // KWP $22 ReadDataByCommonIdentifier - live PIDs (identity stays on $1A)
        (0x23, "ReadMemoryByAddress"),            // = KWP
        (0x27, "SecurityAccess"),                 // = KWP
        (0x28, "DisableNormalCommunication"),     // KWP $28 - bare; re-enable is the separate $20
        (0x2C, "DynamicallyDefineMessage"),       // KWP $2C DynamicallyDefineLocalIdentifier
        (0x2D, "DefinePidByAddress"),             // GM-only; UDS folds this into $2C
        (0x34, "RequestDownload"),                // = KWP
        (0x36, "TransferData"),                   // = KWP ($36 carries per-block addr; no $37 exit)
        // NOTE: $35 RequestUpload (flash-READ) is deliberately NOT in the base
        // enhanced-diag table - it is not a real GMW3110/silicon service. Both GM
        // reader tools issue it only from WITHIN a boot-loaded kernel (PowerPCM @
        // 0x3FB800, 6Speed.T43 @ 0x3FC430), so it lives in the UdsKernel catalog +
        // UdsKernelDispatch. See Service35Handler / ReadEmulation.
        (0x3B, "WriteDataByIdentifier"),          // KWP $3B WriteDataByLocalIdentifier (UDS dropped it for $2E)
        (0x3E, "TesterPresent"),                  // = KWP
        (0xA2, "ReportProgrammedState"),          // GM-only ($A0+ GMLAN enhanced)
        (0xA5, "ProgrammingMode"),                // GM-only ($A0+ GMLAN enhanced)
        (0xA9, "ReadDiagnosticInformation"),      // GM-only ($A0+ GMLAN enhanced); UDS analog: $19
        (0xAA, "ReadDataByPacketIdentifier"),     // GM-only ($A0+ GMLAN enhanced); UDS analog: $2A
        (0xAE, "DeviceControl"));                 // GM-only ($A0+ GMLAN enhanced); UDS analog: $2F

    // ---- UDS (ISO 14229) ----
    // Lineage tag per entry:
    // (= KWP) inherited as-is, (KWP $XX) renamed, (UDS) UDS-only.
    public static readonly ServiceCatalog Uds = ServiceCatalog.Of(
        (0x10, "DiagnosticSessionControl"),       // KWP $10 StartDiagnosticSession - sessions, not start/stop
        (0x11, "ECUReset"),                       // = KWP
        (0x14, "ClearDiagnosticInformation"),     // = KWP
        (0x19, "ReadDtcInformation"),             // UDS; replaces KWP $17/$18
        (0x22, "ReadDataByIdentifier"),           // KWP $22 ReadDataByCommonIdentifier - 2-byte DID
        (0x23, "ReadMemoryByAddress"),            // = KWP
        (0x24, "ReadScalingDataByIdentifier"),    // UDS-only
        (0x27, "SecurityAccess"),                 // = KWP
        (0x28, "CommunicationControl"),           // KWP $28 - folds enable/disable into one $28
        (0x29, "Authentication"),                 // KWP $29 EnableNormalMsgTx byte reused (14229:2020+)
        (0x2A, "ReadDataByPeriodicIdentifier"),   // UDS-only
        (0x2C, "DynamicallyDefineLocalIdentifier"), // = KWP
        (0x2E, "WriteDataByIdentifier"),          // KWP $2E WriteDataByCommonIdentifier - 2-byte DID
        (0x2F, "InputOutputControlByIdentifier"), // KWP $2F InputOutputControlByCommonIdentifier
        (0x31, "RoutineControl"),                 // KWP $31; consolidates KWP $31/$32/$33
        (0x34, "RequestDownload"),                // = KWP
        (0x35, "RequestUpload"),                  // = KWP
        (0x36, "TransferData"),                   // = KWP
        (0x37, "RequestTransferExit"),            // = KWP
        (0x3D, "WriteMemoryByAddress"),           // = KWP
        (0x3E, "TesterPresent"),                  // = KWP
        (0x85, "ControlDTCSetting"));             // KWP $85 (same name)

    // ---- SAE J1979 (legislated OBD-II) ----
    // The catalog is the full standard; a given ECU enables a subset (e.g. E38/E67
    // omit $05/$08) via its allow-list.
    public static readonly ServiceCatalog J1979 = ServiceCatalog.Of(
        (0x01, "ShowCurrentData"),
        (0x02, "ShowFreezeFrameData"),
        (0x03, "ShowStoredDtcs"),
        (0x04, "ClearEmissionsDtcs"),       
        (0x05, "TestResultsO2Sensors"),
        (0x06, "TestResultsOnBoardMonitors"),
        (0x07, "ShowPendingDtcs"),
        (0x08, "ControlOnBoardComponent"),
        (0x09, "RequestVehicleInformation"),
        (0x0A, "PermanentDtcs"));

    // ---- Ford capture persona - the full menu of services a Ford ECU might present ----
    // NOT a real standard: the UNION of the J1979 OBD modes + the UDS gospel + the Ford GGDS
    // proprietary services ($A0/$A1/$B1), each group-tagged so the editor renders OBD / UDS /
    // Ford-proprietary as three separate sections (the same segmented shape GM gets from its J1979 +
    // GMW3110 bindings). The catalog is the full MENU, not a code-derived "implemented" subset: the
    // user TICKS which services this ECU supports. Three on-wire behaviours: a ticked SID is allowed
    // through (answered if FordUdsDispatch implements it, else it declines and the bus NRC-$11s,
    // exactly like GM's ticked-but-unimplemented catalog entries); an unticked SID NRC-$11s at the
    // gate; a SID NOT in this catalog at all is logged by the capture stack and otherwise ignored (no
    // reply). Defined AFTER Uds + J1979 + FordGGDS so their static initialisers run first. Membership
    // locked by Tests.Unit/Protocol/StandardCatalogMembershipTests.Ford_Membership.
    public const string FordObdGroup = "OBD (J1979)";
    public const string FordProprietaryGroup = "Ford Proprietary";

    // ---- Ford GGDS (Generic Global Diagnostic Specification) proprietary services ----
    // The Ford corporate analog to GM's GMW3110. These $A0/$A1/$B1 SIDs are the GGDS
    // proprietary carve-out (DMR rapid-packet datalogging + block read), distinct from the
    // UDS/J1979 spine the Ford capture catalog folds them into below. Source: the DET MDX
    // database names the spec "Generic Global Diagnostic Specification" / shortname "GGDS".
    public static readonly ServiceCatalog FordGGDS = ServiceCatalog.Of(
        (0xA0, "ReadDataMode"),    // DMR rapid-packet read
        (0xA1, "SetupDataMode"),   // DMR slot -> RAM-address bind
        (0xB1, "ReadBlock"));      // block read / flash erase

    public static readonly ServiceCatalog Ford = BuildFordCatalog();

    private static ServiceCatalog BuildFordCatalog()
    {
        var ford = Uds;                                       // the UDS gospel (the "UDS" section)
        foreach (var d in J1979.Services)                     // + the OBD modes $01-$0A
            ford = ford.Add(d.Sid, d.Name, FordObdGroup);
        foreach (var d in FordGGDS.Services)                  // + the Ford GGDS proprietary services
            ford = ford.Add(d.Sid, d.Name, FordProprietaryGroup);
        return ford;
    }
}
