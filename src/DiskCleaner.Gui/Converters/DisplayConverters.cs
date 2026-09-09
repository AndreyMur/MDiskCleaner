using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using DiskCleaner.Core.Localization;
using DiskCleaner.Core.Models;
using DiskCleaner.Core.Reports;

namespace DiskCleaner.Gui.Converters;

public sealed class LongToSizeTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is long bytes ? CleanReportFormatter.FormatBytes(bytes) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class NullableSizeToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is long bytes ? CleanReportFormatter.FormatBytes(bytes) : "—";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class CategoryToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is CleanupCategory category ? LocalizedNames.Category(category) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class RiskToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is CleanupRisk risk ? LocalizedNames.Risk(risk) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class RiskToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not CleanupRisk risk
            ? new SolidColorBrush(Color.FromRgb(0x8F, 0xA2, 0xC2))
            : risk switch
            {
                CleanupRisk.Low => new SolidColorBrush(Color.FromRgb(0x58, 0xD2, 0x84)),
                CleanupRisk.Medium => new SolidColorBrush(Color.FromRgb(0xFF, 0xB2, 0x4D)),
                _ => new SolidColorBrush(Color.FromRgb(0xFF, 0x7A, 0x70))
            };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class BoolToInUseTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "IN_USE" : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class BoolToInUseBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true
            ? new SolidColorBrush(Color.FromRgb(0xFF, 0x7A, 0x70))
            : new SolidColorBrush(Color.FromRgb(0x8F, 0xA2, 0xC2));

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class YesNoTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "да" : "нет";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class ItemToSizeTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is CleanupItem item ? CleanReportFormatter.FormatBytes(item.EffectiveSizeBytes) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class ChangeKindToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not PlanObjectChangeKind kind
            ? new SolidColorBrush(Color.FromRgb(0x8F, 0xA2, 0xC2))
            : kind switch
            {
                PlanObjectChangeKind.Added => new SolidColorBrush(Color.FromRgb(0xFF, 0xB2, 0x4D)),
                PlanObjectChangeKind.Removed => new SolidColorBrush(Color.FromRgb(0x58, 0xD2, 0x84)),
                PlanObjectChangeKind.Changed => new SolidColorBrush(Color.FromRgb(0x7C, 0xC4, 0xFF)),
                _ => new SolidColorBrush(Color.FromRgb(0x8F, 0xA2, 0xC2))
            };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
