namespace Core.Dps;

// Cheap-parse output for the wizard's archive page. ArchivePrimer.ParseArchive
// extracts the zip, reads the utility-file header + cal count + OS module
// header, and returns this. No solver - that runs later in Prime().
//
// OsCalBytes is kept here so a later prime doesn't pay for a second disk read
// of the OS module. The extraction temp dir lives as long as the wrapping
// DpsArchive; we hold only the bytes we need afterwards.
public sealed record ArchiveDescriptor(
    string ArchivePath,
    string UtilityFileName,
    int CalibrationBlockCount,
    string? OsPartNumber,           // null only when archive has no OS module
    string? OsAlphaCode,
    byte[] OsCalBytes);             // empty array when archive has no OS module
