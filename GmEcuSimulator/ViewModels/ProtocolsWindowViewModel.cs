using System.Collections.Generic;
using Core.Protocol;

namespace GmEcuSimulator.ViewModels;

// Read-only backing for the Protocols window. There is no editable state - the window is a
// canonical view of ProtocolSupportRegistry (what the app supports and what it NRC's), so the VM
// is a thin pass-through with no INotifyPropertyChanged. The window binds Protocols directly.
public sealed class ProtocolsWindowViewModel
{
    public IReadOnlyList<ProtocolSupport> Protocols => ProtocolSupportRegistry.Protocols;

    // Footer caption: the app-wide tally so the user can gauge coverage without summing sections.
    public string OverallSummary
    {
        get
        {
            int total = 0, answered = 0;
            foreach (var p in Protocols)
            {
                total += p.ServiceCount;
                answered += p.ImplementedCount;
            }
            return $"{answered} / {total} 个服务，跨 {Protocols.Count} 个协议，具有正向响应路径";
        }
    }
}
