namespace GmEcuSimulator.ViewModels;

// One entry in the per-ECU standard-selector ComboBox (Advanced tab of the ECU
// settings window). Pairs the persistence-side standard id (EcuDto.PersonaId /
// EcuNode.PersonaId) with a short human label for the dropdown. Record gives
// value equality on (Id, Label) so the ComboBox highlights the current pick.
//
// Only the two selectable standard sets are offered here:
//   "gmw3110"  -> J1979 + GMW3110            ("GM Gen 4")
//   "ford-uds" -> Ford UDS capture stack     ("Ford")
// The SPS kernel is intentionally absent - it is a runtime-only stack binding
// pushed by Service36Handler when a kernel boot-loads, not something a user picks.
public sealed record PersonaOption(string Id, string Label);
