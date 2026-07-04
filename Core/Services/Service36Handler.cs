using Common.Protocol;
using Core.Bus;
using Core.Ecu;
using Core.Protocol;

namespace Core.Services;

// $36 TransferData. GMW3110-2010 §8.13 (p161-167).
//
// Request: SID $36 + sub-function ($00 Download or $80 DownloadAndExecute)
//          + startingAddress (2..4 bytes BE) + dataRecord (variable).
// Positive response: $76 (no data parameters).
//
// NRCs (§8.13.4, Table 142):
//   $12 SFNS-IF   - sub-function invalid, or length too short for sub-function
//   $22 CNCRSE    - TransferData_Allowed = NO (i.e., $34 not run first)
//   $31 ROOR      - host wrote before its own anchor, or buffer cap exceeded
//   $78 RCR-RP    - cannot process within P2C (we never need this)
//   $83 VOLTRNG   - voltage out of range (we never need this)
//   $85 PROGFAIL  - program/erase/CRC failure (we never need this)
//
// The startingAddress byte-count is fixed per ECU and must match across all
// $36 calls in the same session (§8.13.2 Note 1). We use the value frozen by
// the most recent $34 in NodeState.DownloadAddressByteCount.
//
// Address model: GMW3110 $34 carries only the declared size; it does NOT
// carry an absolute address. Real GM hosts (DPS, TIS2WEB, 6Speed.T43) put an
// absolute RAM/flash address on every $36 (e.g. $003FC430 for an SPS kernel
// landing zone). To make those work without per-host configuration we always
// anchor on the FIRST $36's address - subsequent $36s are stored at
// (startingAddress - baseAddr). Hosts that send offset 0 / sequential
// in-buffer offsets degenerate cleanly (anchor = 0, offset = address).
// A host writing BEFORE its own anchor address is the only NRC $31 path
// we still emit - that scenario doesn't appear in any observed real flow
// and the alternative (silently shifting the buffer backward) would mask
// genuine host bugs.
public static class Service36Handler
{
    /// <summary>Safety cap on the sink buffer. 64 MiB is well past any real
    /// GM SPS bootloader/calibration payload (~10s of KB to a few MB) and
    /// prevents a malformed host address from triggering an OOM allocation.</summary>
    public const int MaxDownloadBufferBytes = 64 * 1024 * 1024;

    /// <summary>Returns true if a positive response was sent, false if an NRC was sent.</summary>
    public static bool Handle(EcuNode node, ReadOnlySpan<byte> usdtPayload, ChannelSession ch)
    {
        // Flash-READ path (E38/E67 native upload): once a $35 RequestUpload has
        // armed UploadActive, every $36 is a "give me the next block" request
        // (PowerPCM sends a bare "36 00 00 00 00 00" and lets the ECU auto-advance
        // its read cursor). Serve the block from flash before any of the $34
        // download-write bookkeeping below - the two modes are mutually exclusive.
        if (usdtPayload.Length >= 1 && usdtPayload[0] == Service.TransferData
            && node.State.UploadActive)
        {
            ReadEmulation.SendUploadBlock(node, ch);
            return true;
        }

        int addrBytes = node.State.DownloadAddressByteCount;
        // Minimum length: 1 SID + 1 sub + addrBytes + 1 data byte (sub $00 requires data).
        // For sub $80, data may be empty.
        if (usdtPayload.Length < 2 + addrBytes || usdtPayload[0] != Service.TransferData)
        {
            ServiceUtil.EnqueueNrc(node, ch, Service.TransferData, Nrc.SubFunctionNotSupportedInvalidFormat);
            return false;
        }

        byte sub = usdtPayload[1];
        if (sub != 0x00 && sub != 0x80)
        {
            // §8.13.2.1: only $00 (Download) and $80 (DownloadAndExecute) are defined.
            ServiceUtil.EnqueueNrc(node, ch, Service.TransferData, Nrc.SubFunctionNotSupportedInvalidFormat);
            return false;
        }

        // §8.13.4: $00 requires at least one data byte.
        if (sub == 0x00 && usdtPayload.Length == 2 + addrBytes)
        {
            ServiceUtil.EnqueueNrc(node, ch, Service.TransferData, Nrc.SubFunctionNotSupportedInvalidFormat);
            return false;
        }

        if (!node.State.DownloadActive || node.State.DownloadBuffer is null)
        {
            // §8.13.4 NRC $22: $34 not run / TransferData_Allowed = NO.
            ServiceUtil.EnqueueNrc(node, ch, Service.TransferData, Nrc.ConditionsNotCorrectOrSequenceError);
            return false;
        }

        // Decode startingAddress as a BE unsigned integer.
        uint startingAddress = 0;
        for (int i = 0; i < addrBytes; i++)
            startingAddress = (startingAddress << 8) | usdtPayload[2 + i];

        var dataRecord = usdtPayload.Slice(2 + addrBytes);

        // Anchor on the first $36's address. Subsequent $36s are stored at
        // (startingAddress - baseAddr). This is the only model that works
        // for real DPS/TIS2WEB sessions (which send absolute addresses) AND
        // for offset-based hosts (anchor = 0, offset = address).
        node.State.DownloadCaptureBaseAddress ??= startingAddress;
        uint baseAddr = node.State.DownloadCaptureBaseAddress.Value;

        if (startingAddress < baseAddr)
        {
            // Host wrote BEFORE the address it first used. We don't try to
            // rebase silently - that would mask a host bug. No observed
            // GM flow does this.
            ServiceUtil.EnqueueNrc(node, ch, Service.TransferData, Nrc.RequestOutOfRange);
            return false;
        }

        long offset = startingAddress - baseAddr;
        long endExclusive = offset + dataRecord.Length;
        if (endExclusive > MaxDownloadBufferBytes)
        {
            // Hard safety cap - reject rather than allocating multi-GB.
            ServiceUtil.EnqueueNrc(node, ch, Service.TransferData, Nrc.RequestOutOfRange);
            return false;
        }

        if (endExclusive > node.State.DownloadBuffer.Length)
        {
            // Grow with doubling headroom so a contiguous transfer doesn't
            // realloc on every $36. Cap at MaxDownloadBufferBytes.
            long target = Math.Min(MaxDownloadBufferBytes,
                                   Math.Max(endExclusive, node.State.DownloadBuffer.Length * 2L));
            var grown = new byte[target];
            Buffer.BlockCopy(node.State.DownloadBuffer, 0, grown, 0, node.State.DownloadBuffer.Length);
            node.State.DownloadBuffer = grown;
        }

        if (dataRecord.Length > 0)
        {
            dataRecord.CopyTo(node.State.DownloadBuffer.AsSpan((int)offset));
            node.State.DownloadBytesReceived += (uint)dataRecord.Length;
            if ((uint)endExclusive > node.State.DownloadCaptureHighWaterMark)
                node.State.DownloadCaptureHighWaterMark = (uint)endExclusive;

            // Per-$36 immediate disk write. No-op when no CaptureDirectory
            // is configured (unit tests that don't opt in get no disk side
            // effects). The buffer-reassembly above is kept for unit-test
            // introspection regardless.
            if (ch.Bus is not null)
                BootloaderCaptureWriter.WriteEachTransferData(node, ch.Bus, startingAddress, dataRecord);

            // Flash-region mirror: if a $31 EraseMemoryByAddress earlier in
            // this session declared a region that fully contains this $36's
            // [startingAddress, endAddress) range, copy the dataRecord into
            // the region's $FF-backed buffer. EcuExitLogic dumps one .bin
            // per region at session end. Partial overlaps are ignored - the
            // kernel only writes inside declared regions after erasing, so
            // an out-of-region write is a kernel bug we don't want to
            // silently truncate.
            long endAddrExclusive = startingAddress + dataRecord.Length;
            foreach (var region in node.State.CapturedFlashRegions)
            {
                if (startingAddress >= region.StartAddress
                    && endAddrExclusive <= (long)region.StartAddress + region.Size)
                {
                    int regionOffset = (int)(startingAddress - region.StartAddress);
                    dataRecord.CopyTo(region.Buffer.AsSpan(regionOffset));
                    region.BytesWritten += (uint)dataRecord.Length;
                }
            }
        }

        // Sub $80 DownloadAndExecute hands the bus to the just-uploaded SPS
        // kernel. Replace the ECU's stacks with the transient kernel binding so
        // subsequent $31/etc. requests dispatch through the UDS-kernel stack;
        // EcuExitLogic restores the baseline on $20 / P3C timeout. Done before the
        // positive response so wire ordering matches a real kernel handover.
        if (sub == 0x80)
        {
            // Explicit kernel-handover boundary: whatever the host just
            // finished pushing IS the kernel by definition. Sniff & dump a
            // tagged copy alongside the per-$36 fragments before the
            // kernel binding takes over.
            if (ch.Bus is not null)
                BootloaderCaptureWriter.WriteCompletedBracketIfKernel(node, ch.Bus, "exec");
            node.EnterKernelMode(ProtocolStacks.KernelBindingFor(node));

            // 6Speed.T43 read-kernel handover: when a T43-family ECU has just been
            // handed the read-kernel (exec @ 0x003FC430), control passes to that
            // kernel, whose first CAN TX is a single-frame $99 "alive" - NOT the
            // generic $76. Arm T43ReadKernelActive so the following $35s serve
            // multi-frame blocks, reset the read cursor, and send $99 instead.
            if (node.ReadFamily == ReadKernelFamily.T43
                && startingAddress == ReadEmulation.T43ReadKernelEntryAddress)
            {
                node.State.T43ReadKernelActive = true;
                node.State.UploadCursor = 0;
                ReadEmulation.SendT43KernelAlive(node, ch);
                return true;
            }
        }

        // $76, delayed by the ECU's FlashTransferDelayMs (0 = immediate). Lets
        // any ECU model real per-block program time; default 0 keeps the
        // synchronous behaviour every existing flow/test relies on.
        FlashTiming.EnqueueTransferResponse(node, ch, [Service.Positive(Service.TransferData)]);
        return true;
    }
}
