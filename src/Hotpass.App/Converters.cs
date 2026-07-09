using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Hotpass.App;

/// <summary>bool → Visibility。parameter="invert" で反転。</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        var b = value is true;
        if (parameter as string == "invert") b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>null → Collapsed。parameter="invert" で null のとき Visible。</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value is not null;
        if (parameter as string == "invert") visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>DeltaState が Target と一致するときだけ Visible(発散バーの左右出し分け用)。</summary>
public sealed class DeltaStateToVisibilityConverter : IValueConverter
{
    public ViewModels.DeltaState Target { get; set; }

    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        => value is ViewModels.DeltaState s && s == Target ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>bool の反転。</summary>
public sealed class InvertBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>0..1 の割合 → parameter(px)を掛けた幅。%バーや発散バー用。</summary>
public sealed class FractionToWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        var fraction = value is double d ? d : 0;
        var track = double.TryParse(parameter as string, NumberStyles.Any, CultureInfo.InvariantCulture, out var t) ? t : 100;
        return Math.Max(0, Math.Min(fraction, 1)) * track;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
