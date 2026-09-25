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
        private static readonly SolidColorBrush PassBrush = new(Color.FromRgb(0x06, 0x4E, 0x3B));
        private static readonly SolidColorBrush FailBrush = new(Color.FromRgb(0x45, 0x0A, 0x0A));
        private static readonly SolidColorBrush UnknownBrush = new(Color.FromRgb(0x45, 0x1A, 0x03));
        private static readonly SolidColorBrush NeutralBrush = new(Color.FromRgb(0x1E, 0x29, 0x3B));

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
        private static readonly SolidColorBrush PassBrush = new(Color.FromRgb(0x34, 0xD3, 0x99));
        private static readonly SolidColorBrush FailBrush = new(Color.FromRgb(0xF8, 0x71, 0x71));
        private static readonly SolidColorBrush UnknownBrush = new(Color.FromRgb(0xFB, 0xBF, 0x24));
        private static readonly SolidColorBrush NeutralBrush = new(Color.FromRgb(0x94, 0xA3, 0xB8));

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
        private static readonly SolidColorBrush PassBrush = new(Color.FromRgb(0x05, 0x96, 0x69));
        private static readonly SolidColorBrush FailBrush = new(Color.FromRgb(0xDC, 0x26, 0x26));
        private static readonly SolidColorBrush UnknownBrush = new(Color.FromRgb(0xD9, 0x77, 0x06));
        private static readonly SolidColorBrush NeutralBrush = new(Color.FromRgb(0x33, 0x41, 0x55));

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
