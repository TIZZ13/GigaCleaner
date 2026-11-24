using System.Globalization;
using Wpf.Ui.Controls;

namespace GigaCleaner.Views;

/// <summary>Two-digit output for NumberBox: time shows 09:05 instead of 9:5.</summary>
public sealed class TwoDigitFormatter : INumberFormatter, INumberParser
{
    public string FormatDouble(double? value) => value is { } v ? v.ToString("00") : string.Empty;
    public string FormatInt(int? value) => value is { } v ? v.ToString("00") : string.Empty;
    public string FormatUInt(uint? value) => value is { } v ? v.ToString("00") : string.Empty;

    public double? ParseDouble(string? input) =>
        double.TryParse(input, NumberStyles.Float, CultureInfo.CurrentCulture, out var v) ? v : null;

    public int? ParseInt(string? input) =>
        int.TryParse(input, NumberStyles.Integer, CultureInfo.CurrentCulture, out var v) ? v : null;

    public uint? ParseUInt(string? input) =>
        uint.TryParse(input, NumberStyles.Integer, CultureInfo.CurrentCulture, out var v) ? v : null;
}
