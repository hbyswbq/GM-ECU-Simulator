using Core.Dps;
using Core.Identification;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.IO;

namespace GmEcuSimulator.ViewModels.PrimeWizard;

// Page 2 of the prime wizard: display the Phase 3 manifest as an
// editable DataGrid. Rows surface step, DID/PID, source, length, and
// an editable hex value.
//
// Two helper buttons live above the grid:
//
//   Load from bin...    Picks a .bin file, runs Mode1ADidBinExtractor.Parse,
//                       and for each $1A row in the manifest whose DID the
//                       walker found, replaces the row with Source = Bin and
//                       the walker's authentic byte sequence. Empty rows
//                       that match get filled; Bytecode and User rows stay.
//
//   Auto-populate empty Walks every Empty row and asks Phase3DefaultValues
//                       for a sensible value (right length, plausible shape
//                       for known DIDs, alternating-bits filler for unknown
//                       DIDs). Promotes them to Source = Default.
//
// Bytecode and User rows are never touched by either button - those are
// the rows the user (or the archive) has already pinned an authoritative
// value to.
public sealed class Page2Phase3ViewModel : NotifyPropertyChangedBase
{
    private readonly PrimeWizardContext context;
    private readonly Action notifyChanged;
    private int emptyComparedCount;
    private string? errorMessage;
    private string? infoMessage;

    public Page2Phase3ViewModel(PrimeWizardContext context, Action notifyChanged)
    {
        this.context = context;
        this.notifyChanged = notifyChanged;
        LoadFromBinCommand = new RelayCommand(LoadFromBin, () => Rows.Count > 0);
        AutoPopulateCommand = new RelayCommand(AutoPopulateEmpty, () => Rows.Count > 0);
    }

    public ObservableCollection<Phase3RowViewModel> Rows { get; } = new();

    public RelayCommand LoadFromBinCommand { get; }
    public RelayCommand AutoPopulateCommand { get; }

    public int EmptyComparedCount
    {
        get => emptyComparedCount;
        private set
        {
            if (SetField(ref emptyComparedCount, value))
            {
                OnPropertyChanged(nameof(HasEmptyCompared));
                OnPropertyChanged(nameof(EmptyComparedText));
            }
        }
    }

    public bool HasEmptyCompared => emptyComparedCount > 0;
    public string EmptyComparedText
    {
        get
        {
            if (emptyComparedCount == 0) return "";
            var failingSteps = Rows
                .Where(r => r.Model.Source == Phase3RowSource.Empty && r.Model.HasCompareDownstream)
                .Select(r => $"#{r.Model.StepNumber} ({r.DidOrPidDisplay})")
                .ToList();
            return $"{emptyComparedCount} read(s) with no value will fail DPS COMPARE_DATA: {string.Join(", ", failingSteps)}";
        }
    }

    public string? ErrorMessage
    {
        get => errorMessage;
        private set { if (SetField(ref errorMessage, value)) OnPropertyChanged(nameof(HasError)); }
    }
    public bool HasError => errorMessage is not null;

    public string? InfoMessage
    {
        get => infoMessage;
        private set { if (SetField(ref infoMessage, value)) OnPropertyChanged(nameof(HasInfo)); }
    }
    public bool HasInfo => infoMessage is not null;

    public bool IsNextEnabled => context.Dataset is not null && errorMessage is null;

    // Called when the wizard enters this page (forward or via Back). Rebuilds
    // the dataset from the current archive selection and re-applies any prior
    // user edits so they survive Back navigation.
    public void OnEnter()
    {
        if (context.Archive is null) { ErrorMessage = "未选择存档。"; return; }

        try
        {
            context.Dataset = ArchivePrimer.Prime(context.Archive.ArchivePath);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"加载失败: {ex.Message}";
            return;
        }

        ErrorMessage = null;
        InfoMessage = null;
        var manifest = context.Dataset.Phase3;

        // Apply user edits onto a fresh copy of the manifest rows. Keys
        // that no longer match any row (e.g. archive changed and the row
        // shape shifted) are dropped silently.
        var newRows = new List<Phase3Row>(manifest.Rows.Count);
        foreach (var row in manifest.Rows)
        {
            var key = (row.InstructionIndex, row.DidOrPid);
            if (context.UserEdits.TryGetValue(key, out var overrideBytes))
            {
                newRows.Add(row with
                {
                    Source = Phase3RowSource.User,
                    ExpectedValue = overrideBytes,
                    ExpectedLength = overrideBytes.Length,
                });
            }
            else
            {
                newRows.Add(row);
            }
        }
        var edited = new Phase3Manifest(newRows);
        context.EditedManifest = edited;

        Rows.Clear();
        foreach (var r in edited.Rows)
            Rows.Add(new Phase3RowViewModel(r, OnRowEdited));

        RecomputeEmptyCompared();
        OnPropertyChanged(nameof(IsNextEnabled));
        notifyChanged();
    }

    // ----- Load from bin... -----

    private void LoadFromBin()
    {
        var settings = AppSettings.Load();
        var initialDir = (settings.PrimeWizardBinLoadDir is { } d && Directory.Exists(d))
            ? d : null;

        var dlg = new OpenFileDialog
        {
            Title = "从 ECU bin 加载 DID",
            Filter = "二进制 (*.bin)|*.bin|所有文件|*.*",
            CheckFileExists = true,
            InitialDirectory = initialDir ?? string.Empty,
        };
        if (dlg.ShowDialog() != true) return;

        byte[] bytes;
        try { bytes = File.ReadAllBytes(dlg.FileName); }
        catch (Exception ex) { InfoMessage = $"无法读取 bin: {ex.Message}"; return; }

        var binId = Mode1ADidBinExtractor.Parse(bytes);
        if (binId is null)
        {
            InfoMessage = "遍历器无法在该 bin 中定位服务调度器; 未加载任何内容。";
            return;
        }

        var chosenDir = Path.GetDirectoryName(dlg.FileName);
        if (!string.IsNullOrEmpty(chosenDir))
        {
            settings.PrimeWizardBinLoadDir = chosenDir;
            settings.Save();
        }
        context.LoadedBinPath = dlg.FileName;

        // Walk every $1A row and fill from the walker if we've got a value
        // for that DID. Skip Bytecode and User rows - those are pinned.
        int filled = 0;
        foreach (var rowVm in Rows)
        {
            var row = rowVm.Model;
            if (row.OpCode != 0x1A) continue;
            if (row.Source == Phase3RowSource.Bytecode) continue;
            if (row.Source == Phase3RowSource.User) continue;

            var wire = binId.FindDid((byte)row.DidOrPid)?.WireBytes;
            if (wire is null || wire.Length == 0) continue;

            rowVm.UpdateFromExternal(Phase3RowSource.Bin, wire);
            filled++;
        }

        RebuildManifestFromRows();
        RecomputeEmptyCompared();
        InfoMessage = $"Loaded {filled} DID value(s) from {Path.GetFileName(dlg.FileName)} (family: {binId.Family ?? "Unknown"}).";
    }

    // ----- Auto-populate empty rows -----

    private void AutoPopulateEmpty()
    {
        var vin = context.Dataset?.Report.Vin;
        int filled = 0;
        foreach (var rowVm in Rows)
        {
            var row = rowVm.Model;
            if (row.Source != Phase3RowSource.Empty) continue;
            var value = Phase3DefaultValues.For(row.OpCode, row.DidOrPid, row.ExpectedLength, vin);
            rowVm.UpdateFromExternal(Phase3RowSource.Default, value);
            filled++;
        }
        RebuildManifestFromRows();
        RecomputeEmptyCompared();
        InfoMessage = filled == 0
            ? "No empty rows to populate."
            : $"Auto-populated {filled} empty row(s) with sensible default values.";
    }

    // ----- helpers -----

    private void OnRowEdited(Phase3RowViewModel rowVm)
    {
        var key = (rowVm.Model.InstructionIndex, rowVm.Model.DidOrPid);
        var bytes = rowVm.ParsedBytes;
        if (bytes is null) return;

        if (bytes.Length == 0)
        {
            context.UserEdits.Remove(key);
            var baseline = context.Dataset!.Phase3.Rows
                .FirstOrDefault(r => r.InstructionIndex == key.Item1 && r.DidOrPid == key.Item2);
            if (baseline is not null)
                rowVm.UpdateFromBaseline(baseline);
        }
        else
        {
            context.UserEdits[key] = bytes;
            rowVm.MarkAsUser(bytes);
        }
        RebuildManifestFromRows();
        RecomputeEmptyCompared();
    }

    private void RebuildManifestFromRows()
    {
        context.EditedManifest = new Phase3Manifest(Rows.Select(r => r.Model).ToList());
    }

    private void RecomputeEmptyCompared()
    {
        int n = 0;
        foreach (var r in Rows)
            if (r.Model.Source == Phase3RowSource.Empty && r.Model.HasCompareDownstream) n++;
        EmptyComparedCount = n;
    }
}

// Per-row view model for the DataGrid binding.
public sealed class Phase3RowViewModel : NotifyPropertyChangedBase
{
    private Phase3Row model;
    private string valueHex;
    private string? valueError;
    private readonly Action<Phase3RowViewModel> onEdited;

    public Phase3RowViewModel(Phase3Row row, Action<Phase3RowViewModel> onEdited)
    {
        model = row;
        valueHex = ToHex(row.ExpectedValue);
        this.onEdited = onEdited;
    }

    public Phase3Row Model => model;

    public int Step => model.StepNumber;
    public string DidOrPidDisplay => model.OpCode == 0x1A
        ? $"$1A 0x{model.DidOrPid:X2}"
        : $"$22 0x{model.DidOrPid:X4}";
    public string SourceDisplay => model.Source switch
    {
        Phase3RowSource.Bin      => "bin",
        Phase3RowSource.Bytecode => "bytecode",
        Phase3RowSource.Default  => "default",
        Phase3RowSource.User     => "user",
        _                        => "(empty)",
    };
    public int Length => model.ExpectedLength;

    public string ValueHex
    {
        get => valueHex;
        set
        {
            if (!SetField(ref valueHex, value ?? "")) return;
            ParsedBytes = TryParseHex(valueHex);
            if (ParsedBytes is null && !string.IsNullOrWhiteSpace(valueHex))
                ValueError = "无效的十六进制";
            else
            {
                ValueError = null;
                onEdited(this);
            }
        }
    }

    public byte[]? ParsedBytes { get; private set; }

    public string? ValueError
    {
        get => valueError;
        private set { if (SetField(ref valueError, value)) OnPropertyChanged(nameof(HasError)); }
    }
    public bool HasError => valueError is not null;

    public bool ShowWarningIcon => model.Source == Phase3RowSource.Empty && model.HasCompareDownstream;

    public void MarkAsUser(byte[] bytes)
    {
        model = model with
        {
            Source = Phase3RowSource.User,
            ExpectedValue = bytes,
            ExpectedLength = bytes.Length,
        };
        OnPropertyChanged(nameof(SourceDisplay));
        OnPropertyChanged(nameof(Length));
        OnPropertyChanged(nameof(ShowWarningIcon));
    }

    public void UpdateFromBaseline(Phase3Row baseline)
    {
        model = baseline;
        valueHex = ToHex(baseline.ExpectedValue);
        ParsedBytes = baseline.ExpectedValue;
        OnPropertyChanged(nameof(ValueHex));
        OnPropertyChanged(nameof(SourceDisplay));
        OnPropertyChanged(nameof(Length));
        OnPropertyChanged(nameof(ShowWarningIcon));
    }

    // Used by the wizard's bulk-fill buttons (Load from bin..., Auto-populate)
    // to replace the row's value + source in one shot without round-tripping
    // through ValueHex's parser.
    public void UpdateFromExternal(Phase3RowSource source, byte[] bytes)
    {
        model = model with
        {
            Source = source,
            ExpectedValue = bytes,
            ExpectedLength = bytes.Length,
        };
        valueHex = ToHex(bytes);
        ParsedBytes = bytes;
        OnPropertyChanged(nameof(ValueHex));
        OnPropertyChanged(nameof(SourceDisplay));
        OnPropertyChanged(nameof(Length));
        OnPropertyChanged(nameof(ShowWarningIcon));
    }

    private static string ToHex(byte[] bytes)
    {
        if (bytes.Length == 0) return "";
        return string.Join(" ", bytes.Select(b => b.ToString("X2")));
    }

    private static byte[]? TryParseHex(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return Array.Empty<byte>();
        var cleaned = input.Replace(" ", "").Replace("0x", "", StringComparison.OrdinalIgnoreCase).Replace(",", "");
        if (cleaned.Length % 2 != 0) return null;
        var bytes = new byte[cleaned.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            if (!byte.TryParse(cleaned.AsSpan(i * 2, 2),
                System.Globalization.NumberStyles.HexNumber, null, out bytes[i]))
                return null;
        }
        return bytes;
    }
}
