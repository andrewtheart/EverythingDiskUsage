using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace EverythingDiskUsage.Tests.Ui;

internal static class WpfTestHost
{
    public static void Run(Func<Task> action)
    {
        Exception? exception = null;
        using var completed = new ManualResetEventSlim();

        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            if (Application.ResourceAssembly is null)
            {
                Application.ResourceAssembly = typeof(MainWindow).Assembly;
            }

            try
            {
                var task = action();
                var frame = new DispatcherFrame();
                task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
                Dispatcher.PushFrame(frame);
                task.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
            finally
            {
                dispatcher.InvokeShutdown();
                completed.Set();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        completed.Wait();

        if (exception is not null)
        {
            throw exception;
        }
    }

    public static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan? timeout = null)
    {
        var stopwatch = Stopwatch.StartNew();
        var limit = timeout ?? TimeSpan.FromSeconds(5);
        while (!predicate())
        {
            if (stopwatch.Elapsed > limit)
            {
                throw new TimeoutException("The WPF test condition was not met before the timeout expired.");
            }

            await Task.Delay(20);
        }
    }
}