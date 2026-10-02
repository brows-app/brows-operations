#if NETFRAMEWORK
using System;

namespace System.Threading.Tasks;

internal sealed class TaskCompletionSource : TaskCompletionSource<bool> {
    public TaskCompletionSource(TaskCreationOptions creationOptions)
        : base(creationOptions) {
    }

    public void SetResult() => SetResult(true);
    public bool TrySetResult() => TrySetResult(true);
}

internal static class TaskCompatibilityExtensions {
    public static Task WaitAsync(this Task task, TimeSpan timeout) {
        if (task is null) {
            throw new ArgumentNullException(nameof(task));
        }
        return WaitAsyncCore(task, timeout);
    }

    public static async Task<TResult> WaitAsync<TResult>(this Task<TResult> task, TimeSpan timeout) {
        if (task is null) {
            throw new ArgumentNullException(nameof(task));
        }
        await WaitAsyncCore(task, timeout).ConfigureAwait(false);
        return await task.ConfigureAwait(false);
    }

    private static async Task WaitAsyncCore(Task task, TimeSpan timeout) {
        using (var cancellation = new CancellationTokenSource()) {
            var delay = Task.Delay(timeout, cancellation.Token);
            if (await Task.WhenAny(task, delay).ConfigureAwait(false) != task) {
                throw new TimeoutException();
            }
            cancellation.Cancel();
            await task.ConfigureAwait(false);
        }
    }
}
#endif
