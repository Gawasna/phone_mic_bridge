using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using PhoneMicBridge.Application;
using PhoneMicBridge.Core;

namespace PhoneMicBridge.Gui.Converters
{
    internal static class StatusClassifier
    {
        public static bool IsPass(object? value)
        {
            if (value is DiagnosticOverallStatus dos) return dos == DiagnosticOverallStatus.Pass;
            if (value is BridgeState bs) return bs == BridgeState.Streaming;
            string? s = value?.ToString();
            return string.Equals(s, "Pass", StringComparison.OrdinalIgnoreCase) || string.Equals(s, "Streaming", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsFail(object? value)
        {
            if (value is DiagnosticOverallStatus dos) return dos == DiagnosticOverallStatus.Fail;
            if (value is BridgeState bs) return bs == BridgeState.Error;
            string? s = value?.ToString();
            return string.Equals(s, "Fail", StringComparison.OrdinalIgnoreCase) || string.Equals(s, "Error", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsUnknown(object? value)
        {
            if (value is DiagnosticOverallStatus dos) return dos == DiagnosticOverallStatus.Unknown || dos == DiagnosticOverallStatus.InProgress;
            if (value is BridgeState bs) return bs == BridgeState.Connecting;
            string? s = value?.ToString();
            return string.Equals(s, "Unknown", StringComparison.OrdinalIgnoreCase) || string.Equals(s, "InProgress", StringComparison.OrdinalIgnoreCase) || string.Equals(s, "Connecting", StringComparison.OrdinalIgnoreCase);
        }
    }

    public class StatusToBackgroundConverter : IValueConverter
    {
        private static readonly SolidColorBrush PassBrush = new(Color.FromRgb(0xDC, 0xFC, 0xE7)); // Green-100
        private static readonly SolidColorBrush FailBrush = new(Color.FromRgb(0xFE, 0xE2, 0xE2)); // Red-100
        private static readonly SolidColorBrush UnknownBrush = new(Color.FromRgb(0xFE, 0xF3, 0xC7)); // Amber-100
        private static readonly SolidColorBrush NeutralBrush = new(Color.FromRgb(0xF3, 0xF4, 0xF6)); // Gray-100

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (StatusClassifier.IsPass(value)) return PassBrush;
            if (StatusClassifier.IsFail(value)) return FailBrush;
            if (StatusClassifier.IsUnknown(value)) return UnknownBrush;
            return NeutralBrush;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    public class StatusToForegroundConverter : IValueConverter
    {
        private static readonly SolidColorBrush PassBrush = new(Color.FromRgb(0x15, 0x80, 0x3D)); // Green-700
        private static readonly SolidColorBrush FailBrush = new(Color.FromRgb(0xB9, 0x1C, 0x1C)); // Red-700
        private static readonly SolidColorBrush UnknownBrush = new(Color.FromRgb(0xB4, 0x53, 0x09)); // Amber-700
        private static readonly SolidColorBrush NeutralBrush = new(Color.FromRgb(0x37, 0x41, 0x51)); // Gray-700

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (StatusClassifier.IsPass(value)) return PassBrush;
            if (StatusClassifier.IsFail(value)) return FailBrush;
            if (StatusClassifier.IsUnknown(value)) return UnknownBrush;
            return NeutralBrush;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    public class StatusToBorderConverter : IValueConverter
    {
        private static readonly SolidColorBrush PassBrush = new(Color.FromRgb(0x86, 0xEF, 0xAC)); // Green-300
        private static readonly SolidColorBrush FailBrush = new(Color.FromRgb(0xFC, 0xA5, 0xA5)); // Red-300
        private static readonly SolidColorBrush UnknownBrush = new(Color.FromRgb(0xFD, 0xE6, 0x8A)); // Amber-300
        private static readonly SolidColorBrush NeutralBrush = new(Color.FromRgb(0xD1, 0xD5, 0xDB)); // Gray-300

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (StatusClassifier.IsPass(value)) return PassBrush;
            if (StatusClassifier.IsFail(value)) return FailBrush;
            if (StatusClassifier.IsUnknown(value)) return UnknownBrush;
            return NeutralBrush;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    public class BooleanToVisibilityConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            bool flag = value is true;
            if (parameter is string p && p.Equals("Inverse", StringComparison.OrdinalIgnoreCase))
                flag = !flag;
            return flag ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
