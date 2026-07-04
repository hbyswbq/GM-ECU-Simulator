using Common.PassThru;
using Common.Protocol;
using Core.Bus;
using Core.Ecu;
using Core.Transport;

namespace Core.Services;

// Flash-READ ($35 RequestUpload / $36 upload) wire emission for the two real GM
// reader tools. The high-level $35 entry is Service35Handler; the framing lives
// here so the T43 and E38/E67 dialects share one flash source (EcuNode.CopyFlash)
// and one raw-frame emitter. Full protocol reverse-engineering is in
// memory/reference_t43_read_kernel_and_powerpcm_read_protocol.md and the T43
// read-kernel disassembly at ..\6Speed.T43\extracted_blobs\_readkernel.asm.
//
//   E38E67 (PowerPCM_Flasher native upload): $35 arms UploadActive; each $36 is
//     answered by SendUploadBlock with a STANDARD ISO-TP message
//     [$76][seq][1024 flash bytes] - the tool's GMLAN_Read_FF reassembles it
//     with a normal FF/FC/CF handshake (it sends the 0x30 flow control itself),
//     so the node fragmenter does the segmentation.
//
//   T43 (6Speed.T43 read-kernel): $36 sub $80 DownloadAndExecute @ 0x003FC430
//     boots the kernel, which SendT43KernelAlive announces with SF [$01][$99].
//     Each $35 is then answered by SendT43Block, which emits the exact frame
//     sequence the kernel produces - and it is NON-STANDARD ISO-TP: the First
//     Frame carries only the 5-byte "$36 00 <addr24>" echo (7-byte frame, no
//     flash data), and ALL block bytes ride in Consecutive Frames. The tool
//     discards the FF payload and assembles the block purely from the CFs, so
//     the node fragmenter (which would pack 6 bytes into the FF and shift the
//     dump by one byte) can't be used - we enqueue raw CAN frames directly and
//     ignore the tool's flow control (the frames are already queued FIFO).
public static class ReadEmulation
{
    /// <summary>The 6Speed.T43 read-kernel's fixed exec / landing address ($36 sub $80 startingAddress).</summary>
    public const uint T43ReadKernelEntryAddress = 0x003FC430;

    /// <summary>Bytes of flash per E38/E67 native $36 upload block (PowerPCM's fixed chunk).</summary>
    public const int UploadBlockBytes = 1024;

    // Flash bytes per Consecutive Frame (normal ISO-TP addressing: 1 PCI byte + 7 data).
    private const int CfDataBytes = 7;

    // The T43 kernel's First-Frame PCI is (blockSize + 0x1005): 0x1000 = FF marker,
    // + 5 = the "$36 00 <addr24>" echo bytes that precede the block in the declared
    // ISO-TP length. The high nibble stays 0x1 (a valid FF) only while
    // blockSize + 5 <= 0xFFF, so cap the block below that.
    private const int T43FfPciBias = 0x1005;

    /// <summary>Largest $35 block the T43 path will serve, bounded so the First-Frame length stays a valid 12-bit ISO-TP FF.</summary>
    public const int MaxT43BlockBytes = 0x0FFF - 5;   // 0x0FFA

    /// <summary>
    /// Emit the T43 read-kernel "alive" handshake: single frame [$01][$99] on the
    /// ECU's USDT response id. Sent in place of the $36 sub $80 positive ($76) when
    /// the 6Speed read-kernel is DownloadAndExecute'd - control has already passed
    /// to the kernel, whose first CAN TX is this frame (disasm 0x3FC514).
    /// </summary>
    public static void SendT43KernelAlive(EcuNode node, ChannelSession ch)
        => SendRaw(ch, node.UsdtResponseCanId, [0x01, 0x99]);

    /// <summary>
    /// Answer a T43 read-kernel $35 with one self-contained multi-frame block of
    /// <paramref name="size"/> flash bytes, starting at the node's running
    /// UploadCursor (which it then advances). <paramref name="echoAddr"/> is the
    /// 24-bit address echoed back in the First Frame (the tool sends 0 every block
    /// and tracks position on its side; the ECU tracks the real offset in the
    /// cursor). Emits SF [$01][$75], then FF [pciHi][pciLo][$36][$00][addr24], then
    /// Consecutive Frames streaming the block - raw frames, exact kernel framing.
    /// </summary>
    public static void SendT43Block(EcuNode node, ChannelSession ch, int size, uint echoAddr)
    {
        uint txId = node.UsdtResponseCanId;

        // 1) Single-frame positive response: 01 75.
        SendRaw(ch, txId, [0x01, Service.Positive(Service.RequestUpload)]);

        // 2) First Frame. PCI = size + 0x1005; data = 36 00 <addr24> (5 bytes ->
        //    a 7-byte frame). No flash bytes here: the tool ignores the FF payload.
        int pci = size + T43FfPciBias;
        SendRaw(ch, txId,
        [
            (byte)(pci >> 8), (byte)(pci & 0xFF),
            Service.TransferData, 0x00,
            (byte)(echoAddr >> 16), (byte)(echoAddr >> 8), (byte)echoAddr,
        ]);

        // 3) Consecutive frames carrying the whole block. Read the flash slice at
        //    the running cursor (zero-filled past the bin / with no bin loaded).
        long cursor = node.State.UploadCursor;
        var block = new byte[size];
        node.CopyFlash(cursor, block);

        int seq = 1;   // CF sequence number 1..15,0,1.. -> PCI 0x21..0x2F,0x20,..
        for (int off = 0; off < size; off += CfDataBytes)
        {
            int n = Math.Min(CfDataBytes, size - off);
            // Always an 8-byte frame (PCI + 7 data), zero-padded on the final
            // partial CF - matching the kernel, which sends full frames and lets
            // the tester take only the declared-length tail.
            var cf = new byte[1 + CfDataBytes];
            cf[0] = (byte)(0x20 | (seq & 0x0F));
            block.AsSpan(off, n).CopyTo(cf.AsSpan(1));
            SendRaw(ch, txId, cf);
            seq = (seq + 1) & 0x0F;
        }

        node.State.UploadCursor = cursor + size;
    }

    /// <summary>
    /// Answer an E38/E67 native upload $36 with a standard ISO-TP message
    /// [$76][blockSeq][UploadBlockBytes flash bytes] from the running cursor,
    /// then advance the cursor. The node fragmenter segments it (FF/FC/CF) and the
    /// tool reassembles; blockSeq is cosmetic (PowerPCM ignores it).
    /// </summary>
    public static void SendUploadBlock(EcuNode node, ChannelSession ch)
    {
        long cursor = node.State.UploadCursor;
        var payload = new byte[2 + UploadBlockBytes];
        payload[0] = Service.Positive(Service.TransferData);          // 0x76
        payload[1] = (byte)((cursor / UploadBlockBytes) & 0xFF);      // block sequence (cosmetic)
        node.CopyFlash(cursor, payload.AsSpan(2));
        node.State.Fragmenter.SendNow(ch, node.UsdtResponseCanId, payload);
        node.State.UploadCursor = cursor + UploadBlockBytes;
    }

    // Enqueue one raw CAN frame (id + data field, normal addressing) straight onto
    // the channel RX queue - the same shape IsoTpFragmenter.EmitFrame produces,
    // used where the standard fragmenter's framing doesn't fit (T43's non-standard
    // FF, and the tiny fixed handshake frames).
    private static void SendRaw(ChannelSession ch, uint canId, byte[] dataField)
    {
        var frame = new byte[CanFrame.IdBytes + dataField.Length];
        CanFrame.WriteId(frame, canId);
        dataField.CopyTo(frame.AsSpan(CanFrame.IdBytes));
        ch.EnqueueRx(new PassThruMsg { ProtocolID = ProtocolID.CAN, Data = frame });
    }
}
