using System.Windows;
using GmEcuSimulator.ViewModels;

namespace GmEcuSimulator.Views;

// Read-only canonical view of ProtocolSupportRegistry: the protocols the simulator presents and,
// per service, whether it has a positive response path. Themed like ThemedMessageBox (custom
// chrome titlebar + Window.Dialog), no editable state - the DataContext is a thin pass-through VM.
public partial class ProtocolsWindow : Window
{
    public ProtocolsWindow()
    {
        InitializeComponent();
        DataContext = new ProtocolsWindowViewModel();
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();
}
