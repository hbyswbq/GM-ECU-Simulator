// Copied from CAN-Tool (src/Can/CanDeviceInfo.cs) - the shared CAN-adapter layer.
namespace Shim.Hardware;

/// <summary>Which backend a <see cref="CanDeviceInfo"/> belongs to and opens with.</summary>
public enum CanAdapterKind
{
    /// <summary>Ixxat VCI4 (USB-to-CAN V2).</summary>
    IxxatVci,
    /// <summary>OBDX Pro scantool (USB / WiFi / BLE) over the DVI protocol.</summary>
    Obdx
}

/// <summary>
/// UI-friendly description of a selectable CAN device. <see cref="Key"/> is an opaque,
/// adapter-specific handle used to actually open it: for Ixxat it is the VCI object id;
/// for OBDX it is a transport key such as <c>serial:COM5</c> or <c>tcp:192.168.4.1:23</c>.
/// </summary>
public sealed record CanDeviceInfo(
    CanAdapterKind Adapter,
    string Key,
    string Description,
    string Detail)
{
    public override string ToString() =>
        string.IsNullOrEmpty(Detail) ? Description : $"{Description} [{Detail}]";
}
