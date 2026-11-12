using System;
using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Brows.Operations;

[TestFixture]
internal sealed class OperationBaseTest {
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static Operation Start(IOperator @operator, OperationDelegate task, Action<Operation> beforeStart = null) {
        Operation operation = null;
        new OperationManager((OperationCollection)@operator.Operations)
            .Operate("root", task, started => {
                operation = started;
                beforeStart?.Invoke(started);
            });
        return operation;
    }

    private static async Task<double> WaitForPositiveRate(OperationBase operation) {
        var sampled = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        PropertyChangedEventHandler handler = (_, args) => {
            if (args.PropertyName == nameof(OperationBase.ProgressRate) && operation.ProgressRate > 0) {
                sampled.TrySetResult(operation.ProgressRate);
            }
        };
        operation.PropertyChanged += handler;
        try {
            if (operation.ProgressRate > 0) {
                sampled.TrySetResult(operation.ProgressRate);
            }
            return await sampled.Task.WaitAsync(TimeSpan.FromSeconds(4));
        }
        finally {
            operation.PropertyChanged -= handler;
        }
    }

    private static async Task<OperationBase> WaitForChild(OperationBase operation) {
        var children = (IList)operation.ChildSource;
        if (children.Count == 0) {
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            NotifyCollectionChangedEventHandler handler = (_, _) => {
                if (children.Count > 0) {
                    changed.TrySetResult();
                }
            };
            var source = (INotifyCollectionChanged)children;
            source.CollectionChanged += handler;
            try {
                if (children.Count == 0) {
                    await changed.Task.WaitAsync(Timeout);
                }
            }
            finally {
                source.CollectionChanged -= handler;
            }
        }
        return children.Cast<OperationBase>().Single();
    }

    [Test]
    public async Task RefreshRates_UpdatesRunningRateAndAverageForNetProgressAndIdleTime() {
        IOperator @operator = new Operator();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = Start(@operator, async (progress, _) => {
            progress.Change(setProgress: 10);
            await release.Task;
        }, operation => operation.DisableRateSampling());

        try {
            var item = (OperationBase)@operator.Operations.Snapshot().Single();
            Assert.That(item, Is.SameAs(operation));
            operation.RefreshRates(_ => TimeSpan.FromSeconds(1));
            AssertRates(item, 10, 10);
            operation.RefreshRates(_ => TimeSpan.FromSeconds(3));
            AssertRates(item, 0, 10d / 3);
        }
        finally {
            release.TrySetResult();
        }

        await operation.Completion.WaitAsync(Timeout);
        Assert.That(operation.ProgressRate, Is.Zero);
    }

    [Test]
    public async Task RefreshRates_UsesParentAggregateAndEachOperationsOwnElapsedTime() {
        IOperator @operator = new Operator();
        var releaseChild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = Start(@operator, async (progress, _) => {
            progress.Change(setProgress: 10);
            await progress.Child("child", async (childProgress, _) => {
                childProgress.Change(setProgress: 5);
                await releaseChild.Task;
            });
        }, operation => operation.DisableRateSampling());
        var child = await WaitForChild(operation);

        try {
            operation.RefreshRates(current => current.Name == "root"
                ? TimeSpan.FromSeconds(2)
                : TimeSpan.FromSeconds(1));

            AssertRates(operation, 7.5, 7.5);
            AssertRates(child, 5, 5);

            operation.RefreshRates(current => current.Name == "root"
                ? TimeSpan.FromSeconds(3)
                : TimeSpan.FromSeconds(2));
            AssertRates(child, 0, 2.5);
            AssertRates(operation, 0, 5);
        }
        finally {
            releaseChild.TrySetResult();
        }

        await operation.Completion.WaitAsync(Timeout);
    }

    [Test]
    public async Task Complete_FinalizesRatesBeforeCompletionNotificationAndFreezesThem() {
        IOperator @operator = new Operator();
        var completedRates = new TaskCompletionSource<(double Rate, double Average)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = Start(@operator, (progress, _) => {
            progress.Change(setProgress: 9);
            return Task.CompletedTask;
        }, operation => operation.Completed += (_, _) => completedRates.TrySetResult(
            (operation.ProgressRate, operation.ProgressRateAverage)));

        await operation.Completion.WaitAsync(Timeout);
        var rates = await completedRates.Task.WaitAsync(Timeout);

        using (Assert.EnterMultipleScope()) {
            Assert.That(rates.Rate, Is.Zero);
            Assert.That(double.IsNaN(rates.Average) || double.IsInfinity(rates.Average), Is.False);
            Assert.That(operation.Complete, Is.True);
        }
        var finalAverage = operation.ProgressRateAverage;
        operation.RefreshRates(_ => TimeSpan.FromDays(1));
        Assert.That(operation.ProgressRateAverage, Is.EqualTo(finalAverage));
    }

    [Test]
    public async Task RateNotification_ObservesCommittedValuesOutsideStateLock() {
        IOperator @operator = new Operator();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notification = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = Start(@operator, async (progress, _) => {
            progress.Change(setProgress: 4);
            await release.Task;
        }, operation => operation.DisableRateSampling());
        operation.PropertyChanged += (_, args) => {
            if (args.PropertyName == nameof(OperationBase.ProgressRate)) {
                var rates = Task.Run(() => (operation.ProgressRate, operation.ProgressRateAverage))
                    .WaitAsync(Timeout).GetAwaiter().GetResult();
                if (rates == (4d, 4d)) {
                    notification.TrySetResult();
                }
            }
        };

        try {
            operation.RefreshRates(_ => TimeSpan.FromSeconds(1));
            await notification.Task.WaitAsync(Timeout);
        }
        finally {
            release.TrySetResult();
        }

        await operation.Completion.WaitAsync(Timeout);
    }

    [Test]
    public async Task Change_FormatsProgressRatesWithSeparateFormatsAndProvider() {
        IOperator @operator = new Operator();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IOperationProgress reporter = null;
        var progressRateStringChanged = false;
        var progressRateAverageStringChanged = false;
        var operation = Start(@operator, async (progress, _) => {
            reporter = progress;
            progress.Change(setProgress: 25);
            reported.TrySetResult();
            await release.Task;
        }, operation => operation.DisableRateSampling());
        operation.PropertyChanged += (_, args) => {
            progressRateStringChanged |= args.PropertyName == nameof(OperationBase.ProgressRateString);
            progressRateAverageStringChanged |=
                args.PropertyName == nameof(OperationBase.ProgressRateAverageString);
        };

        try {
            await reported.Task.WaitAsync(Timeout);
            reporter.Change(progressRateFormat: "0.00",
                            progressRateAverageFormat: "0.000",
                            formatProvider: CultureInfo.GetCultureInfo("en-US"));

            using (Assert.EnterMultipleScope()) {
                Assert.That(((OperationBase)operation).ProgressRateString, Is.EqualTo("0.00"));
                Assert.That(((OperationBase)operation).ProgressRateAverageString, Is.EqualTo("0.000"));
                Assert.That(progressRateStringChanged, Is.True);
                Assert.That(progressRateAverageStringChanged, Is.True);
            }

            progressRateStringChanged = false;
            progressRateAverageStringChanged = false;
            operation.RefreshRates(_ => TimeSpan.FromSeconds(2));

            using (Assert.EnterMultipleScope()) {
                Assert.That(((OperationBase)operation).ProgressRateString, Is.EqualTo("12.50"));
                Assert.That(((OperationBase)operation).ProgressRateAverageString, Is.EqualTo("12.500"));
                Assert.That(progressRateStringChanged, Is.True);
                Assert.That(progressRateAverageStringChanged, Is.True);
            }

            progressRateStringChanged = false;
            progressRateAverageStringChanged = false;
            reporter.Change(formatProvider: CultureInfo.GetCultureInfo("fr-FR"));

            using (Assert.EnterMultipleScope()) {
                Assert.That(((OperationBase)operation).ProgressRateString, Is.EqualTo("12,50"));
                Assert.That(((OperationBase)operation).ProgressRateAverageString, Is.EqualTo("12,500"));
                Assert.That(progressRateStringChanged, Is.True);
                Assert.That(progressRateAverageStringChanged, Is.True);
            }
        }
        finally {
            release.TrySetResult();
        }

        await operation.Completion.WaitAsync(Timeout);
    }

    [Test]
    public async Task Change_FormatsFileSizeRatesWithAutomaticUnits() {
        IOperator @operator = new Operator();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IOperationProgress reporter = null;
        var progressRateStringChanged = false;
        var progressRateAverageStringChanged = false;
        var operation = Start(@operator, async (progress, _) => {
            reporter = progress;
            progress.Change(setProgress: 1_000_000, setTarget: 2_000_000);
            reported.TrySetResult();
            await release.Task;
        }, operation => operation.DisableRateSampling());
        operation.PropertyChanged += (_, args) => {
            progressRateStringChanged |= args.PropertyName == nameof(OperationBase.ProgressRateString);
            progressRateAverageStringChanged |=
                args.PropertyName == nameof(OperationBase.ProgressRateAverageString);
        };

        try {
            await reported.Task.WaitAsync(Timeout);
            reporter.Change(progressFormat: "N2",
                            progressRateFormat: "N2",
                            progressRateAverageFormat: "N2",
                            formatProvider: CultureInfo.InvariantCulture);
            progressRateStringChanged = false;
            progressRateAverageStringChanged = false;

            reporter.Change(kind: OperationProgressKind.FileSize);

            using (Assert.EnterMultipleScope()) {
                Assert.That(((OperationBase)operation).ProgressString, Is.EqualTo("976.56 KiB"));
                Assert.That(((OperationBase)operation).ProgressRateString, Is.EqualTo("0 B"));
                Assert.That(((OperationBase)operation).ProgressRateAverageString, Is.EqualTo("0 B"));
                Assert.That(progressRateStringChanged, Is.True);
                Assert.That(progressRateAverageStringChanged, Is.True);
            }

            operation.RefreshRates(_ => TimeSpan.FromSeconds(1));

            using (Assert.EnterMultipleScope()) {
                Assert.That(((OperationBase)operation).ProgressRateString, Is.EqualTo("976.56 KiB"));
                Assert.That(((OperationBase)operation).ProgressRateAverageString, Is.EqualTo("976.56 KiB"));
                Assert.That(((OperationBase)operation).ProgressRateString,
                            Is.EqualTo(((OperationBase)operation).ProgressString));
                Assert.That(((OperationBase)operation).ProgressRateAverageString,
                            Is.EqualTo(((OperationBase)operation).ProgressString));
            }
        }
        finally {
            release.TrySetResult();
        }

        await operation.Completion.WaitAsync(Timeout);
    }

    [Test]
    public async Task SamplerListenerFailure_FaultsCompletionAfterFinalizingOperation() {
        Operation operation = null;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var error = new IOException("rate listener");
        Exception observed = null;
        try {
            await UiTestThread.Run(async () => {
                IOperator @operator = new Operator();
                var notification = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var throwOnce = 0;
                operation = Start(@operator, async (progress, _) => {
                    progress.Change(setProgress: 1);
                    await release.Task;
                }, started => started.PropertyChanged += (_, args) => {
                    if (args.PropertyName == nameof(OperationBase.ProgressRate)) {
                        notification.TrySetResult();
                        if (Interlocked.Exchange(ref throwOnce, 1) == 0) {
                            throw error;
                        }
                    }
                });

                await notification.Task.WaitAsync(TimeSpan.FromSeconds(4));
                release.TrySetResult();
                try {
                    await operation.Completion.WaitAsync(Timeout);
                }
                catch (IOException ex) {
                    observed = ex;
                }
            });
        }
        catch (Exception ex) {
            observed = ex;
        }
        finally {
            release.TrySetResult();
        }

        if (operation is not null) {
            try {
                await operation.Completion.WaitAsync(Timeout);
            }
            catch (IOException ex) {
                observed ??= ex;
            }
        }

        using (Assert.EnterMultipleScope()) {
            Assert.That(observed, Is.SameAs(error));
            Assert.That(operation.Complete, Is.True);
            Assert.That(operation.ProgressRate, Is.Zero);
        }
    }

    [Test]
    public async Task SamplerListenerFailure_ReentrantCompletionWaitsForCallbackBeforeFaulting() {
        Operation operation = null;
        var release = new TaskCompletionSource(TaskCreationOptions.None);
        var error = new IOException("rate listener");
        Exception observed = null;
        try {
            await UiTestThread.Run(async () => {
                IOperator @operator = new Operator();
                var notification = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                operation = await Task.Run(() => Start(@operator, async (progress, _) => {
                    progress.Change(setProgress: 1);
                    await release.Task.ConfigureAwait(false);
                }, started => started.PropertyChanged += (_, args) => {
                    if (args.PropertyName == nameof(OperationBase.ProgressRate) && started.ProgressRate > 0) {
                        notification.TrySetResult();
                        release.TrySetResult();
                        throw error;
                    }
                })).WaitAsync(Timeout);

                await notification.Task.WaitAsync(TimeSpan.FromSeconds(4));
                try {
                    await operation.Completion.WaitAsync(Timeout);
                }
                catch (IOException ex) {
                    observed = ex;
                }
            });
        }
        catch (Exception ex) {
            observed = ex;
        }
        finally {
            release.TrySetResult();
        }

        if (operation is not null) {
            try {
                await operation.Completion.WaitAsync(Timeout);
            }
            catch (IOException ex) {
                observed ??= ex;
            }
        }

        using (Assert.EnterMultipleScope()) {
            Assert.That(observed, Is.SameAs(error));
            Assert.That(operation.Complete, Is.True);
            Assert.That(operation.ProgressRate, Is.Zero);
        }
    }

    [Test]
    public async Task Sampler_ReportsProgressAndThenAnIdleRateWithoutAnotherChange() {
        IOperator @operator = new Operator();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRate = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        var idleRate = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = Start(@operator, async (progress, _) => {
            progress.Change(setProgress: 1);
            await release.Task;
        }, operation => operation.PropertyChanged += (_, args) => {
            if (args.PropertyName == nameof(OperationBase.ProgressRate)) {
                var value = operation.ProgressRate;
                if (value == 0) {
                    idleRate.TrySetResult(value);
                }
                else {
                    firstRate.TrySetResult(value);
                }
            }
        });

        try {
            Assert.That(await firstRate.Task.WaitAsync(TimeSpan.FromSeconds(4)), Is.GreaterThan(0));
            Assert.That(await idleRate.Task.WaitAsync(TimeSpan.FromSeconds(4)), Is.Zero);
        }
        finally {
            release.TrySetResult();
        }

        await operation.Completion.WaitAsync(Timeout);
    }

    [Test]
    public async Task Sampler_ReportsProgressAfterProgressListenerFailure() {
        IOperator @operator = new Operator();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var listenerFailure = new InvalidOperationException("progress listener");
        var caughtFailure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<double> positiveRate = null;
        var operation = Start(@operator, async (progress, _) => {
            try {
                progress.Change(setProgress: 10);
            }
            catch (InvalidOperationException ex) {
                caughtFailure.TrySetResult(ex);
            }
            await release.Task;
        }, started => {
            positiveRate = WaitForPositiveRate(started);
            started.PropertyChanged += (_, args) => {
                if (args.PropertyName == nameof(OperationBase.Progress)) {
                    throw listenerFailure;
                }
            };
        });

        try {
            Assert.That(await caughtFailure.Task.WaitAsync(Timeout), Is.SameAs(listenerFailure));
            var rate = await positiveRate;

            using (Assert.EnterMultipleScope()) {
                Assert.That(operation.Progress, Is.EqualTo(10));
                Assert.That(rate, Is.GreaterThan(0));
                Assert.That(operation.ProgressRateAverage, Is.GreaterThan(0));
                Assert.That(operation.Complete, Is.False);
            }
        }
        finally {
            release.TrySetResult();
        }

        await operation.Completion.WaitAsync(Timeout);
    }

    [Test]
    public async Task Sampler_ReportsProgressDuringLongSynchronousDelegate() {
        IOperator @operator = new Operator();
        var operationReady = new TaskCompletionSource<Operation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<double> positiveRate = null;
        using var release = new ManualResetEventSlim();
        var start = Task.Factory.StartNew(
            () => Start(@operator, (progress, _) => {
                progress.Change(setProgress: 10);
                reported.TrySetResult();
                if (!release.Wait(Timeout, CancellationToken.None)) {
                    throw new TimeoutException("The synchronous operation was not released in time.");
                }
                return Task.CompletedTask;
            }, operation => {
                positiveRate = WaitForPositiveRate(operation);
                operationReady.TrySetResult(operation);
            }),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        Operation operation = null;

        try {
            operation = await operationReady.Task.WaitAsync(Timeout);
            await reported.Task.WaitAsync(Timeout);
            var rate = await positiveRate;

            using (Assert.EnterMultipleScope()) {
                Assert.That(rate, Is.GreaterThan(0));
                Assert.That(operation.ProgressRateAverage, Is.GreaterThan(0));
                Assert.That(start.IsCompleted, Is.False);
            }
        }
        finally {
            release.Set();
            await start.WaitAsync(Timeout);
            if (operation is not null) {
                await operation.Completion.WaitAsync(Timeout);
            }
        }
    }

    [Test]
    public async Task Sampler_ReportsDescendantProgressAfterSynchronousParentFailure() {
        IOperator @operator = new Operator();
        var releaseReport = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseChild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var childReported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var error = new IOException("parent sync failure");
        Task<double> positiveRate = null;
        var operation = Start(@operator, (progress, _) => {
            progress.Child("child", async (childProgress, _) => {
                await releaseReport.Task;
                childProgress.Change(setProgress: 10);
                childReported.TrySetResult();
                await releaseChild.Task;
            });
            throw error;
        }, started => positiveRate = WaitForPositiveRate(started));

        try {
            var child = await WaitForChild(operation);
            var childPositiveRate = WaitForPositiveRate(child);
            releaseReport.TrySetResult();
            await childReported.Task.WaitAsync(Timeout);
            var rate = await positiveRate;
            var childRate = await childPositiveRate;

            using (Assert.EnterMultipleScope()) {
                Assert.That(rate, Is.GreaterThan(0));
                Assert.That(childRate, Is.GreaterThan(0));
                Assert.That(operation.ProgressRateAverage, Is.GreaterThan(0));
                Assert.That(operation.Complete, Is.False);
                Assert.That(operation.Error, Is.SameAs(error));
            }
        }
        finally {
            releaseReport.TrySetResult();
            releaseChild.TrySetResult();
        }

        await operation.Completion.WaitAsync(Timeout);
    }

    [Test]
    public void Sampler_DoesNotDelayShortSynchronousOperationCompletion() {
        IOperator @operator = new Operator();
        var operation = Start(@operator, (progress, _) => {
            progress.Change(setProgress: 10);
            return Task.CompletedTask;
        });

        using (Assert.EnterMultipleScope()) {
            Assert.That(operation.Completion.IsCompleted, Is.True);
            Assert.That(operation.Complete, Is.True);
        }
    }

#if NET10_0
    [Test]
    public void Sampler_ShutdownDoesNotBlockABoundedThreadPool() {
        var targetDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        var outputDirectory = targetDirectory.Parent?.Parent ?? throw new DirectoryNotFoundException();
        var probe = Path.Combine(outputDirectory.FullName,
                                 "Brows.Operations.Tests.PoolProbe",
                                 targetDirectory.Name,
                                 "Brows.Operations.Tests.PoolProbe.dll");
        var startInfo = new ProcessStartInfo {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(probe);
        using var process = Process.Start(startInfo);
        Assert.That(process, Is.Not.Null);
        if (!process.WaitForExit(Timeout)) {
            process.Kill(entireProcessTree: true);
            Assert.Fail("The bounded-thread-pool probe did not exit before the watchdog timeout.");
        }
        Assert.That(process.ExitCode, Is.Zero,
                    $"Probe output: {process.StandardOutput.ReadToEnd()}{process.StandardError.ReadToEnd()}");
    }
#endif

    private static void AssertRates(OperationBase operation, double progressRate, double average) {
        Assert.That(operation.ProgressRate, Is.EqualTo(progressRate).Within(0.000001));
        Assert.That(operation.ProgressRateAverage, Is.EqualTo(average).Within(0.000001));
    }
}
