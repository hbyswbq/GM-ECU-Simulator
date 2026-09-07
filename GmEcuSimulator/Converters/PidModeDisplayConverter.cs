using Common.Protocol;
using System.Globalization;
using System.Windows.Data;

namespace GmEcuSimulator.Converters;

// PidMode -> human-friendly display string ("Mode22" -> "Mode 22",
// "Mode1A" -> "Mode 1A", "Mode2D" -> "Mode 2D", "Mode1" -> "Mode 01").
// Used by the PID Setup window's Mode ComboBox so the dropdown reads as
// labels a user would recognise from the spec (SAE J1979 Service $01,
// GMW3110 / UDS $22, etc.) rather than the C# enum identifier spelling.
[ValueConversion(typeof(PidMode), typeof(string))]
public sealed class PidModeDisplayConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is PidMode mode
            ? mode switch
            {
                PidMode.Mode1A => "模式 1A",
                PidMode.Mode22 => "模式 22",
                PidMode.Mode2D => "模式 2D",
                PidMode.Mode23 => "模式 23",
                _              => mode.ToString(),
            }
            : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
