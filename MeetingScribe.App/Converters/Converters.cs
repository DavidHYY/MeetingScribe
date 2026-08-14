using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace MeetingScribe.App.Converters;

/// <summary>bool -> !bool. Used to disable device pickers/title box while recording.</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b && !b;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// bool -> bool passthrough for <c>IsVisible</c> bindings (true = visible). Avalonia has
/// no <c>Visibility</c> enum like WPF - visibility is the plain bool <c>IsVisible</c>
/// property - so this converter is an identity function; it exists to keep XAML
/// self-documenting at the call site ("visible when true") and symmetric with
/// <see cref="InverseBoolToVisibleConverter"/>.
/// </summary>
public sealed class BoolToVisibleConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b && b;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>bool -> !bool for <c>IsVisible</c> bindings (true = hidden). Used to show the Start button only while idle.</summary>
public sealed class InverseBoolToVisibleConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b && !b;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>null-or-empty string -> false, otherwise true, for an <c>IsVisible</c> binding. Hides the error text block when there is no error.</summary>
public sealed class NullToVisibleConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s && s.Length > 0;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
