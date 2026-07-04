namespace Core.Ecu;

// Which GM flash-READ protocol this ECU presents on the wire, selected in the
// Advanced tab under the security-module picker (GM persona only). It decides
// how a $35/$36 read is answered so the two real reader tools each get the
// dialect they expect:
//
//   E38E67 - PowerPCM_Flasher's NATIVE boot-ROM upload. No downloaded kernel:
//            $35 RequestUpload declares the whole image, then repeated $36
//            TransferData responses each carry a 1024-byte block ($76 + seq +
//            data), the ECU auto-advancing its read cursor. Validate via
//            $31 $01 $04 (CRC-16/CCITT-FALSE).
//
//   T43    - 6Speed.T43's downloaded read-kernel. $34 + $36 sub $80
//            DownloadAndExecute at 0x003FC430 boots the kernel, which announces
//            itself with a single-frame $99 handshake (NOT the generic $76);
//            each $35 then returns a self-contained multi-frame block (SF $75,
//            then a non-standard First Frame carrying only the 5-byte
//            "$36 00 <addr24>" echo, then Consecutive Frames streaming the
//            block's flash bytes). See
//            memory/reference_t43_read_kernel_and_powerpcm_read_protocol.md and
//            the disassembly under ..\6Speed.T43\extracted_blobs\_readkernel.asm.
//
// Default E38E67 - the native path is the common case and needs no kernel
// upload, so old configs (which omit the field) read back as E38E67 unchanged.
public enum ReadKernelFamily
{
    E38E67 = 0,
    T43 = 1,
}
