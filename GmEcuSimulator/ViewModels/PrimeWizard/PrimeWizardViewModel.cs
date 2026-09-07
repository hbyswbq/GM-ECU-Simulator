using Core.Bus;
using Core.Dps;
using Core.Ecu;
using Core.Security;
using System.Text.Json;
using System.Windows;

namespace GmEcuSimulator.ViewModels.PrimeWizard;

public enum PrimeWizardStep
{
    Archive,
    Phase3Review,
    Commit,
}

// Orchestrates the three-page DPS prime wizard. Owns the shared context
// and the per-page view models; the host window binds Back/Next/Cancel
// to the commands here. Apply runs on the Commit page's primary button.
//
// Page transitions are explicit so each page's "on enter" hook can
// rebuild downstream state when an earlier page mutated something -
// notably page 2's manifest, which is rebuilt every time the archive
// changes.
public sealed class PrimeWizardViewModel : NotifyPropertyChangedBase
{
    private readonly VirtualBus bus;
    private PrimeWizardStep currentStep;
    private bool isCompleted;     // set true on successful Apply; window closes on next dispatcher tick

    public PrimeWizardContext Context { get; }
    public Page1ArchiveViewModel ArchivePage { get; }
    public Page2Phase3ViewModel Phase3Page { get; }
    public Page3CommitViewModel CommitPage { get; }

    public RelayCommand BackCommand { get; }
    public RelayCommand NextCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ApplyCommand { get; }

    public event Action? RequestClose;
    public EcuNode? CommittedNode { get; private set; }
    public PrimedDataset? CommittedDataset { get; private set; }

    public PrimeWizardViewModel(
        VirtualBus bus,
        EcuNode? existingNode = null,
        PrimeWizardContext? priorContext = null)
    {
        this.bus = bus;

        Context = priorContext ?? new PrimeWizardContext();
        Context.ExistingNode = existingNode;

        ArchivePage = new Page1ArchiveViewModel(Context, OnContextChanged);
        Phase3Page = new Page2Phase3ViewModel(Context, OnContextChanged);
        CommitPage = new Page3CommitViewModel(Context);

        BackCommand   = new RelayCommand(GoBack,   () => currentStep != PrimeWizardStep.Archive && !isCompleted);
        NextCommand   = new RelayCommand(GoNext,   CanGoNext);
        CancelCommand = new RelayCommand(Cancel);
        ApplyCommand  = new RelayCommand(Apply,    () => currentStep == PrimeWizardStep.Commit && !isCompleted);

        currentStep = PrimeWizardStep.Archive;

        // Pre-populate re-edit state on construction so page 1 opens with
        // the prior archive already selected.
        if (Context.ArchivePath is not null)
            ArchivePage.RestoreFromContext();
    }

    public PrimeWizardStep CurrentStep
    {
        get => currentStep;
        private set
        {
            if (SetField(ref currentStep, value))
            {
                OnPropertyChanged(nameof(IsArchivePage));
                OnPropertyChanged(nameof(IsPhase3Page));
                OnPropertyChanged(nameof(IsCommitPage));
                OnPropertyChanged(nameof(StepLabel));
                OnPropertyChanged(nameof(IsApplyVisible));
                OnPropertyChanged(nameof(IsNextVisible));
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool IsArchivePage  => currentStep == PrimeWizardStep.Archive;
    public bool IsPhase3Page   => currentStep == PrimeWizardStep.Phase3Review;
    public bool IsCommitPage   => currentStep == PrimeWizardStep.Commit;

    public string StepLabel => currentStep switch
    {
        PrimeWizardStep.Archive       => "第 1 步/共 3 步: 选择 DPS 存档",
        PrimeWizardStep.Phase3Review  => "第 2 步/共 3 步: 检查阶段 3 读取",
        PrimeWizardStep.Commit        => "第 3 步/共 3 步: 确认并应用",
        _ => "",
    };

    public string NextButtonText => "下一步 >";
    public bool IsNextVisible    => currentStep != PrimeWizardStep.Commit;
    public bool IsApplyVisible   => currentStep == PrimeWizardStep.Commit;

    private bool CanGoNext() => currentStep switch
    {
        PrimeWizardStep.Archive      => ArchivePage.IsNextEnabled,
        PrimeWizardStep.Phase3Review => Phase3Page.IsNextEnabled,
        _ => false,
    };

    private void GoNext()
    {
        switch (currentStep)
        {
            case PrimeWizardStep.Archive:
                CurrentStep = PrimeWizardStep.Phase3Review;
                Phase3Page.OnEnter();
                break;
            case PrimeWizardStep.Phase3Review:
                CurrentStep = PrimeWizardStep.Commit;
                CommitPage.OnEnter();
                break;
        }
    }

    private void GoBack()
    {
        switch (currentStep)
        {
            case PrimeWizardStep.Phase3Review: CurrentStep = PrimeWizardStep.Archive; break;
            case PrimeWizardStep.Commit:       CurrentStep = PrimeWizardStep.Phase3Review; Phase3Page.OnEnter(); break;
        }
    }

    private void Cancel() => RequestClose?.Invoke();

    private void Apply()
    {
        if (Context.Dataset is null) return;

        var manifest = Context.EditedManifest ?? Context.Dataset.Phase3;
        int emptyCompared = 0;
        foreach (var row in manifest.Rows)
            if (row.Source == Phase3RowSource.Empty && row.HasCompareDownstream)
                emptyCompared++;

        if (emptyCompared > 0)
        {
            var msg = $"{emptyCompared} 个带有 COMPARE_DATA 断言的阶段 3 读取仍没有值。" +
                      $"DPS 可能会在第一次比较不匹配时中止会话。\n\n" +
                      $"是否仍要提交？";
            var r = MessageBox.Show(msg, "阶段 3 可能会失败",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;
        }

        // Re-edit mode: drop the existing node first so the rebuilt one
        // takes the same CAN ID without a clash.
        if (Context.IsReEdit && Context.ExistingNode is not null)
            bus.RemoveNode(Context.ExistingNode);

        var reportWithOverrides = ApplySecurityOverrides(Context.Dataset.Report, Context);
        var datasetForApply = Context.Dataset with
        {
            Report = reportWithOverrides,
            EditedPhase3 = manifest,
        };
        var (node, dataset) = ArchivePrimer.ApplyTo(bus, datasetForApply);
        CommittedNode = node;
        CommittedDataset = dataset;
        isCompleted = true;
        RequestClose?.Invoke();
    }

    private void OnContextChanged()
    {
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    // Fold the wizard's per-session overrides (set on Page 3) onto the
    // dataset's primer-derived PrimeReport. Returns a fresh report record so
    // the caller can re-emit it via `with` on the dataset.
    //
    // For the fixed-seed field: serialise to a one-key { "fixedSeed": "..." }
    // JsonElement only if the user actually typed something AND selected a
    // bypass module (the only module family that honours fixedSeed). Empty
    // input leaves the module on its default random-seed behaviour.
    private static PrimeReport ApplySecurityOverrides(PrimeReport report, PrimeWizardContext ctx)
    {
        var moduleId = ctx.OverrideSecurityModuleId ?? report.SecurityModuleId;
        JsonElement? config = report.SecurityModuleConfig;

        bool isBypass = SecurityModuleRegistry.Create(moduleId)?.Behaviour
                        == SecurityModuleBehaviour.BypassAll;

        if (isBypass && !string.IsNullOrWhiteSpace(ctx.OverrideFixedSeedHex))
        {
            var dict = new Dictionary<string, string>
            {
                ["fixedSeed"] = ctx.OverrideFixedSeedHex!.Trim(),
            };
            config = JsonSerializer.SerializeToElement(dict);
        }
        else if (!isBypass)
        {
            // Switching to a strict module drops the bypass config entirely
            // - we don't want a stale fixedSeed riding along into a module
            // that wouldn't know what to do with it.
            config = null;
        }

        return report with
        {
            SecurityModuleId = moduleId,
            SecurityModuleConfig = config,
        };
    }
}
