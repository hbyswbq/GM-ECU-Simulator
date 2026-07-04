using Core.Bus;
using Core.Ecu;
using System.Globalization;

namespace Core.Services;

// Writes captured $36 transfer data to disk. Every $36 produces one .bin
// per dataRecord (WriteEachTransferData), and EcuExitLogic dumps one .bin
// per declared erase region at session end (WriteFlashRegions).
//
// Writes always happen when bus.Capture.CaptureDirectory is set; they no-op
// when it is null. WPF startup sets the directory; unit tests leave it null
// so they don't pollute the user's real captures folder.
public static class BootloaderCaptureWriter
{
    /// <summary>
    /// Per-$36 immediate write. Each $36 TransferData's dataRecord is dumped
    /// to its own .bin file - no reassembly buffer, no sparse-image offset
    /// math, no $34-bracket merging. Filename embeds a session-scoped seq
    /// (D3 since a programming session can issue dozens of $36s), the $36's
    /// startingAddress, and the dataRecord length.
    ///
    /// Result: a flash-tool author scanning the captures dir sees one file
    /// per logical "push" - the kernel pieces are obvious by their distinct
    /// sizes/addresses, staging-buffer cal chunks are obvious by repetition.
    /// </summary>
    public static void WriteEachTransferData(EcuNode node, VirtualBus bus,
                                             uint startingAddress, ReadOnlySpan<byte> dataRecord)
    {
        var settings = bus.Capture;
        if (string.IsNullOrEmpty(settings.CaptureDirectory)) return;
        if (dataRecord.Length == 0) return;

        try
        {
            // Pin the session timestamp on the first capture write so every
            // .bin from this session shares a stable yyyymmdd_HHmmss prefix.
            // Local time, not UTC: this folder name is what the user sees in
            // Explorer, and it should match the wall clock on their machine.
            if (node.State.DownloadCaptureSessionTimestamp is null)
                node.State.DownloadCaptureSessionTimestamp = DateTime.Now;

            var tsLocal = node.State.DownloadCaptureSessionTimestamp.Value;
            uint seq = node.State.DownloadCaptureSequence;
            string ts = tsLocal.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            // Per-session subdirectory so a fresh download doesn't fill the
            // captures root with dozens of loose files. Subdir name pins the
            // ECU + session start; filename inside is just seq + addr + len.
            string sessionDir = string.Format(CultureInfo.InvariantCulture,
                "{0}_{1}", Sanitise(node.Name), ts);
            string fullDir = Path.Combine(settings.CaptureDirectory!, sessionDir);
            Directory.CreateDirectory(fullDir);

            string fileName = string.Format(CultureInfo.InvariantCulture,
                "{0:D3}_{1:X8}_{2}.bin", seq, startingAddress, dataRecord.Length);
            string path = Path.Combine(fullDir, fileName);

            File.WriteAllBytes(path, dataRecord.ToArray());
            node.State.DownloadCaptureSequence++;

            bus.LogSim?.Invoke($"[capture] $36 #{seq}: {dataRecord.Length} B @ 0x{startingAddress:X8} -> {path}");
            settings.RaiseCaptureWritten(path);
        }
        catch (Exception ex)
        {
            bus.LogSim?.Invoke($"[capture] write failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Session-end consolidated flash dump. For every region the kernel
    /// declared via $31 EraseMemoryByAddress, writes one .bin sized to the
    /// declared erase length (any byte not overwritten by a $36 stays at
    /// the post-erase $FF, matching the on-device reality). Files land in
    /// the same per-session subdirectory as the per-$36 fragments so a
    /// user can compare the contiguous image against the individual pieces.
    ///
    /// Naming: {seq:D3}_flash_{start:X8}_{size}.bin. The leading seq comes
    /// from DownloadCaptureSequence so the flash dump sorts after the
    /// individual $36 fragments that built it.
    ///
    /// Called from EcuExitLogic before ClearProgrammingState wipes the
    /// regions. No-op when no CaptureDirectory is set or no region was
    /// declared.
    /// </summary>
    public static void WriteFlashRegions(EcuNode node, VirtualBus bus)
    {
        var settings = bus.Capture;
        if (string.IsNullOrEmpty(settings.CaptureDirectory)) return;
        if (node.State.CapturedFlashRegions.Count == 0) return;

        try
        {
            var tsLocal = node.State.DownloadCaptureSessionTimestamp ?? DateTime.Now;
            string ts = tsLocal.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string sessionDir = string.Format(CultureInfo.InvariantCulture,
                "{0}_{1}", Sanitise(node.Name), ts);
            string fullDir = Path.Combine(settings.CaptureDirectory!, sessionDir);
            Directory.CreateDirectory(fullDir);

            foreach (var region in node.State.CapturedFlashRegions)
            {
                uint seq = node.State.DownloadCaptureSequence++;
                string fileName = string.Format(CultureInfo.InvariantCulture,
                    "{0:D3}_flash_{1:X8}_{2}.bin", seq, region.StartAddress, region.Size);
                string path = Path.Combine(fullDir, fileName);

                File.WriteAllBytes(path, region.Buffer);

                bus.LogSim?.Invoke(
                    $"[capture] flash region 0x{region.StartAddress:X8} +{region.Size} " +
                    $"({region.BytesWritten} B written, rest 0xFF) -> {path}");
                settings.RaiseCaptureWritten(path);
            }
        }
        catch (Exception ex)
        {
            bus.LogSim?.Invoke($"[capture] flash region write failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Consolidated SPS full-image dump. Unions every $31-declared
    /// FlashEraseRegion into ONE contiguous, absolutely-positioned image
    /// spanning [min(StartAddress), max(StartAddress+Size)) and writes it to
    /// the BinOutputDirectory (separate from the per-$36 captures dir).
    ///
    /// Offsets are exact: each region's $36 writes were already mirrored into
    /// region.Buffer at (startingAddress - region.StartAddress) by
    /// Service36Handler, and here each region lands at (StartAddress - lo), so
    /// a written byte ends up at (startingAddress - lo) in the image - its true
    /// absolute position minus the image base. Gaps between regions (and any
    /// byte the kernel never programmed) stay at the post-erase $FF, matching
    /// on-device NOR reality.
    ///
    /// This is the SPS $34/$36 counterpart. The PcmHammer kernel write flow
    /// bypasses regions entirely (its cal writes land in NodeState.KernelFlash);
    /// WriteKernelFlashImage covers that path.
    ///
    /// Called from EcuExitLogic before ClearProgrammingState wipes the regions.
    /// No-op when no BinOutputDirectory is set or no region was declared.
    /// </summary>
    public static void WriteConsolidatedSpsImage(EcuNode node, VirtualBus bus)
    {
        var settings = bus.Capture;
        if (string.IsNullOrEmpty(settings.BinOutputDirectory)) return;
        var regions = node.State.CapturedFlashRegions;
        if (regions.Count == 0) return;

        try
        {
            // Union span. Use long for the high edge so a region that reaches
            // the top of the 32-bit address space can't wrap.
            uint lo = uint.MaxValue;
            long hi = 0;
            foreach (var region in regions)
            {
                if (region.StartAddress < lo) lo = region.StartAddress;
                long end = (long)region.StartAddress + region.Size;
                if (end > hi) hi = end;
            }
            long size = hi - lo;
            // Safety cap (same ceiling Service36Handler uses): refuse to
            // materialise a multi-GB image if two regions sit absurdly far apart.
            if (size <= 0 || size > Service36Handler.MaxDownloadBufferBytes) return;

            var image = new byte[size];
            image.AsSpan().Fill(0xFF);
            foreach (var region in regions)
                region.Buffer.AsSpan().CopyTo(image.AsSpan((int)(region.StartAddress - lo)));

            Directory.CreateDirectory(settings.BinOutputDirectory!);
            var tsLocal = node.State.DownloadCaptureSessionTimestamp ?? DateTime.Now;
            string ts = tsLocal.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string fileName = string.Format(CultureInfo.InvariantCulture,
                "{0}_{1}_sps-image_{2:X8}-{3:X8}_{4}.bin", Sanitise(node.Name), ts, lo, (uint)hi, size);
            string path = Path.Combine(settings.BinOutputDirectory!, fileName);

            File.WriteAllBytes(path, image);

            bus.LogSim?.Invoke(
                $"[bin] SPS image 0x{lo:X8}-0x{hi:X8} ({size} B, {regions.Count} region(s)) -> {path}");
            settings.RaiseCaptureWritten(path);
        }
        catch (Exception ex)
        {
            bus.LogSim?.Invoke($"[bin] SPS image write failed: {ex.Message}");
        }
    }

    /// <summary>
    /// PcmHammer kernel full-image dump. The PcmHammer/PCMHacking write kernel
    /// serves its own $36 command set (PcmHammerKernel.HandleWrite), which
    /// copies each block straight into NodeState.KernelFlash at its absolute
    /// address - so KernelFlash is ALREADY a positioned, full flash image ($FF
    /// where erased/never-written). We just write it out verbatim.
    ///
    /// KernelFlash is lazily allocated on the kernel's first touch, so a null
    /// buffer means no PcmHammer kernel ever ran this session - nothing to dump.
    ///
    /// Called from EcuExitLogic before ClearProgrammingState nulls KernelFlash.
    /// No-op when no BinOutputDirectory is set or the kernel never ran.
    /// </summary>
    public static void WriteKernelFlashImage(EcuNode node, VirtualBus bus)
    {
        var settings = bus.Capture;
        if (string.IsNullOrEmpty(settings.BinOutputDirectory)) return;
        var flash = node.State.KernelFlash;
        if (flash is null) return;

        try
        {
            Directory.CreateDirectory(settings.BinOutputDirectory!);
            var tsLocal = node.State.DownloadCaptureSessionTimestamp ?? DateTime.Now;
            string ts = tsLocal.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string fileName = string.Format(CultureInfo.InvariantCulture,
                "{0}_{1}_kernel-flash_{2}.bin", Sanitise(node.Name), ts, flash.Length);
            string path = Path.Combine(settings.BinOutputDirectory!, fileName);

            File.WriteAllBytes(path, flash);

            bus.LogSim?.Invoke($"[bin] PcmHammer kernel flash ({flash.Length} B) -> {path}");
            settings.RaiseCaptureWritten(path);
        }
        catch (Exception ex)
        {
            bus.LogSim?.Invoke($"[bin] kernel flash write failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Bracket-close kernel sniffer. Looks at the current contents of
    /// node.State.DownloadBuffer[0..DownloadCaptureHighWaterMark] - i.e.
    /// the reassembled payload of every $36 that's landed since the most
    /// recent $34 - runs PowerPcSniffer over it, and if it sniffs as
    /// compiled PowerPC code, dumps a tagged "kernel_*.bin" alongside the
    /// per-$36 fragments. Fires at:
    ///   - the next $34 (prior bracket is about to be reallocated),
    ///   - $36 sub-$80 DownloadAndExecute (the explicit handover boundary),
    ///   - session end via EcuExitLogic ($20 / P3C timeout / disconnect).
    ///
    /// Reason is embedded in the filename ("next34" / "exec" / "end") so a
    /// post-mortem read of the captures dir tells you which boundary
    /// flagged the blob.
    ///
    /// Per-$36 .bin fragments still land regardless - this is an additive
    /// tag write, not a replacement.
    /// </summary>
    public static void WriteCompletedBracketIfKernel(EcuNode node, VirtualBus bus, string reason)
    {
        var settings = bus.Capture;
        if (string.IsNullOrEmpty(settings.CaptureDirectory)) return;

        var buf = node.State.DownloadBuffer;
        if (buf is null) return;
        int len = (int)Math.Min((uint)buf.Length, node.State.DownloadCaptureHighWaterMark);
        if (len == 0) return;

        if (!PowerPcSniffer.IsLikelyCode(buf.AsSpan(0, len), out string snifferReason))
        {
            bus.LogSim?.Invoke($"[capture] bracket-close ({reason}) on {node.Name}: not kernel ({snifferReason})");
            return;
        }

        try
        {
            if (node.State.DownloadCaptureSessionTimestamp is null)
                node.State.DownloadCaptureSessionTimestamp = DateTime.Now;

            var tsLocal = node.State.DownloadCaptureSessionTimestamp.Value;
            string ts = tsLocal.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string sessionDir = string.Format(CultureInfo.InvariantCulture,
                "{0}_{1}", Sanitise(node.Name), ts);
            string fullDir = Path.Combine(settings.CaptureDirectory!, sessionDir);
            Directory.CreateDirectory(fullDir);

            uint seq = node.State.DownloadCaptureSequence++;
            uint baseAddr = node.State.DownloadCaptureBaseAddress ?? 0u;
            string fileName = string.Format(CultureInfo.InvariantCulture,
                "{0:D3}_kernel_{1:X8}_{2}_{3}.bin", seq, baseAddr, len, reason);
            string path = Path.Combine(fullDir, fileName);

            File.WriteAllBytes(path, buf.AsSpan(0, len).ToArray());

            bus.LogSim?.Invoke(
                $"[capture] KERNEL ({reason}) on {node.Name}: {len} B @ 0x{baseAddr:X8} " +
                $"[{snifferReason}] -> {path}");
            settings.RaiseCaptureWritten(path);
        }
        catch (Exception ex)
        {
            bus.LogSim?.Invoke($"[capture] kernel write failed: {ex.Message}");
        }
    }

    private static string Sanitise(string raw)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var buf = new char[raw.Length];
        for (int i = 0; i < raw.Length; i++)
            buf[i] = Array.IndexOf(invalid, raw[i]) >= 0 ? '_' : raw[i];
        return new string(buf);
    }
}
