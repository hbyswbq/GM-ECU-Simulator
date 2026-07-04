using Common.IsoTp;
using Common.PassThru;
using Common.Protocol;
using Core.Bus;
using Core.Ecu;
using Core.Protocol;
using Core.Services;
using Core.Services.Uds;
using Core.Transport;
using EcuSimulator.Tests.TestHelpers;
using Shim.IsoTp;
using Xunit;

namespace EcuSimulator.Tests.Services;

// $35/$36 flash-READ emulation for the two real GM reader tools (see
// Core/Services/ReadEmulation.cs and
// memory/reference_t43_read_kernel_and_powerpcm_read_protocol.md):
//
//   T43    (6Speed.T43 read-kernel) - $36 sub $80 @ 0x003FC430 answers with the
//          $99 "alive" handshake, then each $35 emits a self-contained,
//          NON-standard multi-frame block (SF $75, FF carrying only the 5-byte
//          "$36 00 <addr24>" echo, then CFs streaming the whole block). Verified
//          frame-by-frame on a raw CAN channel - exactly what the tool reads.
//
//   E38E67 (PowerPCM native upload) - $35 RequestUpload -> $75 arms the read,
//          then each $36 returns a STANDARD ISO-TP [$76][seq][1024 flash bytes]
//          block, the cursor auto-advancing. Verified over the Iso15765Channel
//          (FC-aware reassembly), the same surface a J2534 host drives.
public sealed class ReadEmulationTests
{
    private const uint UsdtResp = NodeFactory.UsdtResp;   // $7E8

    // A deterministic flash image: byte i = i & 0xFF, so any slice is trivially
    // predictable and a wrong cursor / off-by-one shows up immediately.
    private static byte ExpectedFlashByte(long i) => (byte)(i & 0xFF);

    private static string WriteRampBin(int length)
    {
        var bytes = new byte[length];
        for (int i = 0; i < length; i++) bytes[i] = ExpectedFlashByte(i);
        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"reademu_{System.Guid.NewGuid():N}.bin");
        System.IO.File.WriteAllBytes(path, bytes);
        return path;
    }

    // The data field (CAN id stripped) of the next raw frame on the channel queue.
    private static byte[] DequeueRaw(ChannelSession ch)
    {
        Assert.True(ch.RxQueue.TryDequeue(out var msg), "expected a frame on the Rx queue");
        return msg!.Data.AsSpan(CanFrame.IdBytes).ToArray();
    }

    // -----------------------------------------------------------------------
    // T43 read-kernel: raw-frame level
    // -----------------------------------------------------------------------

    [Fact]
    public void T43_download_and_execute_at_kernel_address_emits_99_handshake()
    {
        var node = NodeFactory.CreateNode();
        node.ReadFamily = ReadKernelFamily.T43;
        node.State.SecurityUnlockedLevel = 1;
        // Simulate the state after $34 + the kernel-load $36 stream.
        node.State.DownloadActive = true;
        node.State.DownloadBuffer = new byte[16];
        var ch = NodeFactory.CreateChannel();

        // $36 sub $80 DownloadAndExecute @ 0x003FC430 (4-byte address, the default).
        bool ok = Service36Handler.Handle(node,
            new byte[] { Service.TransferData, 0x80, 0x00, 0x3F, 0xC4, 0x30 }, ch);

        Assert.True(ok);
        Assert.True(node.State.T43ReadKernelActive);
        Assert.Equal(new byte[] { 0x01, 0x99 }, DequeueRaw(ch));   // SF, len 1, data $99
        TestFrame.AssertEmpty(ch);   // NOT the generic $76
    }

    [Fact]
    public void T43_download_and_execute_elsewhere_is_a_normal_write_kernel()
    {
        var node = NodeFactory.CreateNode();
        node.ReadFamily = ReadKernelFamily.T43;
        node.State.SecurityUnlockedLevel = 1;
        node.State.DownloadActive = true;
        node.State.DownloadBuffer = new byte[16];
        var ch = NodeFactory.CreateChannel();

        // A write-kernel landing zone (not the read-kernel address) -> generic $76,
        // no read-kernel arming.
        bool ok = Service36Handler.Handle(node,
            new byte[] { Service.TransferData, 0x80, 0x00, 0x3F, 0xAF, 0xE0 }, ch);

        Assert.True(ok);
        Assert.False(node.State.T43ReadKernelActive);
        Assert.Equal(new byte[] { 0x76 }, TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void T43_read_block_frames_stream_the_flash_slice_and_advance_the_cursor()
    {
        const int BlockSize = 0x0800;   // 2048, exactly what 6Speed.T43 requests
        string bin = WriteRampBin(3 * BlockSize);
        try
        {
            var node = NodeFactory.CreateNode();
            node.ReadFamily = ReadKernelFamily.T43;
            node.FlashBinPath = bin;
            node.State.SecurityUnlockedLevel = 1;
            node.State.T43ReadKernelActive = true;
            var ch = NodeFactory.CreateChannel();

            // Two consecutive blocks; the tool always sends address 0 and the ECU
            // tracks the real offset in the cursor.
            AssertT43Block(node, ch, BlockSize, expectedOffset: 0);
            Assert.Equal(BlockSize, node.State.UploadCursor);
            AssertT43Block(node, ch, BlockSize, expectedOffset: BlockSize);
            Assert.Equal(2 * BlockSize, node.State.UploadCursor);
        }
        finally { System.IO.File.Delete(bin); }
    }

    // Send one T43 $35 read command and assert the whole frame sequence: SF $75,
    // the non-standard First Frame, and the Consecutive Frames carrying exactly
    // `size` flash bytes from `expectedOffset`.
    private static void AssertT43Block(EcuNode node, ChannelSession ch, int size, long expectedOffset)
    {
        // "35 00 <sizeHi sizeLo> 00 00 00" - address 0 on the wire, as the tool sends.
        bool ok = Service35Handler.Handle(node, new byte[]
        {
            Service.RequestUpload, 0x00, (byte)(size >> 8), (byte)(size & 0xFF), 0x00, 0x00, 0x00,
        }, ch);
        Assert.True(ok);

        // 1) Single-frame positive: 01 75.
        Assert.Equal(new byte[] { 0x01, Service.Positive(Service.RequestUpload) }, DequeueRaw(ch));

        // 2) First Frame: PCI = size + 0x1005, then 36 00 <addr24=0>. Seven bytes,
        //    NO flash data (the tool ignores the FF payload).
        int pci = size + 0x1005;
        Assert.Equal(
            new byte[] { (byte)(pci >> 8), (byte)(pci & 0xFF), Service.TransferData, 0x00, 0x00, 0x00, 0x00 },
            DequeueRaw(ch));

        // 3) Consecutive frames: seq 0x21,0x22,..0x2F,0x20,.. carrying the block.
        var assembled = new System.Collections.Generic.List<byte>(size);
        int expectedCfs = (size + 6) / 7;   // ceil(size / 7)
        for (int i = 0; i < expectedCfs; i++)
        {
            var cf = DequeueRaw(ch);
            Assert.Equal(8, cf.Length);                                   // full 8-byte frame
            Assert.Equal((byte)(0x20 | ((i + 1) & 0x0F)), cf[0]);         // CF sequence PCI
            assembled.AddRange(cf.AsSpan(1).ToArray());
        }
        TestFrame.AssertEmpty(ch);   // nothing beyond the block

        // The tool keeps only `size` bytes (the last CF is zero-padded); they must
        // be the flash slice at the running offset.
        for (int j = 0; j < size; j++)
            Assert.Equal(ExpectedFlashByte(expectedOffset + j), assembled[j]);
    }

    [Fact]
    public void Request_upload_while_locked_is_refused_with_security_access_denied()
    {
        var node = NodeFactory.CreateNode();
        node.ReadFamily = ReadKernelFamily.T43;
        node.State.T43ReadKernelActive = true;
        // SecurityUnlockedLevel stays 0 (locked).
        var ch = NodeFactory.CreateChannel();

        bool ok = Service35Handler.Handle(node,
            new byte[] { Service.RequestUpload, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00 }, ch);

        Assert.False(ok);
        Assert.Equal(new byte[] { Service.NegativeResponse, Service.RequestUpload, Nrc.SecurityAccessDenied },
                     TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void Check_memory_validate_during_upload_crcs_the_read_image()
    {
        const int Size = 0x400;   // 1 KiB slice to validate
        string bin = WriteRampBin(2 * Size);
        try
        {
            var node = NodeFactory.CreateNode();
            node.FlashBinPath = bin;
            node.State.SecurityUnlockedLevel = 1;
            node.State.UploadActive = true;   // a $35/$36 read is in progress
            var ch = NodeFactory.CreateChannel();

            // PowerPCM's post-read validate: $31 01 04 <start(4)> <size(4)>. The
            // declared start (a nominal OS base) is ignored - the CRC covers the
            // read image from flash 0.
            bool ok = Service31Handler.Handle(node, new byte[]
            {
                0x31, 0x01, 0x04, 0x01,                              // sid, sub, routineId 0x0401
                0x01, 0x00, 0x00, 0x00,                              // start = 0x01000000 (4 BE, ignored)
                0x00, 0x00, (byte)(Size >> 8), (byte)(Size & 0xFF),  // size = 0x00000400 (4 BE)
            }, ch);
            Assert.True(ok);

            // Expected CRC-16/CCITT-FALSE over the same bytes the upload served.
            var expectedImage = new byte[Size];
            for (int i = 0; i < Size; i++) expectedImage[i] = ExpectedFlashByte(i);
            ushort crc = Common.Protocol.Crc16Ccitt.Compute(expectedImage);

            Assert.Equal(new byte[] { 0x71, 0x04, (byte)(crc >> 8), (byte)(crc & 0xFF) },
                         TestFrame.DequeueSingleFrameUsdt(ch));
        }
        finally { System.IO.File.Delete(bin); }
    }

    // -----------------------------------------------------------------------
    // E38/E67 native upload: over the FC-aware Iso15765Channel
    // -----------------------------------------------------------------------

    [Fact]
    public void Native_upload_serves_sequential_1024_byte_blocks_from_flash()
    {
        const int Block = ReadEmulation.UploadBlockBytes;   // 1024
        string bin = WriteRampBin(4 * Block);
        try
        {
            var (node, iso) = SetupIso15765();
            node.ReadFamily = ReadKernelFamily.E38E67;
            node.FlashBinPath = bin;
            node.State.SecurityUnlockedLevel = 1;

            // $35 RequestUpload (PowerPCM sends "35 00 20 00 00") -> bare $75.
            Assert.Equal(new byte[] { Service.Positive(Service.RequestUpload) },
                         SendAndReceive(iso, new byte[] { Service.RequestUpload, 0x00, 0x20, 0x00, 0x00 }));
            Assert.True(node.State.UploadActive);

            // Each $36 "36 00 00 00 00 00" -> [76][seq][1024 flash bytes], cursor advancing.
            for (int block = 0; block < 3; block++)
            {
                var resp = SendAndReceive(iso,
                    new byte[] { Service.TransferData, 0x00, 0x00, 0x00, 0x00, 0x00 });
                Assert.Equal(2 + Block, resp.Length);
                Assert.Equal(Service.Positive(Service.TransferData), resp[0]);   // $76
                for (int j = 0; j < Block; j++)
                    Assert.Equal(ExpectedFlashByte((long)block * Block + j), resp[2 + j]);
            }
            Assert.Equal(3L * Block, node.State.UploadCursor);
        }
        finally { System.IO.File.Delete(bin); }
    }

    [Fact]
    public void Native_upload_past_end_of_bin_reads_zeros()
    {
        const int Block = ReadEmulation.UploadBlockBytes;
        string bin = WriteRampBin(Block / 2);   // shorter than one block
        try
        {
            var (node, iso) = SetupIso15765();
            node.FlashBinPath = bin;
            node.State.SecurityUnlockedLevel = 1;

            SendAndReceive(iso, new byte[] { Service.RequestUpload, 0x00, 0x20, 0x00, 0x00 });
            var resp = SendAndReceive(iso, new byte[] { Service.TransferData, 0x00, 0x00, 0x00, 0x00, 0x00 });

            Assert.Equal(2 + Block, resp.Length);
            for (int j = 0; j < Block; j++)
            {
                byte expected = j < Block / 2 ? ExpectedFlashByte(j) : (byte)0x00;
                Assert.Equal(expected, resp[2 + j]);
            }
        }
        finally { System.IO.File.Delete(bin); }
    }

    // ---- Iso15765Channel harness (mirrors ProgrammingSequenceTests) ----

    private static (EcuNode node, Iso15765Channel iso) SetupIso15765()
    {
        var bus = new VirtualBus();
        var node = NodeFactory.CreateNode();
        bus.AddNode(node);
        // $35/$36 flash-read is a kernel-mode service (both tools boot-load a kernel
        // first), so put the node in kernel mode - the state after a $36 sub $80.
        node.EnterKernelMode(ProtocolStacks.KernelBindingFor(node));

        var ch = new ChannelSession
        {
            Id = 1,
            Protocol = ProtocolID.ISO15765,
            Baud = 500_000,
            Bus = bus,
        };
        var iso = new Iso15765Channel(new IsoTpTimingParameters());
        iso.BusEgress = frame => bus.DispatchHostTx(frame, ch);
        ch.IsoChannel = iso;
        ch.IsoChannelInbound = (canId, frame) => iso.OnInboundCanFrame(canId, frame.AsSpan(4));
        iso.AddFilter(new Iso15765Channel.IsoFilter
        {
            Id = 1,
            MaskCanId = 0xFFFFFFFF,
            PatternCanId = UsdtResp,
            FlowCtlCanId = NodeFactory.PhysReq,
            Format = AddressFormat.Normal,
        });
        return (node, iso);
    }

    private static byte[] SendAndReceive(Iso15765Channel iso, byte[] request)
    {
        var begin = iso.BeginTransmit(NodeFactory.PhysReq, request);
        Assert.True(begin.Started, "BeginTransmit failed - no FlowControl filter?");
        iso.BusEgress!(begin.CanFrame!);
        iso.EndTransmit(begin.Filter!);
        Assert.True(iso.ReassembledPayloadQueue.TryDequeue(out var msg),
            $"no response from ECU for request SID 0x{request[0]:X2}");
        return msg!.Data.AsSpan(4).ToArray();
    }
}
