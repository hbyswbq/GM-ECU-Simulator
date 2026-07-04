using Core.Ecu;

namespace GmEcuSimulator.ViewModels;

// One entry in the per-ECU flash-READ dialect selector (Advanced tab of the ECU
// settings window, shown under the security-module picker for GM personas).
// Pairs the ReadKernelFamily value (EcuNode.ReadFamily) with a short human label.
// Record gives value equality on (Id, Label) so the ComboBox highlights the pick.
//
//   ReadKernelFamily.E38E67 -> "E38 / E67"   (PowerPCM native $35/$36 upload)
//   ReadKernelFamily.T43     -> "T43"         (6Speed.T43 read-kernel)
public sealed record ReadFamilyOption(ReadKernelFamily Id, string Label);
