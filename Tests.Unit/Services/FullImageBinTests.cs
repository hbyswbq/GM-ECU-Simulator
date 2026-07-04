using Common.PassThru;
using Core.Bus;
using Core.Ecu;
using Core.Services;
using Core.Services.Uds;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.Services;

// Coverage for the consolidated full-image bin writer (BinOutputDirectory),
// the "complete bin per programming session" feature that sits alongside the
// per-$36 fragment captures:
//   - SPS $34/$36 flow: EcuExitLogic unions every $31-declared erase region
//     into one absolutely-positioned image (gaps stay $FF).
//   - PcmHammer kernel flow: EcuExitLogic dumps NodeState.KernelFlash verbatim
//     (it's already a positioned full image).
//
// The per-region and fragment writers live in FlashRegionCaptureTests /
// BootloaderCaptureTests; this file is only the consolidated image.
public sealed class FullImageBinTests
{
    private static (VirtualBus bus, EcuNode node, ChannelSession ch) Wire()
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
        return (bus, node, ch);
    }

    /// <summary>$36 sub $00 download with a 4-byte absolute address.</summary>
    private static byte[] BuildDownload(uint address, byte[] data)
    {
        var buf = new byte[6 + data.Length];
        buf[0] = 0x36;
        buf[1] = 0x00;
        buf[2] = (byte)((address >> 24) & 0xFF);
        buf[3] = (byte)((address >> 16) & 0xFF);
        buf[4] = (byte)((address >> 8) & 0xFF);
        buf[5] = (byte)(address & 0xFF);
        data.CopyTo(buf, 6);
        return buf;
    }

    private static void Erase(EcuNode node, ChannelSession ch, uint addr, int size)
    {
        Service31Handler.Handle(node, new byte[]
        {
            0x31, 0x01, 0xFF, 0x00,
            (byte)(addr >> 24), (byte)(addr >> 16), (byte)(addr >> 8), (byte)addr,
            0x00, 0x00, (byte)(size >> 8), (byte)size,
        }, ch);
        ch.RxQueue.TryDequeue(out _);
    }

    [Fact]
    public void Sps_union_places_each_segment_at_its_absolute_offset()
    {
        // Two erase regions with a gap between them. Each gets a distinctive
        // $36 write at its base. The consolidated image must span both, place
        // each segment at (absoluteAddress - lo), and leave the gap at $FF.
        var (bus, node, ch) = Wire();
        var tmp = Path.Combine(Path.GetTempPath(), "GmEcuSimBinTest_" + Guid.NewGuid().ToString("N"));
        bus.Capture.BinOutputDirectory = tmp;   // CaptureDirectory deliberately left null: dirs are independent.
        var written = new List<string>();
        bus.Capture.CaptureWritten += p => written.Add(p);

        const uint region1 = 0x001C0000;
        const uint region2 = 0x001C0400;   // 0x300 gap after region1's 0x100
        const int  regionSize = 0x100;

        try
        {
            Erase(node, ch, region1, regionSize);
            Erase(node, ch, region2, regionSize);

            // One $34 sized generously; the anchor/rebase is per-DownloadBuffer
            // and doesn't affect the absolute-addressed region mirror.
            Service34Handler.Handle(node, new byte[] { 0x34, 0x00, 0x00, 0x01, 0x00, 0x00 }, ch);
            ch.RxQueue.TryDequeue(out _);

            var seg1 = new byte[16];
            for (int i = 0; i < 16; i++) seg1[i] = (byte)(0xA0 + i);
            var seg2 = new byte[16];
            for (int i = 0; i < 16; i++) seg2[i] = (byte)(0xB0 + i);

            Service36Handler.Handle(node, BuildDownload(region1, seg1), ch);
            ch.RxQueue.TryDequeue(out _);
            Service36Handler.Handle(node, BuildDownload(region2, seg2), ch);
            ch.RxQueue.TryDequeue(out _);

            EcuExitLogic.Run(node, bus.Scheduler, ch);

            var imageFile = written.SingleOrDefault(p => Path.GetFileName(p).Contains("sps-image_"));
            Assert.NotNull(imageFile);
            byte[] image = File.ReadAllBytes(imageFile!);

            // lo = region1, hi = region2 + size -> span 0x500.
            const int expectedSize = (int)(region2 - region1) + regionSize;   // 0x500
            Assert.Equal(expectedSize, image.Length);

            // Segment 1 at offset 0.
            for (int i = 0; i < 16; i++) Assert.Equal(seg1[i], image[i]);
            // Rest of region1 + the whole gap stays $FF.
            for (int i = 16; i < (int)(region2 - region1); i++) Assert.Equal(0xFF, image[i]);
            // Segment 2 at its absolute offset (region2 - lo = 0x400).
            int seg2Off = (int)(region2 - region1);
            for (int i = 0; i < 16; i++) Assert.Equal(seg2[i], image[seg2Off + i]);
            // Tail of region2 stays $FF.
            for (int i = seg2Off + 16; i < expectedSize; i++) Assert.Equal(0xFF, image[i]);
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void Pcmhammer_kernel_flash_dumped_as_full_positioned_image()
    {
        // The PcmHammer kernel's own $36 writes land straight in KernelFlash at
        // their absolute address. The consolidated writer dumps that buffer
        // verbatim - a full positioned image, $FF where nothing was written.
        var (bus, node, ch) = Wire();
        var tmp = Path.Combine(Path.GetTempPath(), "GmEcuSimBinTest_" + Guid.NewGuid().ToString("N"));
        bus.Capture.BinOutputDirectory = tmp;
        var written = new List<string>();
        bus.Capture.CaptureWritten += p => written.Add(p);

        try
        {
            // Kernel $36 write: 36 <ct=00> <len2> <addr3> <data..> <sum2>.
            // Non-const so the (byte) casts below truncate at runtime rather
            // than tripping CS0221 on a compile-time constant conversion.
            uint addr = 0x001000;
            byte[] data = { 0xDE, 0xAD, 0xBE, 0xEF };
            ushort sum = 0;
            foreach (var b in data) sum += b;
            var frame = new byte[]
            {
                0x36, 0x00,
                (byte)(data.Length >> 8), (byte)data.Length,
                (byte)(addr >> 16), (byte)(addr >> 8), (byte)addr,
                data[0], data[1], data[2], data[3],
                (byte)(sum >> 8), (byte)sum,
            };
            Assert.True(PcmHammerKernel.HandleWrite(node, frame, ch));
            ch.RxQueue.TryDequeue(out _);

            Assert.NotNull(node.State.KernelFlash);   // lazily allocated by the write above
            int flashLen = node.State.KernelFlash!.Length;

            EcuExitLogic.Run(node, bus.Scheduler, ch);

            var flashFile = written.SingleOrDefault(p => Path.GetFileName(p).Contains("kernel-flash_"));
            Assert.NotNull(flashFile);
            byte[] image = File.ReadAllBytes(flashFile!);

            // Full 2 MiB address space, data at its absolute offset, $FF elsewhere.
            Assert.Equal(flashLen, image.Length);
            Assert.Equal(0xFF, image[0]);
            for (int i = 0; i < data.Length; i++) Assert.Equal(data[i], image[(int)addr + i]);
            Assert.Equal(0xFF, image[(int)addr + data.Length]);

            // No SPS image written on this flow (no $31 erase regions).
            Assert.DoesNotContain(written, p => Path.GetFileName(p).Contains("sps-image_"));
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void No_bin_directory_writes_no_full_image_even_with_regions_or_kernel()
    {
        // Unit-test default: BinOutputDirectory null -> both writers no-op even
        // when a region and a kernel-flash buffer are present. (WPF sets the
        // directory unconditionally on startup.)
        var (bus, node, ch) = Wire();
        Assert.Null(bus.Capture.BinOutputDirectory);
        node.State.CapturedFlashRegions.Add(new FlashEraseRegion(0x001C0000, 0x100));
        node.State.KernelFlash = new byte[0x200000];

        // No throw, no disk side effects.
        EcuExitLogic.Run(node, bus.Scheduler, ch);
    }

    [Fact]
    public void Session_with_no_regions_and_no_kernel_writes_no_full_image()
    {
        var (bus, node, ch) = Wire();
        var tmp = Path.Combine(Path.GetTempPath(), "GmEcuSimBinTest_" + Guid.NewGuid().ToString("N"));
        bus.Capture.BinOutputDirectory = tmp;
        var written = new List<string>();
        bus.Capture.CaptureWritten += p => written.Add(p);

        try
        {
            EcuExitLogic.Run(node, bus.Scheduler, ch);
            Assert.DoesNotContain(written, p => Path.GetFileName(p).Contains("sps-image_"));
            Assert.DoesNotContain(written, p => Path.GetFileName(p).Contains("kernel-flash_"));
            Assert.False(Directory.Exists(tmp));   // dir only created when something is written
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        }
    }
}
