using System.Collections.ObjectModel;

namespace GmEcuSimulator.ViewModels;

// One SECTION of the editor's per-stack diagnostic-service checklist (DESIGN-Protocol-Stack-
// Architecture.md section 6: "render like the full gospel, store like the delta"). For a GM node
// there is one section per bound standard (J1979, GMW3110). For the Ford capture node the single
// catch-all binding is split into one section PER display group (OBD, UDS, Ford-proprietary) so the
// editor shows the same segmented shape GM gets - several sections share the one "Ford" standard and
// therefore the one allow-list override.
//
// Each section renders its services as tickable rows; the tick state is initialised from the binding's
// effective allow-list. Toggling any row asks the owner to recompute that standard's single allow-list
// from EVERY section's live ticks and persist only the delta off the synthesized default
// (EcuViewModel.ApplyStackServiceSelection). One Ford section carries an explanatory note.
public sealed class StackBindingViewModel : NotifyPropertyChangedBase
{
    private readonly EcuViewModel owner;
    private bool suppress;                            // gate the toggle callback during initial fill

    /// <summary>The bound standard's registry key ("J1979" / "GMW3110" / "Ford"). Several sections can
    /// share one Standard (the Ford segments); they all drive the same override.</summary>
    public string Standard { get; }

    /// <summary>Section header: the standard name for GM, or the group name (OBD / UDS / Ford
    /// Proprietary) for a Ford segment.</summary>
    public string Header { get; }

    /// <summary>True when this section carries the capture-stack note (one Ford segment does).</summary>
    public bool ShowsNote { get; }

    /// <summary>Explanatory note shown above this section (Ford capture stack only).</summary>
    public string? Note { get; }

    /// <summary>The tickable service rows in this section.</summary>
    public ObservableCollection<StackServiceViewModel> Services { get; } = new();

    public StackBindingViewModel(EcuViewModel owner, string standard, string header,
        IEnumerable<(byte Sid, string Name, bool Enabled)> services, string? note)
    {
        this.owner = owner;
        Standard = standard;
        Header = header;
        Note = note;
        ShowsNote = note is not null;

        suppress = true;
        foreach (var (sid, name, enabled) in services)
            Services.Add(new StackServiceViewModel(this, sid, name, enabled));
        suppress = false;
    }

    // A service row changed. Let the owner recompute the standard's allow-list from every section that
    // shares this Standard (so the Ford segments combine into one override) and persist only the delta.
    internal void OnServiceToggled()
    {
        if (suppress) return;
        owner.ApplyStackServiceSelection(Standard);
    }
}

// One tickable service row in a section's checklist: the wire SID, its catalog name, and whether this
// ECU's binding answers it.
public sealed class StackServiceViewModel : NotifyPropertyChangedBase
{
    private readonly StackBindingViewModel parent;
    private bool isEnabled;

    public byte Sid { get; }
    public string Name { get; }

    /// <summary>Checkbox label, e.g. "$22  ReadDataByIdentifier".</summary>
    public string Display { get; }

    public StackServiceViewModel(StackBindingViewModel parent, byte sid, string name, bool isEnabled)
    {
        this.parent = parent;
        Sid = sid;
        Name = name;
        this.isEnabled = isEnabled;
        Display = $"${sid:X2}  {name}";
    }

    public bool IsEnabled
    {
        get => isEnabled;
        set
        {
            if (isEnabled == value) return;
            isEnabled = value;
            OnPropertyChanged();
            parent.OnServiceToggled();
        }
    }
}
