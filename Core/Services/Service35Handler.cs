using Common.Protocol;
using Core.Bus;
using Core.Ecu;

namespace Core.Services;

// $35 RequestUpload - the flash-READ counterpart of $34 RequestDownload. Not in
// the GMW3110-2010 PDF (GM never documented an upload service), but both real GM
// reader tools use $35 to read flash OUT of the ECU, in two different dialects
// selected by EcuNode.ReadFamily (Advanced tab, GM persona):
//
//   E38E67 - PowerPCM_Flasher's native boot-ROM upload. $35 declares the read
//     and is answered with a bare positive ($75); the actual data streams back
//     on the subsequent $36 TransferData requests (Service36Handler ->
//     ReadEmulation.SendUploadBlock), the ECU auto-advancing its read cursor.
//
//   T43 - the 6Speed.T43 read-kernel's block-read command. Once the kernel has
//     been DownloadAndExecute'd (Service36Handler sets T43ReadKernelActive), the
//     $35 IS the read: it carries a block size + address and is answered with a
//     complete multi-frame block (ReadEmulation.SendT43Block).
//
// Request layout (both dialects): $35 [sub/fmt] [sizeHi sizeLo] [addrHi addrMid
// addrLo]. The T43 tool sends "35 00 08 00 00 00 00" (size 0x0800, addr 0);
// PowerPCM sends "35 00 20 00 00" (a whole-image declaration we don't need to
// decode - the native path just arms and lets $36 drive).
//
// Precondition: reading protected flash requires a prior $27 unlock. Locked ->
// NRC $33 SecurityAccessDenied. Both tools unlock (and load a kernel) first.
public static class Service35Handler
{
    /// <summary>Returns true if a positive response was sent (caller activates P3C), false on NRC.</summary>
    public static bool Handle(EcuNode node, ReadOnlySpan<byte> usdtPayload, ChannelSession ch)
    {
        if (usdtPayload.Length < 1 || usdtPayload[0] != Service.RequestUpload)
        {
            ServiceUtil.EnqueueNrc(node, ch, Service.RequestUpload, Nrc.SubFunctionNotSupportedInvalidFormat);
            return false;
        }

        // Flash content is security-gated: no unlock, no read.
        if (node.State.SecurityUnlockedLevel == 0)
        {
            ServiceUtil.EnqueueNrc(node, ch, Service.RequestUpload, Nrc.SecurityAccessDenied);
            return false;
        }

        // T43 read-kernel dialect: the $35 is a self-contained block read.
        if (node.ReadFamily == ReadKernelFamily.T43 && node.State.T43ReadKernelActive)
        {
            // size = 2 bytes BE at [2..3]; addr = 3 bytes BE at [4..6] (echoed back,
            // not used to seek - the tool always sends 0 and the ECU tracks the
            // real offset in UploadCursor).
            int size = usdtPayload.Length >= 4 ? (usdtPayload[2] << 8) | usdtPayload[3] : 0;
            if (size <= 0 || size > ReadEmulation.MaxT43BlockBytes)
            {
                ServiceUtil.EnqueueNrc(node, ch, Service.RequestUpload, Nrc.RequestOutOfRange);
                return false;
            }
            uint echoAddr = usdtPayload.Length >= 7
                ? (uint)((usdtPayload[4] << 16) | (usdtPayload[5] << 8) | usdtPayload[6])
                : 0;
            ReadEmulation.SendT43Block(node, ch, size, echoAddr);
            return true;
        }

        // E38/E67 native upload: arm the read; $36 TransferData serves the blocks
        // from the cursor. Reset the cursor so a fresh $35 restarts at flash 0.
        node.State.UploadActive = true;
        node.State.UploadCursor = 0;
        node.State.Fragmenter.EnqueueResponse(ch, node.UsdtResponseCanId,
            [Service.Positive(Service.RequestUpload)]);
        return true;
    }
}
