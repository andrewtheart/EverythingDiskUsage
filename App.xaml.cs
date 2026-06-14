using EverythingDiskUsage.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Windows;
using System.Windows.Threading;

namespace EverythingDiskUsage;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
	private ServiceProvider? _serviceProvider;
	private IAppLogger? _logger;

	protected override void OnStartup(StartupEventArgs e)
	{
		_serviceProvider = ConfigureServices();
		_logger = _serviceProvider.GetRequiredService<IAppLogger>();

		_logger.Info("Application startup requested");
		_logger.Debug("Service provider configured; resolving startup services");
		_logger.Info($"CommandLine='{Environment.CommandLine}'");
		_logger.Info($"StartupArgs='{string.Join(" ", e.Args)}'");
		_logger.Info($"LogFile='{_logger.LogFilePath}'");

		DispatcherUnhandledException += OnDispatcherUnhandledException;
		AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
		TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
		_logger.Debug("Unhandled exception handlers registered");

		base.OnStartup(e);

		_logger.Debug("Resolving MainWindow from service provider");
		MainWindow = _serviceProvider.GetRequiredService<MainWindow>();
		_logger.Debug("Showing MainWindow");
		MainWindow.Show();
		_logger.Info("Application startup completed");
	}

	protected override void OnExit(ExitEventArgs e)
	{
		_logger?.Info($"Application exit requested; exitCode={e.ApplicationExitCode}");
		base.OnExit(e);
		_logger?.Info("Application exit completed");
		_logger?.Debug("Disposing service provider");
		_serviceProvider?.Dispose();
	}

	private static ServiceProvider ConfigureServices()
	{
		var services = new ServiceCollection();
		services.AddSingleton<IAppLogger, AppLoggerAdapter>();
		services.AddSingleton<IAppSettingsService, AppSettingsServiceAdapter>();
		services.AddSingleton<IDiskUsageAnalyzer, DiskUsageAnalyzer>();
		services.AddSingleton<IShellContextMenuService, ShellContextMenuService>();
		services.AddTransient<MainWindow>();
		return services.BuildServiceProvider(validateScopes: true);
	}

	private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
	{
		_logger?.Critical("Unhandled dispatcher exception", e.Exception);
	}

	private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
	{
		if (e.ExceptionObject is Exception exception)
		{
			_logger?.Critical($"Unhandled AppDomain exception; isTerminating={e.IsTerminating}", exception);
		}
		else
		{
			_logger?.Critical($"Unhandled AppDomain exception object; isTerminating={e.IsTerminating}; object='{e.ExceptionObject}'");
		}
	}

	private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
	{
		_logger?.Error("Unobserved task exception", e.Exception);
	}
}

