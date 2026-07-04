// Copied from CAN-Tool (src/Can/CanAdapters.cs) - the shared CAN-adapter layer.
using Shim.Hardware.Obdx;

namespace Shim.Hardware;

/// <summary>Enumerates and constructs the concrete <see cref="ICanAdapter"/> backends.</summary>
public static class CanAdapters
{
    /// <summary>All adapter kinds, for the UI picker.</summary>
    public static IReadOnlyList<CanAdapterKind> Kinds { get; } = Enum.GetValues<CanAdapterKind>();

    /// <summary>List the devices currently available for the given backend.</summary>
    public static IReadOnlyList<CanDeviceInfo> Enumerate(CanAdapterKind kind) => kind switch
    {
        CanAdapterKind.IxxatVci => CanBusService.EnumerateDevices(),
        CanAdapterKind.Obdx => ObdxCanAdapter.EnumerateDevices(),
        _ => []
    };

    /// <summary>Create a fresh, disconnected adapter instance for the given backend.</summary>
    public static ICanAdapter Create(CanAdapterKind kind) => kind switch
    {
        CanAdapterKind.IxxatVci => new CanBusService(),
        CanAdapterKind.Obdx => new ObdxCanAdapter(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown adapter kind.")
    };
}
