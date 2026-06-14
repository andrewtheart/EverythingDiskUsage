using EverythingDiskUsage.Models;
using EverythingDiskUsage.Services;
using System.Windows;
using System.Windows.Controls.Primitives;

namespace EverythingDiskUsage.Tests.Ui;

public sealed class MainWindowUiTests
{
    [Fact]
    public void Constructor_LoadsSettingsAndInitializesControlState()
    {
        WpfTestHost.Run(async () =>
        {
            var window = CreateWindow(new ImmediateAnalyzer(TestData.ScanResultFromFiles(NewRootPath())));
            try
            {
                Assert.Equal(AppLogLevel.Debug, window.LogLevelComboBox.SelectedValue);
                Assert.True(window.LogEachSdkFileCheckBox.IsChecked);
                Assert.True(window.LogToDebugOutputCheckBox.IsChecked);
                Assert.Equal("12", window.RetainedLogFilesTextBox.Text);
                Assert.Equal("Ready", window.StatusTextBlock.Text);
                Assert.True(window.ScanButton.IsEnabled);
                Assert.False(window.CancelButton.IsEnabled);
                Assert.EndsWith("settings.json", window.SettingsFilePathTextBlock.Text, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                window.Close();
            }

            await Task.CompletedTask;
        });
    }

    [Fact]
    public void SaveSettingsButton_PersistsUiValuesAndAppliesLoggerSettings()
    {
        WpfTestHost.Run(async () =>
        {
            var logger = new TestLogger();
            var settings = new TestSettingsService(new AppSettings { MinimumLogLevel = AppLogLevel.Info, RetainedLogFiles = 5 });
            var window = CreateWindow(new ImmediateAnalyzer(TestData.ScanResultFromFiles(NewRootPath())), logger, settings);

            try
            {
                window.LogLevelComboBox.SelectedValue = AppLogLevel.Error;
                window.LogEachSdkFileCheckBox.IsChecked = true;
                window.LogToDebugOutputCheckBox.IsChecked = true;
                window.RetainedLogFilesTextBox.Text = "42";

                Click(window.SaveSettingsButton);

                Assert.Equal(1, settings.SaveCount);
                Assert.Equal(AppLogLevel.Error, settings.SavedSettings.MinimumLogLevel);
                Assert.True(settings.SavedSettings.LogEachSdkFile);
                Assert.True(settings.SavedSettings.LogToDebugOutput);
                Assert.Equal(42, settings.SavedSettings.RetainedLogFiles);
                Assert.Equal(AppLogLevel.Error, logger.CurrentSettings.MinimumLogLevel);
                Assert.StartsWith("Saved ", window.SettingsStatusTextBlock.Text, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                window.Close();
            }

            await Task.CompletedTask;
        });
    }

    [Fact]
    public void ScanButton_DisablesControlsThenPopulatesTreeDetailsAndDuplicates()
    {
        WpfTestHost.Run(async () =>
        {
            using var temp = TempDirectory.Create();
            var analyzer = new ControllableAnalyzer();
            var window = CreateWindow(analyzer);
            var result = TestData.ScanResultFromFiles(
                temp.Path,
                TestData.File(temp.Path, "alpha\\duplicate.bin", 100),
                TestData.File(temp.Path, "beta\\duplicate.bin", 100),
                TestData.File(temp.Path, "beta\\unique.bin", 50));

            try
            {
                window.RootPathTextBox.Text = temp.Path;
                Click(window.ScanButton);

                await WpfTestHost.WaitUntilAsync(() => analyzer.Started.Task.IsCompleted);
                Assert.False(window.ScanButton.IsEnabled);
                Assert.False(window.BrowseButton.IsEnabled);
                Assert.True(window.CancelButton.IsEnabled);
                Assert.True(window.ScanProgressBar.IsIndeterminate || window.ScanProgressBar.Value == 0);

                analyzer.Complete(result);

                await WpfTestHost.WaitUntilAsync(() => window.StatusTextBlock.Text.StartsWith("Scan complete:", StringComparison.OrdinalIgnoreCase));
                Assert.True(window.ScanButton.IsEnabled);
                Assert.False(window.CancelButton.IsEnabled);
                Assert.Equal(result.Root.SizeText, window.SummaryTextBlock.Text);
                Assert.Equal("3 SDK results", window.SdkStatusTextBlock.Text);
                Assert.Single(window.UsageTree.Items);
                Assert.Equal(3, window.DirectoryDetailsGrid.Items.Count);
                Assert.Equal("1 group \u00b7 100 B wasted", window.DuplicatesSummaryTextBlock.Text);
                Assert.Equal(3, window.DuplicatesGrid.Items.Count);
                Assert.Equal("No size data", window.EmptyPieTextBlock.Text);
                Assert.NotNull(window.LegendItems.ItemsSource);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void CancelButton_CancelsActiveScanAndRestoresControls()
    {
        WpfTestHost.Run(async () =>
        {
            using var temp = TempDirectory.Create();
            var analyzer = new ControllableAnalyzer();
            var window = CreateWindow(analyzer);

            try
            {
                window.RootPathTextBox.Text = temp.Path;
                Click(window.ScanButton);

                await WpfTestHost.WaitUntilAsync(() => analyzer.Started.Task.IsCompleted);
                Click(window.CancelButton);
                Assert.True(analyzer.CapturedToken.IsCancellationRequested);

                analyzer.Cancel();

                await WpfTestHost.WaitUntilAsync(() => window.StatusTextBlock.Text.Equals("Scan cancelled", StringComparison.OrdinalIgnoreCase));
                Assert.True(window.ScanButton.IsEnabled);
                Assert.False(window.CancelButton.IsEnabled);
                Assert.Equal(string.Empty, window.SummaryTextBlock.Text);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static MainWindow CreateWindow(IDiskUsageAnalyzer analyzer)
    {
        return CreateWindow(analyzer, new TestLogger(), new TestSettingsService(new AppSettings
        {
            MinimumLogLevel = AppLogLevel.Debug,
            LogEachSdkFile = true,
            LogToDebugOutput = true,
            RetainedLogFiles = 12
        }));
    }

    private static MainWindow CreateWindow(IDiskUsageAnalyzer analyzer, TestLogger logger, TestSettingsService settings)
    {
        return new MainWindow(analyzer, logger, settings, new TestShellContextMenuService(), configureNotifications: false);
    }

    private static void Click(ButtonBase button)
    {
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }

    private static string NewRootPath() => Path.Combine(Path.GetTempPath(), "EverythingDiskUsageTests", Guid.NewGuid().ToString("N"));

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path)
        {
            Path = path;
            Directory.CreateDirectory(path);
        }

        public string Path { get; }

        public static TempDirectory Create()
        {
            return new TempDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "EverythingDiskUsageTests", Guid.NewGuid().ToString("N")));
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}