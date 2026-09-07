using Common.Waveforms;
using Core.Ecu;
using Microsoft.Win32;
using System.IO;
using System.Windows;

namespace GmEcuSimulator.ViewModels;

// Two-way bound editor for a Pid's waveform. Mutations rebuild the
// underlying IWaveformGenerator immediately so the next scheduler tick
// produces the new shape.
public sealed class WaveformViewModel : NotifyPropertyChangedBase
{
    private readonly Pid pid;

    public WaveformViewModel(Pid pid)
    {
        this.pid = pid;
        PickCsvFileCommand = new RelayCommand(PickCsvFile);
        ClearCsvFileCommand = new RelayCommand(ClearCsvFile, () => !string.IsNullOrEmpty(CsvFilePath));
    }

    public RelayCommand PickCsvFileCommand { get; }
    public RelayCommand ClearCsvFileCommand { get; }

    private void Rebuild()
    {
        // Snapshot current settings, push back as a new WaveformConfig so the
        // factory rebuilds the IWaveformGenerator.
        pid.WaveformConfig = new WaveformConfig
        {
            Shape = Shape,
            Amplitude = Amplitude,
            Offset = Offset,
            FrequencyHz = FrequencyHz,
            PhaseDeg = PhaseDeg,
            DutyCycle = DutyCycle,
            CsvFilePath = CsvFilePath,
            CsvLoopMode = CsvLoopMode,
        };
    }

    public WaveformShape Shape
    {
        get => pid.WaveformConfig.Shape;
        set
        {
            if (pid.WaveformConfig.Shape == value) return;
            pid.WaveformConfig.Shape = value;
            Rebuild();
            OnPropertyChanged();
            OnPropertyChanged(nameof(SupportsAmplitude));
            OnPropertyChanged(nameof(SupportsFrequency));
            OnPropertyChanged(nameof(SupportsDuty));
            OnPropertyChanged(nameof(SupportsCsv));
        }
    }

    public double Amplitude
    {
        get => pid.WaveformConfig.Amplitude;
        set { if (pid.WaveformConfig.Amplitude != value) { pid.WaveformConfig.Amplitude = value; Rebuild(); OnPropertyChanged(); } }
    }

    public double Offset
    {
        get => pid.WaveformConfig.Offset;
        set { if (pid.WaveformConfig.Offset != value) { pid.WaveformConfig.Offset = value; Rebuild(); OnPropertyChanged(); } }
    }

    public double FrequencyHz
    {
        get => pid.WaveformConfig.FrequencyHz;
        set { if (pid.WaveformConfig.FrequencyHz != value) { pid.WaveformConfig.FrequencyHz = value; Rebuild(); OnPropertyChanged(); } }
    }

    public double PhaseDeg
    {
        get => pid.WaveformConfig.PhaseDeg;
        set { if (pid.WaveformConfig.PhaseDeg != value) { pid.WaveformConfig.PhaseDeg = value; Rebuild(); OnPropertyChanged(); } }
    }

    public double DutyCycle
    {
        get => pid.WaveformConfig.DutyCycle;
        set { if (pid.WaveformConfig.DutyCycle != value) { pid.WaveformConfig.DutyCycle = value; Rebuild(); OnPropertyChanged(); } }
    }

    public string? CsvFilePath
    {
        get => pid.WaveformConfig.CsvFilePath;
        set
        {
            if (pid.WaveformConfig.CsvFilePath == value) return;
            pid.WaveformConfig.CsvFilePath = value;
            Rebuild();
            OnPropertyChanged();
            OnPropertyChanged(nameof(CsvFileDisplay));
        }
    }

    public CsvLoopMode CsvLoopMode
    {
        get => pid.WaveformConfig.CsvLoopMode;
        set { if (pid.WaveformConfig.CsvLoopMode != value) { pid.WaveformConfig.CsvLoopMode = value; Rebuild(); OnPropertyChanged(); } }
    }

    // Short, label-friendly view of the configured CSV. Bound in the XAML
    // beside the picker button so the user can see which file is loaded
    // without giving up the full path on hover.
    public string CsvFileDisplay
        => string.IsNullOrEmpty(CsvFilePath) ? "(no file picked)" : Path.GetFileName(CsvFilePath);

    // Used by the XAML to disable irrelevant fields per shape. FileStream and Constant don't have an inherent
    // amplitude or frequency - FileStream takes its samples from the loaded bin, Constant is a fixed offset.
    // CsvFile's amplitude/frequency are likewise meaningless - the waveform shape comes from the file rows.
    public bool SupportsAmplitude => Shape != WaveformShape.Constant
                                  && Shape != WaveformShape.FileStream
                                  && Shape != WaveformShape.CsvFile;
    public bool SupportsFrequency => Shape != WaveformShape.Constant
                                  && Shape != WaveformShape.FileStream
                                  && Shape != WaveformShape.CsvFile;
    public bool SupportsDuty => Shape == WaveformShape.Square;
    public bool SupportsCsv => Shape == WaveformShape.CsvFile;

    private void PickCsvFile()
    {
        var settings = AppSettings.Load();
        var picker = new OpenFileDialog
        {
            Filter = "CSV (*.csv)|*.csv|所有文件|*.*",
            Title = "选择 CSV (A列 = 时间(秒), B列 = 值)",
            InitialDirectory = AppSettings.ResolveInitialDir(settings.LastCsvWaveformDir),
        };
        if (picker.ShowDialog() != true) return;

        try
        {
            var result = CsvReplayLoader.Load(picker.FileName);
            var summary = result.SkippedHeader
                ? $"Loaded {result.Samples.Count} rows (header row skipped)."
                : $"Loaded {result.Samples.Count} rows.";
            MessageBox.Show(summary, "CSV 波形",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"所选文件不是兼容的波形 CSV：\n\n{ex.Message}",
                "CSV 波形",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var chosenDir = Path.GetDirectoryName(picker.FileName);
        if (!string.IsNullOrEmpty(chosenDir))
        {
            settings.LastCsvWaveformDir = chosenDir;
            settings.Save();
        }

        CsvFilePath = picker.FileName;
    }

    private void ClearCsvFile() => CsvFilePath = null;
}
