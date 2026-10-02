using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Brows;

internal static class WpfTestThread {
    public static Task Run(Func<Task> action) {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            Exception failure = null;
            dispatcher.UnhandledException += (_, e) => { failure ??= e.Exception; e.Handled = true; };
            // Prevent a broken binding/test continuation from hanging the entire test process.
            var timeout = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Send,
                (_, _) => {
                    failure ??= new TimeoutException("The WPF test did not finish within 15 seconds.");
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
                }, dispatcher);
            timeout.Start();
            dispatcher.BeginInvoke(async () => {
                try { await action(); }
                catch (Exception ex) { failure ??= ex; }
                finally {
                    timeout.Stop();
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.ApplicationIdle);
                }
            });
            Dispatcher.Run();
            SynchronizationContext.SetSynchronizationContext(null);
            if (failure is null) result.TrySetResult();
            else result.TrySetException(failure);
        }) { IsBackground = true, Name = "Brows.Operations WPF test dispatcher" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return result.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
}
