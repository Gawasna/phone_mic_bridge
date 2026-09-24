using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using PhoneMicBridge.Gui.ViewModels;

namespace PhoneMicBridge.Gui
{
    /// <summary>
    /// Value converter to determine visibility of pre-flight remediation guidance.
    /// Returns Visible when check status is Fail or Unknown, and Collapsed when Pass.
    /// </summary>
    public class StatusToRemediationVisibilityConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            string? s = value?.ToString();
            if (string.Equals(s, "Fail", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s, "Unknown", StringComparison.OrdinalIgnoreCase))
            {
                return Visibility.Visible;
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    /// <summary>
    /// Interaction logic for MainWindow.xaml.
    /// Uses native OS chrome with custom window title matching REVIEW-UX-001 specifications.
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        private void EngineSpecsButton_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(
                "Phone Mic Bridge Engine v0.0.1 Technical Specs:\n\n" +
                "• Target Transport: Bluetooth HFP / SCO Channel (16 kHz, 16-bit Mono)\n" +
                "• WASAPI Mode: Shared / Event-driven Render Endpoint\n" +
                "• Buffer Architecture: Zero-Allocation Lock-Free RingBuffer (~20ms latency)\n" +
                "• Resampler: Linear Phase Low-Latency Resampler\n" +
                "• Truthful Status: 3-Tier Independent Endpoint & Transport Verification",
                "Engine Technical Specifications",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);
            if (DataContext is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}
