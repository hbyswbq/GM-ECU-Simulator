using Common.PassThru;
using Core.Bus;
using Core.Ecu;
using Core.Services;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.Services;

// Unit coverage for PowerPcSniffer + the bracket-close kernel-capture wiring
// in Service34Handler / Service36Handler / EcuExitLogic. The sniffer's job
// is "tell a SPS kernel apart from a calibration block"; the wiring's job
// is to dump a tagged kernel_*.bin at each of the three boundaries (next
// $34, $36 sub-$80, session end) without disturbing the per-$36 fragments.
public sealed class PowerPcSnifferTests
{
    private const uint Blr = 0x4E800020u;        // book-e return
    private const uint MflrR0 = 0x7C0802A6u;     // book-e function prologue
    private const uint MflrR3 = 0x7C6802A6u;     // mflr r3 - exercises the rD mask
    private const uint StwuR1Neg16 = 0x9421FFF0u;// stwu r1,-16(r1) - prologue
    private const uint Bl = 0x48000005u;         // bl +4 (LK bit set)
    private const uint Nop = 0x60000000u;        // ori 0,0,0 = nop

    private static byte[] Build(params uint[] words)
    {
        var buf = new byte[words.Length * 4];
        for (int i = 0; i < words.Length; i++)
        {
            buf[i * 4 + 0] = (byte)((words[i] >> 24) & 0xFF);
            buf[i * 4 + 1] = (byte)((words[i] >> 16) & 0xFF);
            buf[i * 4 + 2] = (byte)((words[i] >> 8) & 0xFF);
            buf[i * 4 + 3] = (byte)(words[i] & 0xFF);
        }
        return buf;
    }

    private static byte[] PadTo(byte[] core, int totalLength)
    {
        if (core.Length >= totalLength) return core;
        var padded = new byte[totalLength];
        core.CopyTo(padded, 0);
        // Fill tail with nop so the sniffer's denominator-style thresholds
        // see a realistic kernel-sized blob, not just the keystone words.
        for (int i = core.Length; i + 3 < padded.Length; i += 4)
        {
            padded[i + 0] = 0x60;
            padded[i + 1] = 0x00;
            padded[i + 2] = 0x00;
            padded[i + 3] = 0x00;
        }
        return padded;
    }

    [Fact]
    public void Detects_book_e_kernel_with_blr_mflr_stwu()
    {
        // 4 functions worth of recognisable prologue/epilogue. Each "function"
        // is mflr -> stwu -> bl -> ... -> blr, which matches what a real T43
        // SPS kernel looks like at any 16-instruction window.
        var core = Build(
            MflrR0, StwuR1Neg16, Bl, Nop, Blr,
            MflrR3, StwuR1Neg16, Bl, Nop, Blr,
            MflrR0, StwuR1Neg16, Bl, Nop, Blr,
            MflrR0, StwuR1Neg16, Bl, Nop, Blr);
        var blob = PadTo(core, 4096);

        Assert.True(PowerPcSniffer.IsLikelyCode(blob, out string reason));
        Assert.Contains("book-e", reason);
    }

    [Fact]
    public void Rejects_calibration_block_full_of_constants()
    {
        // 4 KB of "table" data - bytes that are NOT a multiple of any of the
        // keystone PowerPC encodings. Real cal blocks look like this: small
        // counts, scaled floats, lookup tables.
        var blob = new byte[4096];
        for (int i = 0; i < blob.Length; i++) blob[i] = (byte)((i * 13 + 0x11) & 0xFF);

        Assert.False(PowerPcSniffer.IsLikelyCode(blob, out string reason));
        Assert.Contains("no match", reason);
    }

    [Fact]
    public void Rejects_blob_below_minimum_size()
    {
        // Even a blob full of blrs is rejected if it's too short to be
        // confidently called code - keeps the false-positive floor low on
        // tiny $36 fragments that happen to start with an opcode-shaped byte.
        var tiny = Build(Blr, Blr, Blr, Blr); // 16 bytes
        Assert.False(PowerPcSniffer.IsLikelyCode(tiny));
    }

    [Fact]
    public void Mflr_rD_mask_catches_any_destination_register()
    {
        // The book-e threshold needs 3 mflr at 4-byte alignment. Use three
        // different rD encodings to prove the 0xFC1FFFFF mask works across
        // them (not just rD=0).
        var core = Build(
            0x7C0802A6u /*mflr r0*/, StwuR1Neg16, Blr,
            0x7C6802A6u /*mflr r3*/, StwuR1Neg16, Blr,
            0x7FE802A6u /*mflr r31*/, StwuR1Neg16, Blr);
        var blob = PadTo(core, 1024);

        Assert.True(PowerPcSniffer.IsLikelyCode(blob, out string reason));
        Assert.Contains("mflr=3", reason);
    }

    [Fact]
    public void Detects_vle_kernel_via_se_blr_se_mflr_e_stwu()
    {
        // VLE kernel approximation: lots of se_blr returns, a handful of
        // se_mflr entry-points, several e_stwu frame allocations, with
        // padding that's not all-zero (zero-pad would inflate se_blr counts
        // since 00 00 sits adjacent to 00 04 only at chosen offsets, but
        // is harmless on a 2-byte scan).
        var blob = new byte[2048];
        // Fill with a non-trivial repeating VLE-shaped instruction so the
        // sniffer sees a code-density profile, not a sparse one.
        for (int i = 0; i + 1 < blob.Length; i += 2)
        {
            // 70 00 is reserved/null in VLE; using it as filler avoids
            // accidentally landing on a real opcode pattern.
            blob[i] = 0x70;
            blob[i + 1] = 0x00;
        }
        // Sprinkle in the keystones at 2-byte boundaries.
        void Put16(int offset, byte hi, byte lo)
        {
            blob[offset] = hi;
            blob[offset + 1] = lo;
        }
        void Put32(int offset, byte b0, byte b1, byte b2, byte b3)
        {
            blob[offset] = b0; blob[offset + 1] = b1; blob[offset + 2] = b2; blob[offset + 3] = b3;
        }
        // 20 se_blr (00 04) - well above sizeThreshold=max(8, 2048/1024=2)
        for (int n = 0; n < 20; n++) Put16(0x40 + n * 16, 0x00, 0x04);
        // 6 se_mflr (00 80)
        for (int n = 0; n < 6; n++) Put16(0x200 + n * 16, 0x00, 0x80);
        // 4 e_stwu r1,disp(r1) (18 21 .. ..)
        for (int n = 0; n < 4; n++) Put32(0x400 + n * 32, 0x18, 0x21, 0xFF, 0xF0);

        Assert.True(PowerPcSniffer.IsLikelyCode(blob, out string reason));
        Assert.Contains("vle", reason);
    }

    // -------- Bracket-close wiring (handler-level) --------

    private static (VirtualBus bus, EcuNode node, ChannelSession ch, string tmp) WireProgrammingReady()
    {
        var bus = new VirtualBus();
        var node = NodeFactory.CreateNode();
        node.State.DownloadAddressByteCount = 4;
        node.State.SecurityUnlockedLevel = 1;
        node.State.NormalCommunicationDisabled = true;
        node.State.ProgrammingModeRequested = true;
        node.State.ProgrammingModeActive = true;
        bus.AddNode(node);
        var ch = new ChannelSession { Id = 1, Protocol = ProtocolID.CAN, Baud = 500_000, Bus = bus };
        node.State.LastEnhancedChannel = ch;
        var tmp = Path.Combine(Path.GetTempPath(), "GmEcuSimKernelSniff_" + Guid.NewGuid().ToString("N"));
        bus.Capture.CaptureDirectory = tmp;
        return (bus, node, ch, tmp);
    }

    private static byte[] BuildDownload36(byte sub, uint address, byte[] data)
    {
        var buf = new byte[6 + data.Length];
        buf[0] = 0x36;
        buf[1] = sub;
        buf[2] = (byte)(address >> 24);
        buf[3] = (byte)(address >> 16);
        buf[4] = (byte)(address >> 8);
        buf[5] = (byte)address;
        data.CopyTo(buf, 6);
        return buf;
    }

    private static byte[] BookEKernelBlob(int totalLength)
    {
        var core = Build(
            MflrR0, StwuR1Neg16, Bl, Nop, Blr,
            MflrR3, StwuR1Neg16, Bl, Nop, Blr,
            MflrR0, StwuR1Neg16, Bl, Nop, Blr);
        return PadTo(core, totalLength);
    }

    [Fact]
    public void Sub_80_exec_dumps_kernel_bin()
    {
        var (bus, node, ch, tmp) = WireProgrammingReady();
        var written = new List<string>();
        bus.Capture.CaptureWritten += p => written.Add(p);

        try
        {
            // $34 then a single $36 sub-$80 carrying a recognisable kernel.
            // The $36 itself dumps a per-fragment .bin; sub-$80 then triggers
            // the bracket-close kernel sniff before the kernel binding takes over.
            Service34Handler.Handle(node, new byte[] { 0x34, 0x00, 0x00, 0x00, 0x10, 0x00 }, ch);
            ch.RxQueue.TryDequeue(out _);

            var blob = BookEKernelBlob(2048);
            Service36Handler.Handle(node, BuildDownload36(0x80, 0x003FB800, blob), ch);
            ch.RxQueue.TryDequeue(out _);

            Assert.Contains(written, p => Path.GetFileName(p).Contains("_kernel_") &&
                                          Path.GetFileName(p).Contains("_exec"));
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void Next_34_after_kernel_bracket_dumps_kernel_bin()
    {
        var (bus, node, ch, tmp) = WireProgrammingReady();
        var written = new List<string>();
        bus.Capture.CaptureWritten += p => written.Add(p);

        try
        {
            // First $34/$36 pair carries a kernel-shaped blob (sub-$00 so
            // no exec handover happens here).
            Service34Handler.Handle(node, new byte[] { 0x34, 0x00, 0x00, 0x00, 0x10, 0x00 }, ch);
            ch.RxQueue.TryDequeue(out _);
            var blob = BookEKernelBlob(2048);
            Service36Handler.Handle(node, BuildDownload36(0x00, 0x003FB800, blob), ch);
            ch.RxQueue.TryDequeue(out _);

            // Second $34 arrives. Bracket close should fire BEFORE the
            // buffer realloc and emit a kernel_*_next34.bin.
            Service34Handler.Handle(node, new byte[] { 0x34, 0x00, 0x00, 0x01, 0x00, 0x00 }, ch);
            ch.RxQueue.TryDequeue(out _);

            Assert.Contains(written, p => Path.GetFileName(p).Contains("_kernel_") &&
                                          Path.GetFileName(p).Contains("_next34"));
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void Calibration_bracket_does_not_dump_kernel_bin()
    {
        var (bus, node, ch, tmp) = WireProgrammingReady();
        var written = new List<string>();
        bus.Capture.CaptureWritten += p => written.Add(p);

        try
        {
            // First bracket is a "calibration" - random-looking constants,
            // no PowerPC structure. Second $34 closes it; sniffer must NOT
            // flag this as a kernel.
            Service34Handler.Handle(node, new byte[] { 0x34, 0x00, 0x00, 0x00, 0x10, 0x00 }, ch);
            ch.RxQueue.TryDequeue(out _);
            var cal = new byte[2048];
            for (int i = 0; i < cal.Length; i++) cal[i] = (byte)((i * 13 + 0x11) & 0xFF);
            Service36Handler.Handle(node, BuildDownload36(0x00, 0x001C0000, cal), ch);
            ch.RxQueue.TryDequeue(out _);

            Service34Handler.Handle(node, new byte[] { 0x34, 0x00, 0x00, 0x01, 0x00, 0x00 }, ch);
            ch.RxQueue.TryDequeue(out _);

            // Per-$36 fragment IS expected; kernel_*.bin is NOT.
            Assert.DoesNotContain(written, p => Path.GetFileName(p).Contains("_kernel_"));
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        }
    }
}
