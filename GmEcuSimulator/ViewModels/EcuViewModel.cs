using Common.Protocol;
using Common.Signals;
using Common.Signals.Engines;
using Common.Waveforms;
using Core.Ecu;
using Core.Identification;
using Core.Scheduler;
using Core.Security;
using Core.Services;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;

namespace GmEcuSimulator.ViewModels;

// Editable view of one EcuNode plus its child PidViewModels. CAN ID
// edits push straight to the model; on the next IPC dispatch they take
// effect (the bus's FindByRequestId does the lookup fresh per frame).
public sealed class EcuViewModel : NotifyPropertyChangedBase
{
    public EcuNode Model { get; }
    public ObservableCollection<PidViewModel> Pids { get; } = new();

    // The ECU's $01 (OBD-II) PIDs: read-only catalogue rows with a per-PID Supported toggle, shown in the editor's
    // "$01 (OBD-II)" section. Together with the editable Pids grid ($1A/$22/$2D) this is the whole-ECU view.
    public ObservableCollection<J1979RowViewModel> Obd2Pids { get; } = new();

    // The editable modes, each rendered as its own collapsible section in the editor (mirrors the read-only "$01"
    // section). Every section is an independent filtered/sorted view over the single shared Pids collection above, so
    // Add/Remove/alias-collision logic keeps operating on Pids directly. Order = display order top-to-bottom.
    public IReadOnlyList<PidModeSection> Sections { get; }

    // The DBC-driven CAN broadcast messages this ECU emits, shown in the editor's "CAN Broadcast"
    // section (above $01). Each wraps a Core BroadcastMessage; edits flow through OnBroadcastEdited so
    // the live scheduler rebuilds. Import / Save / Load (*.dbc / *.dbc.json) are MainViewModel
    // commands (they own the file dialogs); Add / Remove message are local commands below.
    public ObservableCollection<BroadcastMessageViewModel> Broadcasts { get; } = new();
    private BroadcastMessageViewModel? selectedBroadcast;

    // Ford-persona DMR address -> engine signal map (only meaningful / shown for ford-uds ECUs).
    public ObservableCollection<DmrSignalMappingViewModel> DmrSignalMappings { get; } = new();
    private DmrSignalMappingViewModel? selectedDmrMapping;

    public GlitchConfigViewModel Glitch { get; }
    private PidViewModel? selectedPid;

    public EcuViewModel(EcuNode model)
    {
        Model = model;
        Glitch = new GlitchConfigViewModel(model.Glitch);
        // AllPids unions the four per-mode stores in deterministic order
        // (Mode22 -> Mode2D -> Mode1A -> Mode1), so the editor grid renders
        // every row regardless of which underlying dictionary owns it. The
        // Mode setter on PidViewModel calls EcuNode.RelocatePidMode when
        // the user flips a row, which moves the underlying Pid between
        // stores without churning this ObservableCollection.
        foreach (var pid in model.AllPids) Pids.Add(new PidViewModel(pid, this));
        foreach (var def in J1979Catalogue.All) Obd2Pids.Add(new J1979RowViewModel(def, model));
        foreach (var msg in model.Broadcasts) Broadcasts.Add(new BroadcastMessageViewModel(msg, this));
        foreach (var m in model.DmrSignalMappings) DmrSignalMappings.Add(new DmrSignalMappingViewModel(m, this));

        AddBroadcastCommand = new RelayCommand(AddBroadcast);
        RemoveBroadcastCommand = new RelayCommand(
            () => { if (SelectedBroadcast != null) RemoveBroadcast(SelectedBroadcast); },
            () => SelectedBroadcast != null);
        AddDmrMappingCommand = new RelayCommand(AddDmrMapping);
        RemoveDmrMappingCommand = new RelayCommand(
            () => { if (SelectedDmrMapping != null) RemoveDmrMapping(SelectedDmrMapping); },
            () => SelectedDmrMapping != null);
        SeedDefaultPidsCommand = new RelayCommand(() => SeedDefaultPids(), () => !Model.IsPrimed);

        // One collapsible section per editable mode. Each builds its own filtered/sorted view over Pids and its own
        // column-filter set; the order here is the editor's top-to-bottom order.
        Sections = new[]
        {
            new PidModeSection(this, PidMode.Mode1A, "$1A (Identity / ReadDataByIdentifier)", Pids),
            new PidModeSection(this, PidMode.Mode22, "$22 (ReadDataByIdentifier)", Pids),
            new PidModeSection(this, PidMode.Mode2D, "$2D (DefinePIDByAddress)", Pids),
            new PidModeSection(this, PidMode.Mode23, "$23 (ReadMemoryByAddress)", Pids),
        };

        // Re-evaluate Mode2D alias collisions whenever rows are added,
        // removed, or replaced. Per-row mode/address edits flow through
        // PidViewModel -> RaisePidsChanged which also calls this; the two
        // entry points keep every collision warning in sync.
        Pids.CollectionChanged += (_, __) => RefreshAliasCollisions();
        RefreshAliasCollisions();

        // Security module picker: synthetic "(none)" at index 0, then every
        // registered module ID. Matches the ComboBox's ItemsSource binding.
        AvailableSecurityModuleIds = new ObservableCollection<string> { NoneSecurityModuleLabel };
        foreach (var id in SecurityModuleRegistry.KnownIds) AvailableSecurityModuleIds.Add(id);
        selectedSecurityModuleId = model.SecurityModule?.Id ?? NoneSecurityModuleLabel;

        // Initial KV entries from any persisted config.
        SecurityModuleConfigEntries = new ObservableCollection<KeyValueEntry>();
        LoadEntriesFromJson(model.SecurityModuleConfig);
        SecurityModuleConfigEntries.CollectionChanged += OnSecurityEntriesChanged;
        foreach (var e in SecurityModuleConfigEntries) e.PropertyChanged += OnSecurityEntryPropertyChanged;

        LoadInfoFromBinCommand = new RelayCommand(LoadInfoFromBin);
        ClearBinCommand = new RelayCommand(ClearBin, () => !string.IsNullOrEmpty(Model.FlashBinPath));
        AutoPopulateDidsCommand = new RelayCommand(AutoPopulateMissingDids);
        EditPrimeCommand = new RelayCommand(EditPrime, () => primeContext != null && bus != null);

        // Hide the PID-mode sections the current persona doesn't speak (Ford -> no $1A / $2D).
        RefreshSectionVisibility();
    }

    // Show only the PID-mode sections whose SID is in the current persona's service catalog. GMW3110 carries all four
    // editable modes ($1A / $22 / $2D / $23); the Ford UDS catalog carries $22 and $23 only (UDS dropped $1A for $22 and
    // folded $2D into $2C, but kept $23 ReadMemoryByAddress), so the GM-only $1A and $2D sections collapse when Ford is
    // selected while $22 and $23 stay. This matches the wire behaviour - FordUdsDispatch doesn't dispatch $1A/$2D and
    // Mode 09 has its own store, but $23 is served (from a Mode23 row or the loaded flash bin) - so a hidden section also
    // plays no part. Rows are only hidden, never deleted: switching persona back reveals them and Save round-trips them.
    private void RefreshSectionVisibility()
    {
        var catalog = Model.PersonaId == "ford-uds"
            ? Core.Protocol.StandardCatalogs.Ford
            : Core.Protocol.StandardCatalogs.Gmw3110;
        foreach (var section in Sections)
            section.IsVisible = catalog.Contains(section.Sid);
    }

    // -------- Prime wizard re-entry --------

    private PrimeWizard.PrimeWizardContext? primeContext;
    private Core.Bus.VirtualBus? bus;

    /// <summary>
    /// True when this ECU was produced by a successful Prime wizard run and
    /// the wizard context is available for re-entry. Drives the Edit prime
    /// button's visibility on the main window's per-ECU template.
    /// </summary>
    public bool IsPrimed => primeContext != null;

    /// <summary>
    /// Called by MainViewModel.Rebuild for every ECU. The bus reference is
    /// needed when the user re-opens the prime wizard via EditPrimeCommand.
    /// </summary>
    public void BindBus(Core.Bus.VirtualBus bus) => this.bus = bus;

    // -------- CAN broadcast section --------

    public RelayCommand AddBroadcastCommand { get; private set; } = null!;
    public RelayCommand RemoveBroadcastCommand { get; private set; } = null!;
    public RelayCommand AddDmrMappingCommand { get; private set; } = null!;
    public RelayCommand RemoveDmrMappingCommand { get; private set; } = null!;

    // Explicit "Seed default PIDs" action for the editor's Diagnostic PIDs header. Adds the curated baseline identity
    // ($1A) + live $22 set to this ECU. Opt-in by design: seeding is no longer automatic on Add ECU / config load, so
    // a deleted seed row never reappears on its own. Disabled for primed ECUs (they own their map from the archive).
    public RelayCommand SeedDefaultPidsCommand { get; private set; } = null!;

    public BroadcastMessageViewModel? SelectedBroadcast
    {
        get => selectedBroadcast;
        set => SetField(ref selectedBroadcast, value);   // RemoveBroadcastCommand re-queries via CommandManager
    }

    private void AddBroadcast()
    {
        // Pick a CAN id not already used by another broadcast on this ECU.
        var taken = new HashSet<uint>(Broadcasts.Select(b => b.Model.CanId));
        uint canId = 0x100;
        while (taken.Contains(canId)) canId++;
        var msg = new BroadcastMessage { CanId = canId, Name = "新广播", Dlc = 8, PeriodMs = 100, Enabled = true };
        Model.AddBroadcast(msg);
        var vm = new BroadcastMessageViewModel(msg, this);
        Broadcasts.Add(vm);
        SelectedBroadcast = vm;
        OnBroadcastEdited();
    }

    public void RemoveBroadcast(BroadcastMessageViewModel vm)
    {
        Model.RemoveBroadcast(vm.Model);
        Broadcasts.Remove(vm);
        if (ReferenceEquals(selectedBroadcast, vm)) SelectedBroadcast = null;
        OnBroadcastEdited();
    }

    // Re-sync the VM collection from the model after a bulk change (DBC import / .dbc.json load /
    // replace-all). The caller has already mutated Model.Broadcasts.
    public void ReloadBroadcasts()
    {
        Broadcasts.Clear();
        foreach (var msg in Model.Broadcasts) Broadcasts.Add(new BroadcastMessageViewModel(msg, this));
        SelectedBroadcast = null;
        OnBroadcastEdited();
    }

    // Any broadcast edit: tell the model (so node-level subscribers know) and rebuild the live
    // scheduler if a host session is currently emitting. No-op on the scheduler when idle.
    public void OnBroadcastEdited()
    {
        Model.RaiseBroadcastsChanged();
        bus?.BroadcastScheduler.RebuildIfRunning();
    }

    // ---------------- Ford DMR signal map ----------------

    /// <summary>True only for ECUs running the Ford UDS persona (the DMR map is meaningless
    /// otherwise). Drives the visibility of the DMR mapping section in the editor.</summary>
    public bool IsFordUdsPersona => Model.PersonaId == "ford-uds";

    /// <summary>True for the GM (GMW3110) persona. Drives the visibility of the GM-only
    /// flash-READ dialect ("Read as") picker under the security-module dropdown.</summary>
    public bool IsGmPersona => Model.PersonaId == "gmw3110";

    public DmrSignalMappingViewModel? SelectedDmrMapping
    {
        get => selectedDmrMapping;
        set => SetField(ref selectedDmrMapping, value);
    }

    private void AddDmrMapping()
    {
        var m = new Core.Ecu.DmrSignalMapping { Address = 0x003F7FA0, Name = "", Signal = Common.Signals.SignalId.EngineRpm };
        Model.AddDmrSignalMapping(m);
        var vm = new DmrSignalMappingViewModel(m, this);
        DmrSignalMappings.Add(vm);
        SelectedDmrMapping = vm;
        OnDmrMappingEdited();
    }

    public void RemoveDmrMapping(DmrSignalMappingViewModel vm)
    {
        Model.RemoveDmrSignalMapping(vm.Model);
        DmrSignalMappings.Remove(vm);
        if (ReferenceEquals(selectedDmrMapping, vm)) SelectedDmrMapping = null;
        OnDmrMappingEdited();
    }

    // Any DMR-map edit: notify node-level subscribers. The Ford broadcast loop reads the map live
    // (EcuNode.DmrSignalFor) each tick, so there is no scheduler to rebuild - the change is picked up
    // on the next emit automatically.
    public void OnDmrMappingEdited() => Model.RaiseDmrSignalMappingsChanged();

    // 10 Hz live-value refresh for the broadcast signal readouts (driven by MainWindow's timer).
    public void RefreshBroadcastsLive(double timeMs)
    {
        foreach (var b in Broadcasts) b.RefreshLive(Model.EngineModel, timeMs);
    }

    // The operating points the user can drive this ECU through; bound to the Scenario ComboBox in the inspector.
    public ScenarioId[] Scenarios { get; } = Enum.GetValues<ScenarioId>();

    // The live operating point. Backed by the engine model (not a VM field) so it always reflects the model. Setting
    // it ramps the signals toward the new scenario from the current bus clock, which a connected tool sees move on
    // Mode $01 and on any signal-backed $22 PIDs.
    public ScenarioId SelectedScenario
    {
        get => Model.EngineModel.ActiveScenario;
        set
        {
            if (Model.EngineModel.ActiveScenario == value) return;
            Model.EngineModel.SetScenario(value, bus?.NowMs ?? 0);
            OnPropertyChanged();
        }
    }

    // The engine characters the user can pick for this ECU; bound to the Engine Model ComboBox in the inspector.
    public EngineModelOption[] EngineModels { get; } =
        EngineCharacterRegistry.Catalogue.Select(c => new EngineModelOption(c.Id, c.DisplayName)).ToArray();

    // The selected engine character's id. Backed by the live model (not a VM field) so it always reflects what is
    // running. Setting it swaps the character behind the engine model's volatile reference - so a connected tool
    // immediately sees the new induction behaviour (e.g. MAP and fuel pressure rising above base under boost) on the
    // next $01 / $22 read, without re-creating the ECU or dropping the session.
    public string SelectedEngineModelId
    {
        get => Model.EngineModel.Character.Id;
        set
        {
            if (Model.EngineModel.Character.Id == value) return;
            Model.EngineModel.Character = EngineCharacterRegistry.Create(value);
            OnPropertyChanged();
        }
    }

    // The AccelDecelSweep rev-pull timing is fixed at SweepProfile.Default for every ECU - it is not editable per
    // ECU and not persisted. The former Sweep* editor properties (and their collapsed Setup-pane inputs) were retired.

    /// <summary>
    /// Called by MainViewModel after the wizard registers a new primed ECU.
    /// Stores the wizard's final context so the user can re-open the wizard
    /// later via <see cref="EditPrimeCommand"/>; the context lives for the
    /// session and is discarded when the ECU is removed.
    /// </summary>
    public void AttachPrimeContext(PrimeWizard.PrimeWizardContext context)
    {
        primeContext = context;
        OnPropertyChanged(nameof(IsPrimed));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    public RelayCommand EditPrimeCommand { get; }

    private void EditPrime()
    {
        if (primeContext is null || bus is null) return;
        var wizard = new Views.PrimeWizard.PrimeWizardWindow(bus, Model, primeContext)
        {
            Owner = System.Windows.Application.Current?.MainWindow,
        };
        wizard.ShowDialog();
        if (wizard.CommittedNode is not null && wizard.CommittedDataset is not null)
        {
            // Wizard already swapped the bus node; refresh the stashed
            // context with the (possibly mutated) one for the next re-open.
            primeContext = wizard.Context;
        }
    }

    public string Name
    {
        get => Model.Name;
        set { if (Model.Name != value) { Model.Name = value; OnPropertyChanged(); } }
    }

    public ushort PhysicalRequestCanId
    {
        get => Model.PhysicalRequestCanId;
        set { if (Model.PhysicalRequestCanId != value) { Model.PhysicalRequestCanId = value; OnPropertyChanged(); OnPropertyChanged(nameof(PhysicalRequestCanIdHex)); } }
    }

    public string PhysicalRequestCanIdHex
    {
        get => $"0x{Model.PhysicalRequestCanId:X3}";
        set { if (TryParseHexU16(value, out var v)) PhysicalRequestCanId = v; }
    }

    public ushort UsdtResponseCanId
    {
        get => Model.UsdtResponseCanId;
        set { if (Model.UsdtResponseCanId != value) { Model.UsdtResponseCanId = value; OnPropertyChanged(); OnPropertyChanged(nameof(UsdtResponseCanIdHex)); } }
    }

    public string UsdtResponseCanIdHex
    {
        get => $"0x{Model.UsdtResponseCanId:X3}";
        set { if (TryParseHexU16(value, out var v)) UsdtResponseCanId = v; }
    }

    public ushort UudtResponseCanId
    {
        get => Model.UudtResponseCanId;
        set { if (Model.UudtResponseCanId != value) { Model.UudtResponseCanId = value; OnPropertyChanged(); OnPropertyChanged(nameof(UudtResponseCanIdHex)); } }
    }

    public string UudtResponseCanIdHex
    {
        get => $"0x{Model.UudtResponseCanId:X3}";
        set { if (TryParseHexU16(value, out var v)) UudtResponseCanId = v; }
    }

    // ---------------- $1A ECU identity DIDs ----------------
    //
    // No grid surface in the inspector - the Bin menu's Load Info From Bin /
    // Auto-populate DIDs items are the user-facing way to populate DIDs.
    // Both writers push straight to EcuNode.SetIdentifier, the same storage
    // the $1A handler reads from at runtime. DIDs live in memory only
    // (v12 dropped IdentifierDto from the JSON schema); re-seed every
    // session via the Bin menu or File -> Prime from DPS archive.

    /// <summary>
    /// "Load Info From Bin" command, bound to the Advanced pane's bin-picker
    /// "Load..." button. Pops a file picker, parses the selected .bin via
    /// <see cref="Mode1ADidBinExtractor"/>, pushes the extracted identity fields
    /// into <see cref="EcuNode.Identifiers"/>, and records the bin as this ECU's
    /// flash source (<see cref="FlashBinPath"/>).
    /// </summary>
    public RelayCommand LoadInfoFromBinCommand { get; }

    /// <summary>Clears the recorded bin (display + flash source). Bound to the
    /// Advanced pane's "Clear" button; disabled when no bin is set.</summary>
    public RelayCommand ClearBinCommand { get; }

    /// <summary>
    /// Path of the .bin chosen as this ECU's flash source, backed by the
    /// round-tripping <see cref="EcuNode.FlashBinPath"/>. Set by the bin picker;
    /// the ford-uds persona reads the bytes to back Service $23.
    /// </summary>
    public string? FlashBinPath
    {
        get => Model.FlashBinPath;
        set
        {
            if (Model.FlashBinPath == value) return;
            Model.FlashBinPath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BinFileDisplay));
        }
    }

    /// <summary>Bare filename for the read-only bin textbox; full path is the tooltip.</summary>
    public string BinFileDisplay
        => string.IsNullOrEmpty(Model.FlashBinPath) ? "(no bin loaded)" : Path.GetFileName(Model.FlashBinPath);

    // Clears the recorded bin (display + flash source). Leaves already-materialised
    // $1A identity rows in place - they are normal editable rows once loaded.
    private void ClearBin()
    {
        FlashBinPath = null;
        if (Model.PersonaId == "ford-uds")
            Core.Protocol.FordUdsDispatch.LoadFlashBin((byte[]?)null);
    }

    private void LoadInfoFromBin()
    {
        var settings = AppSettings.Load();
        bool isFord = Model.PersonaId == "ford-uds";
        var picker = new OpenFileDialog
        {
            Title = isFord ? "Pick a Ford PCM flash image" : "Pick a GM ECU flash image",
            Filter = "ECU bin (*.bin)|*.bin|All files|*.*",
            InitialDirectory = AppSettings.ResolveInitialDir(settings.LastBinDir),
        };
        if (picker.ShowDialog() != true) return;

        // Persist the dir before we do any parsing - the user picked a real
        // file in that folder, so even if the bin turns out to be unreadable
        // / unrecognised we still want the next session to land there.
        var chosenDir = Path.GetDirectoryName(picker.FileName);
        if (!string.IsNullOrEmpty(chosenDir))
        {
            settings.LastBinDir = chosenDir;
            settings.Save();
        }

        // Ford persona: the bin is a Ford PCM (Spanish Oak) image with no GM service dispatcher or $1A DID
        // table, so the GM identity flow below (the "$1A DIDs" overwrite prompt + GM extractor) doesn't apply.
        // Take a Ford-shaped path that seeds the Mode 09 store from the bin instead.
        if (isFord)
        {
            LoadBinForFordPersona(picker.FileName);
            return;
        }

        // Ask the user which load mode to use BEFORE touching the file - if
        // they cancel, no work is done. Yes = replace-all (destructive but
        // explicit), No = merge (keeps user-edited / auto-populated values),
        // Cancel = bail.
        var modeChoice = MessageBox.Show(
            "是否用 bin 中的内容替换此 ECU 上所有现有的 $1A DID？\n\n" +
            "[是] 全部替换 - 先清除此 ECU 上的每个 DID，然后只写入" +
            "bin 中提取的内容。bin 无法提取的 DID 将最终未配置。\n\n" +
            "[否] 仅空白时添加 - 保留现有 DID；只填充当前为空的。" +
            "用户编辑和先前自动填充的值将保留。\n\n" +
            "[取消] 不加载。",
            "从 Bin 加载信息",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        if (modeChoice == MessageBoxResult.Cancel) return;
        var mode = modeChoice == MessageBoxResult.Yes
            ? BinIdentificationApplier.LoadMode.ReplaceAll
            : BinIdentificationApplier.LoadMode.Merge;

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(picker.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"无法读取文件：\n{ex.Message}", "从 Bin 加载信息",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // The picked bin is this GM ECU's flash source: record the path (round-trips via
        // ConfigStore.FlashBinPath). Done before identity parsing so the flash source sticks even if
        // extraction fails. (The Ford persona took its own branch above and never reaches here.)
        FlashBinPath = picker.FileName;

        var result = Mode1ADidBinExtractor.Parse(bytes);
        if (result == null)
        {
            MessageBox.Show(
                "无法将此文件识别为 GM ECU 闪存映像。未找到服务" +
                "调度器 - 文件可能被截断、加密，或属于" +
                "与 T43/E38/E67 不同的 ECU 系列。",
                "从 Bin 加载信息", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Delegate to the testable Core helper. It clears + writes the model
        // per the chosen mode and returns the lists for the summary dialog.
        var outcome = BinIdentificationApplier.Apply(Model, result, mode);
        var applied = outcome.Applied.ToList();
        var skipped = outcome.Skipped.ToList();
        if (!string.IsNullOrEmpty(result.CalibrationPartNumber))
            applied.Add($"Cal P/N ({result.CalibrationPartNumber} - not stored, no fixed DID)");

        // Surface the just-loaded identity as editable $1A rows. Apply() writes the runtime identifier
        // dictionary, but the editor's $1A section (and the $1A handler's preferred lookup) work off Mode1A
        // Pid rows - so without this the rows never appear and the synthetic seeded $90 placeholder would
        // even shadow the bin's real VIN on the wire.
        MaterializeIdentifiersToMode1ARows(mode);

        // Compose a summary message - shows which fields were populated and
        // surfaces any parser warnings (e.g. "no trampoline pattern detected").
        string modeLabel = mode == BinIdentificationApplier.LoadMode.ReplaceAll
            ? "全部替换 ($1A 行变为 bin 的精确集合)"
            : "合并 (添加 bin 值; 保留 bin 未提供的行)";
        var lines = new List<string>
        {
            $"Mode: {modeLabel}",
            $"Family: {result.Family}",
            $"Service dispatcher: 0x{result.ServiceDispatcherOffset:X6}",
            $"$1A handler: 0x{result.Service1AHandlerOffset:X6}",
            $"DID dispatcher: 0x{result.DidDispatcherOffset:X6}",
            $"Supported SIDs: {string.Join(", ", result.SupportedSids.Select(s => $"${s:X2}"))}",
            $"Supported DIDs: {string.Join(", ", result.Dids.Select(s => $"${s.Did:X2}"))}",
            "",
            applied.Count > 0
                ? $"Populated: {string.Join(", ", applied)}"
                : "No fields populated (no extractable values found).",
        };
        if (skipped.Count > 0)
        {
            lines.Add("");
            lines.Add($"Kept existing (precedence: user/auto-populate wins): {string.Join(", ", skipped)}");
        }
        if (result.Warnings.Count > 0)
        {
            lines.Add("");
            lines.Add("Warnings:");
            foreach (var w in result.Warnings) lines.Add("  - " + w);
        }

        MessageBox.Show(string.Join(Environment.NewLine, lines),
            "Load Info From Bin", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // Ford-persona bin load. The chosen .bin is a Ford PCM (Spanish Oak) image: it carries no GM service
    // dispatcher / $1A DID table, so the GM identity extractor doesn't apply and there are no $1A DIDs to
    // overwrite. Record it as the flash source, push it live so $23 ReadMemoryByAddress serves it this session,
    // then seed the persona's dedicated Mode 09 store - VIN (InfoType $02) from bin window 0x000100C0 and
    // Calibration ID (InfoType $04) from 0x00010046 - so a $09 reply matches a $23 read of the same address.
    private void LoadBinForFordPersona(string path)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"无法读取文件：\n{ex.Message}", "从 Bin 加载信息",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // Record + push the bin before seeding: SeedMode09Identity reads the VIN/CalID windows out of the
        // just-loaded flash backing (falling back to the known-good FG strings if a window isn't present).
        FlashBinPath = path;
        Core.Protocol.FordUdsDispatch.LoadFlashBin(bytes);
        Core.Protocol.FordUdsDispatch.SeedMode09Identity(Model);

        // Read back exactly what was seeded for the summary (same store $09 answers from).
        string vin   = AsciiOf(Model.GetMode09Info(0x02));
        string calId = AsciiOf(Model.GetMode09Info(0x04));

        MessageBox.Show(
            "Loaded Ford PCM flash image as this ECU's $23 source and seeded the Mode 09 identity " +
            "from the bin:\n\n" +
            $"VIN (Mode $09 InfoType $02, bin 0x000100C0): {vin}\n" +
            $"Calibration ID (Mode $09 InfoType $04, bin 0x00010046): {calId}",
            "Load Info From Bin", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // Render a Mode 09 store value as ASCII for the load summary; "(none)" when the InfoType is unset.
    private static string AsciiOf(byte[]? bytes)
        => bytes is { Length: > 0 } ? System.Text.Encoding.ASCII.GetString(bytes) : "(none)";

    // Mirrors the runtime identifier dictionary (just written by BinIdentificationApplier.Apply) into editable
    // Mode1A Pid rows so the bin's identity shows in the editor's $1A section, persists in config, and is the value
    // the $1A handler returns (the handler prefers Mode1A rows over the dict, so the row MUST carry the bin value or
    // a stale seeded placeholder would shadow it). The bin is the source the user explicitly chose, so it overwrites
    // any existing row for the same DID; ReplaceAll additionally drops $1A rows the bin didn't surface.
    private void MaterializeIdentifiersToMode1ARows(BinIdentificationApplier.LoadMode mode)
    {
        if (mode == BinIdentificationApplier.LoadMode.ReplaceAll)
            foreach (var vm in Pids.Where(p => p.Model.Mode == PidMode.Mode1A).ToList())
                RemovePid(vm);

        foreach (var did in Model.Identifiers.Keys.OrderBy(d => d))
        {
            var data = Model.GetIdentifier(did);
            if (data is null || data.Length == 0) continue;

            // Overwrite any existing row for this DID (e.g. the seeded $90 placeholder) with the bin value.
            var existing = Pids.FirstOrDefault(p => p.Model.Mode == PidMode.Mode1A
                                                 && (byte)(p.Model.Address & 0xFF) == did);
            if (existing != null) RemovePid(existing);

            var pid = new Pid
            {
                Mode        = PidMode.Mode1A,
                Address     = did,
                Name        = Gmw3110DidNames.NameOf(did) ?? $"DID {did:X2}",
                StaticBytes = data,
                LengthBytes = data.Length,
                Size        = PidSize.DWord,
                DataType    = PidDataType.Unsigned,
            };
            Model.AddPid(pid);
            Pids.Add(new PidViewModel(pid, this));
        }

        foreach (var s in Sections) s.Refresh();
    }

    /// <summary>
    /// True iff the model has a non-empty byte array stored for this DID.
    /// Used by the precedence guard in <see cref="AutoPopulateMissingDids"/>
    /// so the auto-populate pass doesn't clobber a higher-precedence value
    /// (user hand-edit and bin-load both take precedence over defaults).
    /// </summary>
    private bool HasIdentifier(byte did)
    {
        var bytes = Model.GetIdentifier(did);
        return bytes != null && bytes.Length > 0;
    }

    /// <summary>
    /// "Auto-populate missing $1A DIDs" command. Prompts the user for one of
    /// three modes, then fills well-known DIDs (<see cref="Gmw3110DidNames.KnownDids"/>)
    /// with placeholder values from <see cref="DefaultDidValues"/>. DIDs that
    /// already have a non-empty value are NEVER overwritten - they stay even
    /// in the aggressive mode. The two modes differ in how they treat
    /// sticky-user blanks (source=User with no bytes - the user explicitly
    /// cleared a row).
    /// </summary>
    public RelayCommand AutoPopulateDidsCommand { get; }

    private void AutoPopulateMissingDids()
    {
        // Three-way prompt: Yes = aggressive (fill all blanks including ones
        // the user deliberately cleared), No = conservative (default - keep
        // sticky-user blanks), Cancel = bail with no model change.
        var choice = MessageBox.Show(
            "How should Auto-populate handle DIDs the user previously cleared?\n\n" +
            "[Yes] Overwrite user-blank fields - fill every well-known DID that is " +
            "currently empty, including ones the user explicitly cleared (the sticky-User " +
            "rule is ignored for this run; rows already containing a value still aren't " +
            "touched).\n\n" +
            "[No] Populate blanks - only fill DIDs that are blank AND not tagged " +
            "source=user. Rows the user deliberately cleared stay empty.\n\n" +
            "[Cancel] Don't change anything.",
            "Auto-populate DIDs",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        if (choice == MessageBoxResult.Cancel) return;
        bool overwriteUserBlanks = choice == MessageBoxResult.Yes;

        int populated = 0;
        int respectedUserBlanks = 0;
        foreach (var did in Common.Protocol.Gmw3110DidNames.KnownDids)
        {
            // Existing values are never overwritten by Auto-populate - the
            // user owns rows with content regardless of mode.
            if (HasIdentifier(did)) continue;

            // Sticky-User blanks: skip in conservative mode; fill in aggressive.
            // Auto / Bin / Blank sources always get the default.
            if (!overwriteUserBlanks &&
                Model.GetIdentifierSource(did) == Common.Protocol.DidSource.User)
            {
                respectedUserBlanks++;
                continue;
            }

            var bytes = Common.Protocol.DefaultDidValues.Get(did);
            if (bytes == null || bytes.Length == 0) continue;
            Model.SetIdentifier(did, bytes, Common.Protocol.DidSource.Auto);
            populated++;
        }

        if (populated == 0)
        {
            var msg = respectedUserBlanks > 0
                ? $"Nothing to do - every well-known DID already has a value or " +
                  $"is a user-blanked row. {respectedUserBlanks} row(s) tagged " +
                  $"source=user were preserved; re-run and pick 'Overwrite user-blank " +
                  $"fields' to fill those too."
                : "Nothing to do - every well-known DID already has a value. " +
                  "Edit the JSON config to clear an entry and re-run to re-fill " +
                  "it with the placeholder default.";
            MessageBox.Show(msg, "Auto-populate DIDs",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    /// <summary>FC.BS byte sent on First Frame reception. Hex display, e.g. "0x01".</summary>
    public string FlowControlBlockSizeHex
    {
        get => $"0x{Model.FlowControlBlockSize:X2}";
        set
        {
            if (TryParseHexByte(value, out var v) && Model.FlowControlBlockSize != v)
            {
                Model.FlowControlBlockSize = v;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// 8-bit diagnostic address returned by $1A $B0. Hex display, e.g. "0x11".
    /// Typically the low byte of PhysicalRequestCanId.
    /// </summary>
    public string DiagnosticAddressHex
    {
        get => $"0x{Model.DiagnosticAddress:X2}";
        set
        {
            if (TryParseHexByte(value, out var v) && Model.DiagnosticAddress != v)
            {
                Model.DiagnosticAddress = v;
                OnPropertyChanged();
            }
        }
    }

    private static bool TryParseHexByte(string s, out byte v)
    {
        var trimmed = (s ?? "").Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed[2..];
        return byte.TryParse(trimmed, System.Globalization.NumberStyles.HexNumber,
                             System.Globalization.CultureInfo.InvariantCulture, out v);
    }

    /// <summary>
    /// Per-block delay (ms) applied to every $36 TransferData response so a flash
    /// write takes a realistic time instead of finishing in &lt;10 s. 0 = instant.
    /// Honoured by all personas. Keep well under the tester's read timeout
    /// (~2.5 s) - per-block pacing is the lever, not one giant delay.
    /// </summary>
    public int FlashTransferDelayMs
    {
        get => Model.FlashTransferDelayMs;
        set
        {
            var v = value < 0 ? 0 : value;
            if (Model.FlashTransferDelayMs != v)
            {
                Model.FlashTransferDelayMs = v;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Modelled erase duration (ms). The erase positive response is simply
    /// deferred by this much (the ECU goes quiet then answers when done) - no
    /// ResponsePending, which PCMTec rejects for $B1. 0 = instant. Honoured by
    /// all personas (Ford $B1, GM SPS $31 $FF00).
    /// </summary>
    public int FlashEraseDelayMs
    {
        get => Model.FlashEraseDelayMs;
        set
        {
            var v = value < 0 ? 0 : value;
            if (Model.FlashEraseDelayMs != v)
            {
                Model.FlashEraseDelayMs = v;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// When ticked, a $23 ReadMemoryByAddress for an address beyond the loaded
    /// flash bin (RAM) is answered with a positive zero-filled response instead
    /// of NRC $31 RequestOutOfRange. Applies to every persona. Default off.
    /// </summary>
    public bool RamReadReturnsZeros
    {
        get => Model.RamReadReturnsZeros;
        set
        {
            if (Model.RamReadReturnsZeros != value)
            {
                Model.RamReadReturnsZeros = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// When ticked, a Ford $A1 SETUP_DMR for a RAM address that has no row in the
    /// $A1 grid is rejected with NRC $31 RequestOutOfRange instead of the positive
    /// E1 echo. Ford UDS persona only. Default off (accept any address) so the
    /// capture/datalog flow keeps working without pre-wiring every address.
    /// </summary>
    public bool RejectUnmappedDmr
    {
        get => Model.RejectUnmappedDmr;
        set
        {
            if (Model.RejectUnmappedDmr != value)
            {
                Model.RejectUnmappedDmr = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Modelled processing latency (ms) applied to every diagnostic response on
    /// this ECU. 0 = instant (the default). Above the active stack's P2 the ECU
    /// emits 7F sid 78 ResponsePending heartbeats to P2* (unless
    /// <see cref="Emit78WhenSlow"/> is off). See EcuNode.ResponseDelayMs.
    /// </summary>
    public int ResponseDelayMs
    {
        get => Model.ResponseDelayMs;
        set
        {
            var v = value < 0 ? 0 : value;
            if (Model.ResponseDelayMs != v)
            {
                Model.ResponseDelayMs = v;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Emit 7F sid 78 RCR-RP when a response is slower than P2 (default on,
    /// spec-correct). Off models an ECU that goes quiet then answers when done -
    /// needed for hosts that reject a pending reply. See EcuNode.Emit78WhenSlow.
    /// </summary>
    public bool Emit78WhenSlow
    {
        get => Model.Emit78WhenSlow;
        set
        {
            if (Model.Emit78WhenSlow != value)
            {
                Model.Emit78WhenSlow = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Per-ECU override (ms) of the session (P3C / S3) timeout, or 0 to use the
    /// active stack's TimingProfile default. See EcuNode.SessionTimeoutOverrideMs.
    /// </summary>
    public int SessionTimeoutOverrideMs
    {
        get => Model.SessionTimeoutOverrideMs ?? 0;
        set
        {
            var v = value < 0 ? 0 : value;
            int? stored = v == 0 ? null : v;
            if (Model.SessionTimeoutOverrideMs != stored)
            {
                Model.SessionTimeoutOverrideMs = stored;
                OnPropertyChanged();
            }
        }
    }

    // The single "active row" across all sections. The waveform inspector and the per-ECU Remove route through this;
    // each section's grid drives it via NotifySectionSelected. Setting it from code (e.g. after Add) does NOT clear
    // the sections - NotifySectionSelected owns the cross-section deselect.
    public PidViewModel? SelectedPid
    {
        get => selectedPid;
        set => SetField(ref selectedPid, value);
    }

    // Called by a section when the user selects one of its rows. Promotes the row to the shared SelectedPid and clears
    // every other section's selection so only one row is highlighted across the whole editor.
    internal void NotifySectionSelected(PidModeSection source, PidViewModel pid)
    {
        SelectedPid = pid;
        foreach (var s in Sections)
            if (!ReferenceEquals(s, source)) s.ClearSelection();
    }

    // Add a new PID into a specific mode's section. The Mode column is gone, so the section's Add button stamps its
    // mode here; the new row appears in that section (and only that section) because each section's view filters by
    // Mode. Catalogue-driven modes ($1A / $22) start on a placeholder address the user re-points via the Identifier
    // dropdown; $2D rows start on the next free byte range so a hand-rolled address doesn't overlap an existing one.
    public void AddPid(PidMode mode)
    {
        // Default the new PID to Word (2 bytes). Pick the next address that
        // doesn't overlap an existing PID's [Address, Address+ResponseLength)
        // range - simulator addresses are byte-granular (see Service2D's
        // 32-bit memory addressing), so a Word at 0x0001 occupies bytes
        // 0x0001..0x0002 and the next Word has to start at 0x0003. Walks
        // from 0x0001 and skips forward over each occupied span until a gap
        // of at least newSize bytes is found.
        const PidSize NewSize = PidSize.Word;
        int newSize = (int)NewSize;
        var occupied = Pids
            .Select(p => (Start: p.Model.Address, End: p.Model.Address + (uint)p.Model.ResponseLength))
            .OrderBy(s => s.Start)
            .ToList();
        uint addr = 0x0001;
        foreach (var (start, end) in occupied)
        {
            if (addr + newSize <= start) break;     // gap fits before this PID
            if (end > addr) addr = end;             // skip past the occupied span
        }

        // Pick a unique name: walk "New PID N" upwards until none collides
        // with an existing PID name. Bare "New PID" stays the first label so
        // single-PID configs don't get a number suffix gratuitously.
        var existingNames = Pids.Select(p => p.Model.Name).ToHashSet(StringComparer.Ordinal);
        string name = "新 PID";
        if (existingNames.Contains(name))
        {
            int n = 2;
            while (existingNames.Contains($"新 PID {n}")) n++;
            name = $"New PID {n}";
        }

        var pid = new Pid
        {
            Address = addr,
            Name = name,
            Size = NewSize,
            DataType = PidDataType.Unsigned,
            Mode = mode,
            Scalar = 1.0,
            Offset = 0.0,
            Unit = "",
            // A fresh row has no live source - it reads 0 until the user picks a signal or "Waveform" in the Signal
            // column. The waveform config is still seeded with a sensible visible shape so picking "Waveform" produces
            // something immediately, but it stays dormant under ValueSource.None.
            ValueSource = PidValueSource.None,
            WaveformConfig = new WaveformConfig { Shape = WaveformShape.Sin, Amplitude = 50, Offset = 50, FrequencyHz = 1.0 },
        };
        // AddPid routes by pid.Mode into the right per-mode store.
        Model.AddPid(pid);
        var vm = new PidViewModel(pid, this);
        Pids.Add(vm);                       // ListCollectionView in each section re-flows; only the matching one shows it
        RefreshAliasCollisions();
        NotifyIdentifierSetChanged();
        var section = Sections.FirstOrDefault(s => s.Mode == mode);
        if (section != null) section.SelectedPid = vm;   // selects in-section + promotes to SelectedPid
        else SelectedPid = vm;
    }

    // Back-compat no-arg add (MainViewModel's top toolbar): default to a $22 row.
    public void AddPid() => AddPid(PidMode.Mode22);

    // Explicit, opt-in seeding of the curated default PID set onto THIS ECU (the editor's "Seed default PIDs" button).
    // Runs the same two seeders that used to fire automatically on Add ECU / config load, then wraps any rows they
    // added in PidViewModels so the editor grid shows them. Both seeders are precedence-safe (existing DIDs win) and
    // skip primed ECUs, so this is a no-op on an ECU that already carries the full set and never clobbers a user edit.
    // Returns the number of rows actually added.
    public int SeedDefaultPids()
    {
        if (Model.IsPrimed) return 0;

        // The seeders mutate the EcuNode stores directly, so capture the model refs we already wrap and diff after.
        var wrapped = new HashSet<Pid>(Pids.Select(p => p.Model));
        EcuIdentitySeeder.Seed(Model);
        EcuMode22Seeder.Seed(Model);

        int added = 0;
        foreach (var pid in Model.AllPids)
        {
            if (wrapped.Contains(pid)) continue;
            Pids.Add(new PidViewModel(pid, this));   // section ListCollectionViews re-flow; only the matching one shows it
            added++;
        }
        if (added > 0)
        {
            RefreshAliasCollisions();
            NotifyIdentifierSetChanged();
        }
        return added;
    }

    // The per-mode store keys currently occupied by rows in <paramref name="mode"/>, optionally excluding one row.
    // Two rows that share a key collide in EcuNode's store (last write wins, the rest go silent on the wire), so the
    // editor consults this to keep each identifier unique - the $22 catalogue picker hides taken identifiers and the
    // $1A/$2D address box rejects a typed duplicate.
    internal HashSet<uint> IdentifiersInUse(PidMode mode, Pid? exclude = null)
    {
        var set = new HashSet<uint>();
        foreach (var vm in Pids)
            if (vm.Model.Mode == mode && !ReferenceEquals(vm.Model, exclude))
                set.Add(vm.Model.StoreKey);
        return set;
    }

    // True when a row other than <paramref name="self"/> in <paramref name="mode"/> already serves the identifier
    // <paramref name="address"/> maps to.
    internal bool IsIdentifierTaken(Pid self, PidMode mode, uint address)
        => IdentifiersInUse(mode, exclude: self).Contains(Pid.StoreKeyFor(mode, address));

    // Re-announce each row's IdentifierCatalogue so the $22 picker drops (or restores) identifiers as rows claim or
    // release them. Called after a structural identifier change (add / remove / address edit / bulk replace), not on
    // every field edit, so the per-row filtering stays cheap.
    private void NotifyIdentifierSetChanged()
    {
        foreach (var vm in Pids) vm.RefreshIdentifierCatalogue();
    }

    // Remove a specific row (the section Remove buttons call this with the section's selection).
    public void RemovePid(PidViewModel vm)
    {
        // RemovePid routes to the correct per-mode store based on Pid.Mode.
        Model.RemovePid(vm.Model);
        Pids.Remove(vm);
        foreach (var s in Sections) s.ClearSelection();
        if (ReferenceEquals(selectedPid, vm)) SelectedPid = null;
        RefreshAliasCollisions();
        NotifyIdentifierSetChanged();
    }

    public void RemoveSelectedPid()
    {
        if (selectedPid != null) RemovePid(selectedPid);
    }

    /// <summary>
    /// Called by PidViewModel.Mode when the user flips a row's mode. The
    /// underlying Pid object stays the same; only its EcuNode-side storage
    /// location changes - <see cref="EcuNode.RelocatePidMode"/> handles the
    /// move atomically. The Pids ObservableCollection isn't touched, so the
    /// same VM entry keeps rendering the same model.
    /// </summary>
    public void OnPidModeChanged(Pid pid, PidMode oldMode, PidMode newMode)
    {
        if (oldMode == newMode) return;
        // PidViewModel has already set pid.Mode = newMode by the time we get
        // here, so pass oldMode explicitly to find the source store.
        Model.RelocatePidMode(pid, oldMode);
    }

    // Called by PidViewModel.Address when the user edits a row's address. The per-mode stores are keyed by Address, so
    // the model has to re-key the entry. Otherwise GetPid / GetPidByWireId miss after the edit and the ECU NRCs the
    // request - e.g. a $2D or $22 read returns RequestOutOfRange. PidViewModel has already written the new address onto
    // the model, so pass the prior address to find the existing entry.
    internal void OnPidAddressChanged(Pid pid, uint oldAddress)
    {
        Model.RekeyPidAddress(pid, oldAddress);
        NotifyIdentifierSetChanged();
    }

    /// <summary>
    /// Atomically replace this ECU's PID list with <paramref name="loaded"/>.
    /// Used by the SetupWindow's Load PIDs button: clears the model + VM
    /// collection, then appends each new PID through the same pipeline
    /// AddPid uses so the model's per-mode stores and the observable list
    /// stay in sync.
    /// </summary>
    public void ReplacePids(IEnumerable<Pid> loaded)
    {
        // RemovePid routes by Pid.Mode into the right store.
        foreach (var existing in Pids.Select(p => p.Model).ToList())
            Model.RemovePid(existing);
        Pids.Clear();
        SelectedPid = null;
        foreach (var pid in loaded)
        {
            // AddPid routes by pid.Mode into the right store.
            Model.AddPid(pid);
            Pids.Add(new PidViewModel(pid, this));
        }
        foreach (var s in Sections) s.Refresh();   // re-apply each section's filter + sort to the swapped-in rows
        NotifyIdentifierSetChanged();
        RaisePidsChanged();
    }

    public void RaisePidsChanged()
    {
        RefreshAliasCollisions();
        Model.RaisePidsChanged();
    }

    // Walks Mode2D rows and flags any pair that derives to the same wire
    // alias (0xF000 | (addr & 0x0FFF)). Cleared on every recompute so
    // resolving a collision wipes both rows' warnings on the next tick.
    // Cheap O(N) - the typical PID list is < 50 rows; no need to memoise.
    private void RefreshAliasCollisions()
    {
        var aliasToRows = new Dictionary<ushort, List<PidViewModel>>();
        foreach (var vm in Pids)
        {
            if (vm.Model.Mode != PidMode.Mode2D) { vm.HasAliasCollision = false; vm.AliasCollisionTooltip = null; continue; }
            ushort alias = (ushort)(0xF000 | (vm.Model.Address & 0x0FFF));
            if (!aliasToRows.TryGetValue(alias, out var list))
                aliasToRows[alias] = list = new List<PidViewModel>();
            list.Add(vm);
        }
        foreach (var (alias, rows) in aliasToRows)
        {
            bool collision = rows.Count > 1;
            string? tip = collision
                ? $"$2D alias 0x{alias:X4} is shared by {rows.Count} rows ("
                  + string.Join(", ", rows.Select(r => $"0x{r.Model.Address:X8}"))
                  + "). The first matching row wins on the $22 wire; the others are unreachable."
                : null;
            foreach (var r in rows)
            {
                r.HasAliasCollision = collision;
                r.AliasCollisionTooltip = tip;
            }
        }
    }

    private static bool TryParseHexU16(string s, out ushort v)
    {
        var trimmed = (s ?? "").Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed[2..];
        return ushort.TryParse(trimmed, System.Globalization.NumberStyles.HexNumber,
                               System.Globalization.CultureInfo.InvariantCulture, out v);
    }

    // ---------------- Persona (diagnostic dispatch table) ----------------

    // Shared, immutable option set. Instance property below exposes it for the
    // ComboBox - WPF instance-path bindings ({Binding AvailablePersonas}) don't
    // resolve static properties, so the public surface must be an instance member.
    private static readonly IReadOnlyList<PersonaOption> SharedPersonas = new[]
    {
        new PersonaOption("ford-uds", "Ford"),
        new PersonaOption("gmw3110",      "GM 第4代"),
    };

    /// <summary>
    /// Diagnostic personas a user can pick for THIS ECU in the Advanced tab.
    /// Only the two shipping dispatch tables are offered; uds-kernel is
    /// runtime-only (Service36Handler swaps it in on a $36 sub $80 kernel
    /// boot-load) and isn't a user-pickable choice.
    /// </summary>
    public IReadOnlyList<PersonaOption> AvailablePersonas => SharedPersonas;

    // ---------------- Flash-READ dialect ("Read as", GM persona only) ----------------

    private static readonly IReadOnlyList<ReadFamilyOption> SharedReadFamilies = new[]
    {
        new ReadFamilyOption(ReadKernelFamily.E38E67, "E38 / E67"),
        new ReadFamilyOption(ReadKernelFamily.T43,    "T43"),
    };

    /// <summary>
    /// Flash-READ dialects a user can pick for THIS ECU in the Advanced tab
    /// (shown under the security-module dropdown, GM persona only). Decides how a
    /// $35/$36 read is answered: E38/E67 = PowerPCM native upload, T43 = the
    /// 6Speed.T43 read-kernel. See <see cref="ReadKernelFamily"/>.
    /// </summary>
    public IReadOnlyList<ReadFamilyOption> AvailableReadFamilies => SharedReadFamilies;

    /// <summary>
    /// Selected flash-READ dialect for this ECU. Getter reflects the live
    /// Model.ReadFamily; setter changes it on this ECU only. Purely a config
    /// knob - it doesn't touch security or dispatch bindings, so no reset is
    /// needed. Persisted per-ECU via EcuDto.ReadFamily.
    /// </summary>
    public ReadFamilyOption? SelectedReadFamily
    {
        get => AvailableReadFamilies.FirstOrDefault(o => o.Id == Model.ReadFamily);
        set
        {
            if (value is null || value.Id == Model.ReadFamily) return;
            Model.ReadFamily = value.Id;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// The diagnostic standard set this single ECU speaks, bound two-way to the
    /// Advanced-tab dropdown. Getter reflects the live Model.PersonaId (so a
    /// config load with PersonaId = "ford-uds" shows "Ford"); setter changes the
    /// standard on this ECU only - which re-synthesises its protocol-stack bindings -
    /// and resets its security state, the same way a security-module change does,
    /// so a prior standard's unlock can't leak across. An unrecognised id reads
    /// back as null, leaving the dropdown blank until the user picks one.
    /// </summary>
    public PersonaOption? SelectedPersonaOption
    {
        get => AvailablePersonas.FirstOrDefault(o => o.Id == Model.PersonaId);
        set
        {
            if (value is null) return;
            if (value.Id == Model.PersonaId) return;
            Model.PersonaId = value.Id;
            OnPropertyChanged();
            // The DMR signal map section is Ford-persona-only and the "Read as"
            // flash-read picker is GM-persona-only; re-announce both visibilities.
            OnPropertyChanged(nameof(IsFordUdsPersona));
            OnPropertyChanged(nameof(IsGmPersona));
            // GMW3110-only PID-mode sections ($1A / $2D) hide under the Ford persona and reappear under GM.
            RefreshSectionVisibility();
            // The $22 Identifier picker is persona-scoped (GM vs Ford library),
            // so re-announce every row's catalogue to repopulate the dropdowns.
            NotifyIdentifierSetChanged();
            // Spec-aligned with ApplyModuleSelection: replacing the dispatch
            // table is a fresh start, so clear any unlock / pending seed /
            // lockout state left over from the previous persona.
            ResetSecurityState();
            // A persona change replaces the bound standards. Drop any service
            // overrides keyed to the previous persona's standards so they don't
            // linger in serviceOverrides and get persisted as stale Stacks
            // entries (e.g. GMW3110 / J1979 deltas surviving a switch to Ford,
            // where they are inert at dispatch but misrepresent the saved config).
            var keep = new HashSet<string>(
                Model.SynthesizedDefaults().Select(d => d.Stack.Standard), StringComparer.Ordinal);
            foreach (var standard in Model.ServiceOverrides?.Keys.ToList() ?? new List<string>())
                if (!keep.Contains(standard))
                    Model.SetServiceOverride(standard, null);
        }
    }

    // ---------------- Security ($27) ----------------

    private const string NoneSecurityModuleLabel = "(无)";

    /// <summary>All registered module IDs, prefixed with a synthetic "(无)" entry.</summary>
    public ObservableCollection<string> AvailableSecurityModuleIds { get; }

    /// <summary>Editable key→string map for the module's SecurityModuleConfig JsonElement.</summary>
    public ObservableCollection<KeyValueEntry> SecurityModuleConfigEntries { get; }

    // ---- Live security state (refreshed from MainWindow refresh timer) ----

    private string securityStatusText = "已锁定";
    public string SecurityStatusText
    {
        get => securityStatusText;
        private set => SetField(ref securityStatusText, value);
    }

    // Short, pill-friendly variant of SecurityStatusText for the titlebar
    // pill. The Security tab still uses SecurityStatusText so the level /
    // remaining-time detail stays visible there.
    private string securityPillText = "ECU 已锁定";
    public string SecurityPillText
    {
        get => securityPillText;
        private set => SetField(ref securityPillText, value);
    }

    private string securityFailedAttemptsText = "0 / 3";
    public string SecurityFailedAttemptsText
    {
        get => securityFailedAttemptsText;
        private set => SetField(ref securityFailedAttemptsText, value);
    }

    private string securityPendingSeedText = "(无)";
    public string SecurityPendingSeedText
    {
        get => securityPendingSeedText;
        private set => SetField(ref securityPendingSeedText, value);
    }

    private string securityProgSessionText = "(无模块)";
    public string SecurityProgSessionText
    {
        get => securityProgSessionText;
        private set => SetField(ref securityProgSessionText, value);
    }

    /// <summary>
    /// Simulated power-cycle for this ECU. Combines the spec-defined $20
    /// ReturnToNormalMode exit (clears programming/download/$28/$A5 state and
    /// emits the unsolicited $60 if a host is still attached) with a full
    /// security re-lock. This is the canonical behaviour for any "Reset ECU
    /// state" button across the workspace tabs - keep all such buttons routed
    /// through here so they stay in sync.
    ///
    /// Note: EcuExitLogic.Run already re-locks security per GMW3110 §8.5.6.2
    /// (both the $20 and P3C-timeout branches end locked); the ResetSecurityState
    /// call here is what pushes the immediate UI refresh after that re-lock.
    /// </summary>
    public void ResetEcuState(DpidScheduler scheduler)
    {
        var channel = Model.State.LastEnhancedChannel;
        EcuExitLogic.Run(Model, scheduler, channel);
        ResetSecurityState();
    }

    /// <summary>
    /// Re-locks the ECU and clears every transient $27 field on its NodeState
    /// (unlocked level, pending seed, failed-attempt counter, lockout deadline,
    /// module-private bookkeeping). Equivalent to a power-cycle for this one
    /// ECU's security subsystem. Bound to the "Reset state" button in the
    /// Security tab.
    /// </summary>
    public void ResetSecurityState()
    {
        // Delegates to NodeState.ResetSecurity so this button and the spec
        // teardown (EcuExitLogic.Run) share one re-lock implementation.
        Model.State.ResetSecurity();
        // The 10Hz refresh tick would catch this within 100ms; push an
        // immediate update so the click feels instant.
        RefreshSecurity(0);
    }

    // Called from the main refresh timer to refresh the $01 section's live wire-byte readout.
    public void RefreshObd2Live(double timeMs)
    {
        foreach (var row in Obd2Pids) row.RefreshLive(timeMs);
    }

    /// <summary>Called from the main refresh timer to update the live security display.</summary>
    public void RefreshSecurity(long nowMs)
    {
        var s = Model.State;
        if (s.IsInLockout(nowMs))
        {
            double remainingSec = (s.SecurityLockoutUntilMs - nowMs) / 1000.0;
            SecurityStatusText = $"锁定中 - 剩余 {remainingSec:F1} 秒";
            SecurityPillText   = "ECU 锁定中";
        }
        else if (s.SecurityUnlockedLevel > 0)
        {
            SecurityStatusText = $"已解锁 (级别 {s.SecurityUnlockedLevel})";
            SecurityPillText   = "ECU 已解锁";
        }
        else
        {
            SecurityStatusText = "已锁定";
            SecurityPillText   = "ECU 已锁定";
        }

        SecurityFailedAttemptsText = $"{s.SecurityFailedAttempts} / 3";

        var seed = s.SecurityLastIssuedSeed;
        if (s.SecurityPendingSeedLevel == 0 || seed is null)
        {
            SecurityPendingSeedText = "(无)";
        }
        else
        {
            SecurityPendingSeedText =
                $"级别 {s.SecurityPendingSeedLevel}, 种子 = {string.Join(" ", seed.Select(b => b.ToString("X2")))}";
        }

        // Module's programming-session policy + the live shortcut flag that
        // gates the bypass path in Gmw3110_2010_Generic. The shortcut flag is
        // set by $10 $02 or by the full $28 + $A5 $01/$02 + $A5 $03 chain; it
        // only changes the wire behaviour when the module's policy is
        // BypassAll.
        var module = Model.SecurityModule;
        if (module is null)
        {
            SecurityProgSessionText = "(no module)";
        }
        else
        {
            bool inProgSession = s.SecurityProgrammingShortcutActive;
            SecurityProgSessionText = module.Behaviour switch
            {
                SecurityModuleBehaviour.BypassAll => inProgSession
                    ? "Bypass (in prog session)"
                    : "Bypass (not in prog session)",
                SecurityModuleBehaviour.Strict => inProgSession
                    ? "Enforce seed/key (in prog session)"
                    : "Enforce seed/key (not in prog session)",
                _ => module.Behaviour.ToString(),
            };
        }
    }

    private string selectedSecurityModuleId;
    public string SelectedSecurityModuleId
    {
        get => selectedSecurityModuleId;
        set
        {
            if (selectedSecurityModuleId == value) return;
            selectedSecurityModuleId = value;
            OnPropertyChanged();
            ApplyModuleSelection();
        }
    }

    private void ApplyModuleSelection()
    {
        if (selectedSecurityModuleId == NoneSecurityModuleLabel)
        {
            Model.SecurityModule = null;
        }
        else
        {
            Model.SecurityModule = SecurityModuleRegistry.Create(selectedSecurityModuleId);
            Model.SecurityModule?.LoadConfig(Model.SecurityModuleConfig);
        }
        // Spec-aligned: a real ECU's security state is bound to its security
        // module's lifetime. Replacing the module means a fresh start - any
        // unlocked level, pending seed, failed-attempt counter, or module-
        // private bookkeeping from the prior module is now stale and would
        // silently mask the new module's behaviour (e.g. a prior BypassAll
        // unlock makes the new algorithm's $27 short-circuit through the
        // "already unlocked -> seed=00 00" branch without ever running).
        ResetSecurityState();
    }

    private void OnSecurityEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
            foreach (KeyValueEntry old in e.OldItems) old.PropertyChanged -= OnSecurityEntryPropertyChanged;
        if (e.NewItems != null)
            foreach (KeyValueEntry n in e.NewItems) n.PropertyChanged += OnSecurityEntryPropertyChanged;
        PushEntriesToModel();
    }

    private void OnSecurityEntryPropertyChanged(object? sender, PropertyChangedEventArgs e)
        => PushEntriesToModel();

    private void PushEntriesToModel()
    {
        Model.SecurityModuleConfig = BuildJsonFromEntries();
        Model.SecurityModule?.LoadConfig(Model.SecurityModuleConfig);
    }

    private void LoadEntriesFromJson(JsonElement? json)
    {
        SecurityModuleConfigEntries?.Clear();
        if (json is null || json.Value.ValueKind != JsonValueKind.Object) return;
        foreach (var prop in json.Value.EnumerateObject())
            SecurityModuleConfigEntries!.Add(new KeyValueEntry(prop.Name, ValueToDisplayString(prop.Value)));
    }

    private JsonElement? BuildJsonFromEntries()
    {
        // Every entry value persists as a JSON string - modules parse strings
        // themselves (hex bytes, integers, etc.). Round-tripping a number-typed
        // load lands as a stringified number; modules that care can parse it.
        var dict = new Dictionary<string, string>();
        foreach (var e in SecurityModuleConfigEntries)
        {
            if (string.IsNullOrWhiteSpace(e.Key)) continue;
            dict[e.Key] = e.Value ?? "";
        }
        if (dict.Count == 0) return null;
        return JsonSerializer.SerializeToElement(dict);
    }

    private static string ValueToDisplayString(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString() ?? "",
        JsonValueKind.Number => e.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "",
        _ => e.GetRawText(),
    };

}

public sealed class KeyValueEntry : NotifyPropertyChangedBase
{
    private string key = "";
    private string value = "";

    // Parameterless ctor lets the DataGrid create new rows when CanUserAddRows=True.
    public KeyValueEntry() { }
    public KeyValueEntry(string key, string value) { this.key = key; this.value = value; }

    public string Key
    {
        get => key;
        set => SetField(ref key, value);
    }

    public string Value
    {
        get => value;
        set => SetField(ref this.value, value);
    }
}
