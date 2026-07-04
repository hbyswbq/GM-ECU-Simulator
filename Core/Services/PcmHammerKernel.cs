using Common.Protocol;
using Core.Bus;
using Core.Ecu;

namespace Core.Services;

// Command set of the PCMHacking.net / PcmHammer flash-WRITE kernel, served once
// that kernel is running on a GM E38 (and similar Gen-IV) ECM - i.e. after $36
// sub $80 DownloadAndExecute uploads a kernel whose image carries the
// "PCMHacking" ASCII banner (sniffed in Service36Handler, which sets
// NodeState.KernelIsPcmHammer).
//
// This is a plain helper, NOT a persona or a separate protocol stack: the shared
// kernel dispatch (UdsKernelDispatch) is the single home for every boot-loaded
// kernel, and it branches on KernelIsPcmHammer exactly the way the flash-READ
// path branches on ReadFamily / T43ReadKernelActive (see ReadEmulation). A DPS /
// PowerPCM session never sets the flag, so their $34/$35/$36 path is unchanged
// and they never reach this code.
//
// Unlike GM's SPS kernel (generic $34/$36 download), the PcmHammer kernel exposes
// its OWN command set so a host can erase, write, and CRC-verify calibration.
// Transcribed from PcmHammer's Gmlan.cs (mode $3D queries + the kernel's $36
// flash-write):
//
//   $3D $00                                  -> $7D $00 <ver4>     version / probe
//   $3D $02 <size3> <addr3>                  -> $7D $02 <crc4>     CRC-32 of a range
//   $3D $05 <addr3>                          -> $7D $05 $00        erase 64 KiB sector
//   $36 <ct> <len2> <addr3> <data..> <sum2>  -> $76                write block
//                                              ($7F $36 nrc on a bad sum)
//   $20                                      -> hand control back to the boot ROM
//
// $36 ct: $00 = program, $44 = test-write (validate the transfer, no program).
// The kernel operates on NodeState.KernelFlash (2 MiB, $FF on erase, copied on
// write, CRC read). Reset by ClearProgrammingState on $20 / P3C timeout.
public static class PcmHammerKernel
{
    /// <summary>Mode $3D: the PcmHammer kernel's query/erase channel (probe / CRC-32 / sector erase).</summary>
    public const byte KernelFlashQuery = 0x3D;

    private const int  FlashSize = 0x200000;       // 2 MiB address space
    private const uint EraseSectorSize = 0x10000;  // 64 KiB sectors

    /// <summary>
    /// Handle a mode $3D kernel query ($00 probe / $02 CRC-32 / $05 erase).
    /// Returns true if a positive response was enqueued, false if an NRC was sent
    /// (so the caller mirrors the other kernel services' "ActivateP3C on positive"
    /// convention).
    /// </summary>
    public static bool HandleQuery(EcuNode node, ReadOnlySpan<byte> usdt, ChannelSession ch)
    {
        if (usdt.Length < 2) { Nrc36OrQuery(node, ch, KernelFlashQuery, Nrc.SubFunctionNotSupportedInvalidFormat); return false; }
        switch (usdt[1])
        {
            case 0x00:   // version / probe
                Respond(node, ch, [Service.Positive(KernelFlashQuery), 0x00, 0x00, 0x00, 0x00, 0x01]);
                return true;

            case 0x02:   // CRC-32: $3D $02 <size3> <addr3>  (size precedes address)
            {
                if (usdt.Length < 8) { Nrc36OrQuery(node, ch, KernelFlashQuery, Nrc.SubFunctionNotSupportedInvalidFormat); return false; }
                uint size = (uint)((usdt[2] << 16) | (usdt[3] << 8) | usdt[4]);
                uint addr = (uint)((usdt[5] << 16) | (usdt[6] << 8) | usdt[7]);
                byte[] flash = Flash(node);
                uint crc = ((long)addr + size <= flash.Length) ? Crc32(flash, addr, size) : 0u;
                Respond(node, ch, [Service.Positive(KernelFlashQuery), 0x02,
                    (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc]);
                return true;
            }

            case 0x05:   // erase the 64 KiB sector containing addr: $3D $05 <addr3>
            {
                if (usdt.Length < 5) { Nrc36OrQuery(node, ch, KernelFlashQuery, Nrc.SubFunctionNotSupportedInvalidFormat); return false; }
                uint addr = (uint)((usdt[2] << 16) | (usdt[3] << 8) | usdt[4]);
                byte[] flash = Flash(node);
                uint sector = addr & ~(EraseSectorSize - 1);
                if (sector < flash.Length)
                {
                    int end = (int)Math.Min(flash.Length, sector + EraseSectorSize);
                    flash.AsSpan((int)sector, end - (int)sector).Fill(0xFF);
                }
                Respond(node, ch, [Service.Positive(KernelFlashQuery), 0x05, 0x00]);   // status $00 = ok
                return true;
            }

            default:
                Nrc36OrQuery(node, ch, KernelFlashQuery, Nrc.SubFunctionNotSupportedInvalidFormat);
                return false;
        }
    }

    /// <summary>
    /// Handle the PcmHammer kernel's custom $36 flash-write block:
    /// $36 &lt;ct&gt; &lt;len2&gt; &lt;addr3&gt; &lt;data..&gt; &lt;sum2&gt;.
    /// This is NOT the boot-ROM TransferData form - the caller has already
    /// established KernelIsPcmHammer, so Service36Handler is bypassed. Returns
    /// true on the positive $76, false on an NRC.
    /// </summary>
    public static bool HandleWrite(EcuNode node, ReadOnlySpan<byte> usdt, ChannelSession ch)
    {
        // $36 <ct> <len hi,lo> <addr hi,mid,lo> <data..> <sum hi,lo>
        if (usdt.Length < 7 + 2) { ServiceUtil.EnqueueNrc(node, ch, Service.TransferData, Nrc.SubFunctionNotSupportedInvalidFormat); return false; }
        byte ct = usdt[1];
        int len = (usdt[2] << 8) | usdt[3];
        uint addr = (uint)((usdt[4] << 16) | (usdt[5] << 8) | usdt[6]);
        if (usdt.Length != 7 + len + 2) { ServiceUtil.EnqueueNrc(node, ch, Service.TransferData, Nrc.RequestOutOfRange); return false; }

        var data = usdt.Slice(7, len);
        ushort sum = 0;
        for (int i = 0; i < len; i++) sum += data[i];
        ushort got = (ushort)((usdt[7 + len] << 8) | usdt[7 + len + 1]);
        if (sum != got) { ServiceUtil.EnqueueNrc(node, ch, Service.TransferData, Nrc.GeneralProgrammingFailure); return false; }

        if (ct == 0x00)   // $00 = program; $44 = test-write (validate only, no program)
        {
            byte[] flash = Flash(node);
            if ((long)addr + len <= flash.Length)
                data.CopyTo(flash.AsSpan((int)addr));
        }
        Respond(node, ch, [Service.Positive(Service.TransferData)]);   // $76
        return true;
    }

    // Lazily allocate the kernel's 2 MiB flash image ($FF = erased) on first touch.
    private static byte[] Flash(EcuNode node)
    {
        if (node.State.KernelFlash is null || node.State.KernelFlash.Length < FlashSize)
        {
            var f = new byte[FlashSize];
            f.AsSpan().Fill(0xFF);
            node.State.KernelFlash = f;
        }
        return node.State.KernelFlash;
    }

    private static void Respond(EcuNode node, ChannelSession ch, ReadOnlySpan<byte> payload)
        => node.State.Fragmenter.EnqueueResponse(ch, node.UsdtResponseCanId, payload);

    // The $3D query channel NRCs against the $3D SID; the shared ServiceUtil helper
    // does the identical [$7F sid nrc] framing used everywhere else.
    private static void Nrc36OrQuery(EcuNode node, ChannelSession ch, byte sid, byte nrc)
        => ServiceUtil.EnqueueNrc(node, ch, sid, nrc);

    // CRC-32: polynomial 0x04C11DB7, initial 0, MSB-first, no reflection, no final
    // XOR - matches PcmHammer Gmlan.ComputeCrc32 and the display's flash verify.
    private static uint Crc32(byte[] d, uint offset, uint len)
    {
        uint r = 0;
        for (uint i = 0; i < len; i++)
        {
            r ^= (uint)d[offset + i] << 24;
            for (int b = 0; b < 8; b++)
                r = (r & 0x80000000u) != 0 ? (r << 1) ^ 0x04C11DB7u : (r << 1);
        }
        return r;
    }
}
