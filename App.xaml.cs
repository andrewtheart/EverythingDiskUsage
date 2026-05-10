using EverythingDiskUsage.Services;
using System.Windows;
using System.Windows.Threading;

namespace EverythingDiskUsage;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
	protected override void OnStartup(StartupEventArgs e)
	{
		AppLogger.Info("Application startup requested");
		AppLogger.Info($"CommandLine='{Environment.CommandLine}'");
		AppLogger.Info($"LogFile='{AppLogger.LogFilePath}'");

		DispatcherUnhandledException += OnDispatcherUnhandledException;
		AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
		TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

		base.OnStartup(e);
		AppLogger.Info("Application startup completed");
	}

	protected override void OnExit(ExitEventArgs e)
	{
		AppLogger.Info($"Application exit requested; exitCode={e.ApplicationExitCode}");
		base.OnExit(e);
		AppLogger.Info("Application exit completed");
	}

	private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
	{
		AppLogger.Critical("Unhandled dispatcher exception", e.Exception);
	}

	private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
	{
		if (e.ExceptionObject is Exception exception)
		{
			AppLogger.Critical($"Unhandled AppDomain exception; isTerminating={e.IsTerminating}", exception);
		}
		else
		{
			AppLogger.Critical($"Unhandled AppDomain exception object; isTerminating={e.IsTerminating}; object='{e.ExceptionObject}'");
		}
	}

	private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
	{
		AppLogger.Error("Unobserved task exception", e.Exception);
	}
}

