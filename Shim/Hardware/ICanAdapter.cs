// Copied from CAN-Tool (src/Can/ICanAdapter.cs) - the shared CAN-adapter layer.
// Kept as a local copy (not a shared package) per the integration decision;
// keep in sync with CAN-Tool by hand if the contract changes.
namespace Shim.Hardware;

/// <summary>
/// A CAN adapter backend. Confines all vendor/transport specifics behind one surface so the
/// bridge and view model stay adapter-agnostic. Implemented by the Ixxat VCI4 driver
/// (<see cref="CanBusService"/>) and the OBDX Pro tool (<c>ObdxCanAdapter</c>).
/// </summary>
public interface ICanAdapter : IDisposable
{
    /// <summary>Raised for every received (or self-received) frame, on a background thread.</summary>
    event Action<CanFrame>? FrameReceived;

    /// <summary>Raised on a bus error / state change, on a background thread.</summary>
    event Action<string>? BusError;

    bool IsConnected { get; }

    /// <summary>True when cyclic frames transmit without a software timer (hardware scheduler).</summary>
    bool SupportsScheduler { get; }

    /// <summary>Open <paramref name="device"/> at the given bit rate and start receiving.</summary>
    void Connect(CanDeviceInfo device, CanBitRate bitRate, bool listenOnly = false);

    void Disconnect();

    void Send(uint identifier, bool extended, byte[] data, bool remote = false);

    int StartCyclic(uint identifier, bool extended, byte[] data, bool remote, double intervalMs);

    void StopCyclic(int handle);

    void StopAllCyclic();
}
