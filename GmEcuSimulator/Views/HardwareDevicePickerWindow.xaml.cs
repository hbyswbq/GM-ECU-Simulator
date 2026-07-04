using System.Windows;
using Shim.Hardware;

namespace GmEcuSimulator.Views;

// Picks the physical CAN adapter + device for ConnectionType.HardwareCan. Code-behind
// only (no VM): enumerates both backends (IXXAT VCI + OBDX) and exposes the chosen
// CanDeviceInfo. OK sets DialogResult=true; the caller (MainViewModel.PromptHardwareDevice)
// persists the choice to AppSettings and starts the hardware transport.
public partial class HardwareDevicePickerWindow : Window
{
    /// <summary>The device the user chose; null while cancelled / unset.</summary>
    public CanDeviceInfo? SelectedDevice { get; private set; }

    private readonly CanAdapterKind initialKind;
    private readonly string? initialKey;

    public HardwareDevicePickerWindow(CanAdapterKind currentKind, string? currentKey)
    {
        InitializeComponent();
        initialKind = currentKind;
        initialKey = currentKey;
        Loaded += (_, _) => Refresh();
    }

    private void Refresh()
    {
        StatusText.Text = "";
        var devices = new List<CanDeviceInfo>();
        foreach (var kind in CanAdapters.Kinds)
        {
            try
            {
                devices.AddRange(CanAdapters.Enumerate(kind));
            }
            catch (Exception ex)
            {
                // A backend failed to enumerate (most commonly the IXXAT VCI driver
                // isn't installed). Surface it, but keep listing the other backends.
                StatusText.Text = $"{kind}: {ex.Message}";
            }
        }

        DeviceList.ItemsSource = devices;
        DeviceList.SelectedItem =
            devices.FirstOrDefault(d => d.Adapter == initialKind && d.Key == initialKey)
            ?? devices.FirstOrDefault();

        if (devices.Count == 0 && string.IsNullOrEmpty(StatusText.Text))
            StatusText.Text = "No CAN devices found. Plug in an IXXAT USB-to-CAN or an OBDX Pro, then Refresh.";
    }

    private void OnRefreshClicked(object sender, RoutedEventArgs e) => Refresh();

    private void OnOkClicked(object sender, RoutedEventArgs e)
    {
        if (DeviceList.SelectedItem is CanDeviceInfo dev)
        {
            SelectedDevice = dev;
            DialogResult = true;
            Close();
        }
        else
        {
            StatusText.Text = "Select a device first, or Cancel.";
        }
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
