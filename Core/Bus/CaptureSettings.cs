namespace Core.Bus;

// Bootloader-capture configuration. Captures used to be gated on an explicit
// Enabled toggle that the user had to tick before a programming session;
// missing the toggle meant Service36Handler's spec-mode bounds check
// rejected real-DPS absolute addresses with NRC $31. The address-anchoring
// model in Service36Handler now handles real-DPS sessions unconditionally,
// so capture-to-disk is just a side effect: writes always happen when a
// CaptureDirectory is set, never when it isn't.
//
// CaptureDirectory is nullable on purpose. Unit tests construct VirtualBus
// directly and leave it null - BootloaderCaptureWriter no-ops in that case
// so the tests don't pollute the user's real captures folder. WPF startup
// sets the property to %LOCALAPPDATA%\GmEcuSimulator\logs\captures.
public sealed class CaptureSettings
{
    /// <summary>
    /// Directory the bootloader-capture writer drops .bin files into. When
    /// null, all capture writes are no-ops. Set by WPF startup; tests set
    /// their own temp directory when they want to assert files on disk.
    /// </summary>
    public string? CaptureDirectory { get; set; }

    /// <summary>
    /// Directory the consolidated full-image writer drops its .bin files into:
    /// one positioned SPS image per programming session (the union of every
    /// $31-declared erase region), plus the PcmHammer kernel's flash image
    /// verbatim. Distinct from <see cref="CaptureDirectory"/> (which collects
    /// the loose per-$36 fragments): these are the finished, flashable-shaped
    /// images the user actually wants to keep. When null, the full-image
    /// writes are no-ops (unit-test default). WPF startup sets it to
    /// %LOCALAPPDATA%\GmEcuSimulator\bins.
    /// </summary>
    public string? BinOutputDirectory { get; set; }

    /// <summary>
    /// Raised after a capture file is successfully written. Argument is the
    /// full path to the written .bin. UI subscribes to refresh the captured-
    /// downloads list without polling.
    /// </summary>
    public event Action<string>? CaptureWritten;

    internal void RaiseCaptureWritten(string path) => CaptureWritten?.Invoke(path);

    /// <summary>
    /// Default capture directory used by WPF startup. Exposed so the UI
    /// can show the path even before any capture has been written.
    /// </summary>
    public static string DefaultDirectory()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return System.IO.Path.Combine(local, "GmEcuSimulator", "logs", "captures");
    }

    /// <summary>
    /// Default full-image directory used by WPF startup:
    /// %LOCALAPPDATA%\GmEcuSimulator\bins. Kept out of the logs/ tree because
    /// these are keepsake flash images, not a debug trail.
    /// </summary>
    public static string DefaultBinDirectory()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return System.IO.Path.Combine(local, "GmEcuSimulator", "bins");
    }
}
