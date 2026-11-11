using System;
using System.Threading;
using System.Threading.Tasks;
using Brows.Operations;

namespace Brows.Operations.Tests.PoolProbe;

internal static class Program {
    public static int Main() {
        if (!ThreadPool.SetMinThreads(1, 1) || !ThreadPool.SetMaxThreads(1, 1)) {
            Console.Error.WriteLine("Could not limit the thread pool to one worker.");
            return 2;
        }

        var operatorType = typeof(IOperator).Assembly.GetType("Brows.Operations.Operator");
        var @operator = (IOperator)Activator.CreateInstance(operatorType, new object[] { null });
        var start = Task.Run(() => @operator.Operate("short", (progress, _) => {
            progress.Change(setProgress: 1);
            return Task.CompletedTask;
        }));
        if (start.Wait(TimeSpan.FromSeconds(3))) {
            return 0;
        }
        Console.Error.WriteLine("Operation startup did not complete before the watchdog timeout.");
        return 1;
    }
}
