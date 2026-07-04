using System.ComponentModel;
using System.Windows.Input;

namespace GmEcuSimulator.ViewModels;

// One Excel-style column header menu: a text filter plus sort-ascending /
// sort-descending / clear-sort / clear-filter actions for a single grid
// column. The owning EcuViewModel creates one per filterable column and wires
// the callbacks: onChanged re-runs the view's filter; sort/clearSort drive the
// multi-level sort. The grid's header template binds to this object -
// right-click opens the popup, the magnifying-glass glyph shows when IsActive,
// and the sort arrow + priority number show this column's place in the
// multi-level sort order (SortDirection / SortPriority are set by the owner).
public sealed class PidColumnFilter : NotifyPropertyChangedBase
{
    // Segoe MDL2 Assets chevrons used for the sort-direction indicator.
    private const string ChevronUp = "\uE70E";
    private const string ChevronDown = "\uE70D";

    private readonly Func<PidViewModel, string?> selector;
    private readonly Action onChanged;
    private readonly Action<PidColumnFilter, ListSortDirection> sort;
    private readonly Action<PidColumnFilter> clearSort;

    public PidColumnFilter(string title, string sortPath,
                           Func<PidViewModel, string?> selector,
                           Action onChanged,
                           Action<PidColumnFilter, ListSortDirection> sort,
                           Action<PidColumnFilter> clearSort)
    {
        Title = title;
        SortPath = sortPath;
        this.selector = selector;
        this.onChanged = onChanged;
        this.sort = sort;
        this.clearSort = clearSort;

        SortAscendingCommand = new RelayCommand(() => RunSort(ListSortDirection.Ascending));
        SortDescendingCommand = new RelayCommand(() => RunSort(ListSortDirection.Descending));
        ToggleSortCommand = new RelayCommand(RunToggleSort);
        ClearSortCommand = new RelayCommand(RunClearSort);
        ClearFilterCommand = new RelayCommand(() => FilterText = string.Empty);
    }

    // Header caption and the property path the grid sorts this column by.
    public string Title { get; }
    public string SortPath { get; }

    private string filterText = string.Empty;
    public string FilterText
    {
        get => filterText;
        set
        {
            if (!SetField(ref filterText, value)) return;
            OnPropertyChanged(nameof(IsActive));
            onChanged();
        }
    }

    // Drives the header's magnifying-glass indicator and the combined predicate.
    public bool IsActive => !string.IsNullOrWhiteSpace(filterText);

    private bool isOpen;
    public bool IsOpen
    {
        get => isOpen;
        set => SetField(ref isOpen, value);
    }

    // -------- Sort state (owned/updated by EcuViewModel.RebuildSort) --------

    private ListSortDirection? sortDirection;
    // null when this column isn't part of the sort. Set by the owner.
    public ListSortDirection? SortDirection
    {
        get => sortDirection;
        set
        {
            if (!SetField(ref sortDirection, value)) return;
            OnPropertyChanged(nameof(IsSorted));
            OnPropertyChanged(nameof(SortGlyph));
        }
    }

    public bool IsSorted => sortDirection.HasValue;

    // Up chevron for ascending, down for descending; empty when unsorted.
    public string SortGlyph => sortDirection switch
    {
        ListSortDirection.Ascending => ChevronUp,
        ListSortDirection.Descending => ChevronDown,
        _ => string.Empty,
    };

    private int sortPriority;
    // 1-based position in the multi-level sort, or 0 when not shown. The owner
    // sets it to 0 for a single sort column (no number needed) and 1..N when
    // more than one column is sorted, so the badge only appears when it helps.
    public int SortPriority
    {
        get => sortPriority;
        set
        {
            if (!SetField(ref sortPriority, value)) return;
            OnPropertyChanged(nameof(ShowSortPriority));
            OnPropertyChanged(nameof(SortPriorityText));
        }
    }

    public bool ShowSortPriority => sortPriority > 0;
    public string SortPriorityText => sortPriority > 0 ? sortPriority.ToString() : string.Empty;

    public ICommand SortAscendingCommand { get; }
    public ICommand SortDescendingCommand { get; }
    public ICommand ToggleSortCommand { get; }
    public ICommand ClearSortCommand { get; }
    public ICommand ClearFilterCommand { get; }

    // True when this column doesn't filter out the row (case-insensitive
    // substring against the column's projected text). Inactive columns pass
    // everything.
    public bool Matches(PidViewModel pid)
    {
        if (!IsActive) return true;
        var value = selector(pid);
        return value != null &&
               value.Contains(filterText.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private void RunSort(ListSortDirection direction)
    {
        sort(this, direction);
        IsOpen = false;
    }

    // Double-clicking the header toggles this column's sort between ascending and descending. A column not yet in the
    // sort starts ascending; thereafter each toggle flips the direction. Promotes the column to the primary sort key
    // (same path SortAscending/Descending take), so repeated double-clicks just reverse the order in place.
    private void RunToggleSort()
    {
        var next = sortDirection == ListSortDirection.Ascending
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;
        RunSort(next);
    }

    private void RunClearSort()
    {
        clearSort(this);
        IsOpen = false;
    }
}
