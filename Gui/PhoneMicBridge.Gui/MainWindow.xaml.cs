using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
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
    /// Manages responsive adaptive layout between Compact Utility Window (<680px)
    /// and Wide Engineering Dashboard (>=680px) without animation delays.
    /// </summary>
    public partial class MainWindow : Window
    {
        private bool _isSingleColumn;

        public MainWindow()
        {
            InitializeComponent();
        }

        private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (AdaptiveGrid == null || LeftPanel == null || RightPanel == null)
            {
                return;
            }

            if (e.NewSize.Width < 680 && !_isSingleColumn)
            {
                _isSingleColumn = true;

                AdaptiveGrid.ColumnDefinitions.Clear();
                AdaptiveGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                AdaptiveGrid.RowDefinitions.Clear();
                AdaptiveGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                AdaptiveGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                Grid.SetColumn(LeftPanel, 0);
                Grid.SetRow(LeftPanel, 0);

                Grid.SetColumn(RightPanel, 0);
                Grid.SetRow(RightPanel, 1);

                if (DataContext is MainViewModel vm)
                {
                    vm.IsCompactMode = true;
                }
            }
            else if (e.NewSize.Width >= 680 && _isSingleColumn)
            {
                _isSingleColumn = false;

                AdaptiveGrid.ColumnDefinitions.Clear();
                AdaptiveGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                AdaptiveGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                AdaptiveGrid.RowDefinitions.Clear();
                AdaptiveGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                Grid.SetColumn(LeftPanel, 0);
                Grid.SetRow(LeftPanel, 0);

                Grid.SetColumn(RightPanel, 1);
                Grid.SetRow(RightPanel, 0);

                if (DataContext is MainViewModel vm)
                {
                    vm.IsCompactMode = false;
                }
            }
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
