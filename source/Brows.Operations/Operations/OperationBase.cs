using Brows.Shims;
using Domore.Logs;
using Domore.Notification;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TASK = System.Threading.Tasks.Task;

namespace Brows.Operations;

internal class OperationBase : Notifier, IOperation {
    private static readonly ILog Log = Logging.For(typeof(OperationBase));
    private static readonly PropertyChangedEventArgs CancelingEvent = new(nameof(Canceling));
    private static readonly PropertyChangedEventArgs CompleteEvent = new(nameof(Complete));
    private static readonly PropertyChangedEventArgs CompleteWithErrorEvent = new(nameof(CompleteWithError));
    private static readonly PropertyChangedEventArgs DataEvent = new(nameof(Data));
    private static readonly PropertyChangedEventArgs ErrorEvent = new(nameof(Error));
    private static readonly PropertyChangedEventArgs NameEvent = new(nameof(Name));
    private static readonly PropertyChangedEventArgs ProgressEvent = new(nameof(Progress));
    private static readonly PropertyChangedEventArgs ProgressingEvent = new(nameof(Progressing));
    private static readonly PropertyChangedEventArgs ProgressPercentEvent = new(nameof(ProgressPercent));
    private static readonly PropertyChangedEventArgs ProgressRateEvent = new(nameof(ProgressRate));
    private static readonly PropertyChangedEventArgs ProgressRateAverageEvent = new(nameof(ProgressRateAverage));
    private static readonly PropertyChangedEventArgs ProgressStringEvent = new(nameof(ProgressString));
    private static readonly PropertyChangedEventArgs ProgressStringFormatEvent = new(nameof(ProgressStringFormat));
    private static readonly PropertyChangedEventArgs ProgressKindEvent = new(nameof(ProgressKind));
    private static readonly PropertyChangedEventArgs RelevantEvent = new(nameof(Relevant));
    private static readonly PropertyChangedEventArgs TargetEvent = new(nameof(Target));
    private static readonly PropertyChangedEventArgs TargetStringEvent = new(nameof(TargetString));
    private static readonly PropertyChangedEventArgs TargetStringFormatEvent = new(nameof(TargetStringFormat));
    private static readonly PropertyChangedEventArgs FormatProviderEvent = new(nameof(FormatProvider));
    private static readonly PropertyChangedEventArgs[] ProgressDependents = [ProgressPercentEvent, ProgressStringEvent];
    private static readonly PropertyChangedEventArgs[] TargetDependents = [ProgressPercentEvent, TargetStringEvent];

    private bool MakingRelevant;
    private bool Started;
    private bool ChildCollectionClosed;
    private bool CancellationRequested;
    private bool RateSamplingClosed;
    private bool RateSamplingDisabled;
    private bool RateSamplerRefreshing;
    private int TokenSourceCancellationCount;
    private bool TokenSourceDisposalPending;
    private Stopwatch Stopwatch;
    private CancellationTokenSource TokenSource;
    private CancellationTokenSource RateSamplerTokenSource;
    private Task RateSamplerTask;
    private Exception RateSamplerError;
    private readonly OperationRate Rate = new();
    private readonly OperationSynchronization Synchronization;
    private readonly ProgressChangeContext ProgressChange;
    private readonly OperationBaseCollection ChildCollection;
    private readonly TaskCompletionSource<bool> CompletionSource =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void ReleaseTokenSourceCancellation(CancellationTokenSource tokenSource) {
        Synchronization.Update(() => {
            TokenSourceCancellationCount--;
            if (TokenSourceCancellationCount == 0 && TokenSourceDisposalPending) {
                TokenSourceDisposalPending = false;
                tokenSource.Dispose();
            }
        });
    }

    private void CancelTokenSource(
        CancellationTokenSource tokenSource,
        IEnumerable<(OperationBase Owner, CancellationTokenSource TokenSource)> leasedTokenSources = null) {
        try {
            tokenSource?.Cancel();
        }
        catch (AggregateException ex) {
            throw new AggregateException($"Cancellation of '{Name}' encountered callback failures.", ex)
                .Flatten();
        }
        finally {
            if (leasedTokenSources is null) {
                if (tokenSource is not null) {
                    ReleaseTokenSourceCancellation(tokenSource);
                }
            }
            else {
                foreach (var lease in leasedTokenSources) {
                    lease.Owner.ReleaseTokenSourceCancellation(lease.TokenSource);
                }
            }
        }
    }

    private void RecordFailure(Exception ex) {
        if (Log.Warn()) {
            Log.Warn(ex);
        }
        Synchronization.Update(() => {
            if (Error is null) {
                Error = ex;
            }
            Relevant = true;
        });
    }

    private OperationBase Child(string name, OperationDelegate task) {
        if (Log.Info()) {
            Log.Info(nameof(Child) + " > " + name);
        }
        var child = new OperationBase(name, this, task, Synchronization);
        ChildCollection.Add(child, () => {
            if (CancellationRequested) {
                throw new OperationCanceledException("The parent operation has been canceled.");
            }
            if (ChildCollectionClosed) {
                throw new InvalidOperationException("Child operations can only be added while the parent is running.");
            }
        });
        child.Start();
        return child;
    }

    private async void MakeRelevant() {
        CancellationToken token = default;
        Task delay = null;
        var start = Synchronization.Update(() => {
            if (Relevant || MakingRelevant || !Progressing) {
                return false;
            }
            MakingRelevant = true;
            token = TokenSource?.Token ?? default;
            delay = TASK.Delay(1000, token);
            return true;
        });
        if (!start) {
            return;
        }

        try {
            try {
                await delay;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) {
            }
            Synchronization.Update(() => {
                if (Progressing) {
                    Relevant = true;
                }
            });
        }
        finally {
            Synchronization.Update(() => MakingRelevant = false);
        }
    }

    private void NotifyTargetChanged() {
        NotifyPropertyChanged(TargetEvent, TargetDependents);
        Synchronization.Notify(() => TargetChanged?.Invoke(this, EventArgs.Empty));
        MakeRelevant();
    }

    private void NotifyProgressChanged() {
        NotifyPropertyChanged(ProgressEvent, ProgressDependents);
        Synchronization.Notify(() => ProgressChanged?.Invoke(this, EventArgs.Empty));
        MakeRelevant();
    }

    private async Task SampleRates(CancellationToken token) {
        try {
            while (true) {
                await TASK.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                var refresh = Synchronization.Update(() => {
                    if (RateSamplingClosed || Complete) {
                        return false;
                    }
                    RateSamplerRefreshing = true;
                    return true;
                });
                if (!refresh) {
                    return;
                }
                try {
                    RefreshRates();
                }
                catch (Exception ex) {
                    RateSamplerError = ex;
                    return;
                }
                finally {
                    Synchronization.Update(() => {
                        RateSamplerRefreshing = false;
                    });
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) {
        }
        catch (Exception ex) {
            RateSamplerError = ex;
        }
    }

    private void StartRateSampler() {
        RateSamplerTokenSource = new CancellationTokenSource();
        RateSamplerTask = SampleRates(RateSamplerTokenSource.Token);
    }

    private void RequestRateSampler() {
        var root = this;
        while (root.Parent is not null) {
            root = root.Parent;
        }
        root.Synchronization.Update(() => {
            if (!root.RateSamplingDisabled && root.Progressing && !root.Complete && !root.RateSamplingClosed) {
                if (root.RateSamplerTokenSource is null) {
                    root.StartRateSampler();
                }
            }
        });
    }

    private async Task StopRateSampler() {
        var source = RateSamplerTokenSource;
        if (source is null) {
            return;
        }
        var task = RateSamplerTask;
        try {
            source.Cancel();
            var refreshing = Synchronization.Read(() => RateSamplerRefreshing);
            /*
             * The root closes sampling before shutdown. An idle sampler can only observe cancellation or
             * that closed state, so do not block its caller on a queued continuation that may need the
             * same thread-pool worker. A listener can complete the operation inline during refresh;
             * awaiting an active refresh lets it return before finalization reads any error it raised.
             */
            if (refreshing) {
                await task.ConfigureAwait(false);
            }
        }
        finally {
            source.Dispose();
            RateSamplerTokenSource = null;
            RateSamplerTask = null;
        }
    }

    private void RefreshRates() => RefreshRates(operation => operation.Stopwatch?.Elapsed ?? TimeSpan.Zero);

    private void CompleteRate() {
        var previousRate = Rate.ProgressRate;
        var previousAverage = Rate.ProgressRateAverage;
        Rate.Complete(Progress, Stopwatch?.Elapsed ?? TimeSpan.Zero);
        if (Rate.ProgressRate != previousRate) {
            NotifyPropertyChanged(ProgressRateEvent);
        }
        if (Rate.ProgressRateAverage != previousAverage) {
            NotifyPropertyChanged(ProgressRateAverageEvent);
        }
    }

    private void ChangeProgress(long? setProgress = null,
                                long? addProgress = null,
                                long? setTarget = null,
                                long? addTarget = null) {
        Synchronization.Update(() => {
            /*
             * Defer notifications until the child and all its ancestors have committed the update.
             */
            var
            change = ProgressChange;
            change.Depth++;
            try {
                core();
            }
            finally {
                change.Depth--;
                if (change.Depth == 0) {
                    Flush(change);
                }
            }
            void core() {
                var changed = false;
                var changedTarget = false;
                var changedProgress = false;
                if (setProgress.HasValue) {
                    var
                    value = setProgress.Value;
                    changed = true;
                    changedProgress = true;
                    if (Parent is not null) {
                        Parent.ChangeProgress(setProgress: Parent.Progress + (value - Progress));
                    }
                    Progress = value;
                }
                if (addProgress.HasValue) {
                    var
                    value = addProgress.Value;
                    changed = true;
                    changedProgress = true;
                    if (Parent is not null) {
                        Parent.ChangeProgress(addProgress: value);
                    }
                    Progress += value;
                }
                if (setTarget.HasValue) {
                    var
                    value = setTarget.Value;
                    changed = true;
                    changedTarget = true;
                    if (Parent is not null) {
                        Parent.ChangeProgress(setTarget: Parent.Target + (value - Target));
                    }
                    Target = value;
                }
                if (addTarget.HasValue) {
                    var
                    value = addTarget.Value;
                    changed = true;
                    changedTarget = true;
                    if (Parent is not null) {
                        Parent.ChangeProgress(addTarget: value);
                    }
                    Target += value;
                }
                if (changed) {
                    ProgressPercent = Target == 0
                        ? 0
                        : (double)Progress / Target * 100;
                }
                if (changedTarget || changedProgress) {
                    ProgressChange.Notifications.Enqueue((this, changedTarget, changedProgress));
                }
            }
        });
    }

    private static void Flush(ProgressChangeContext change) {
        if (change.Dispatching) {
            return;
        }
        change.Dispatching = true;
        try {
            while (change.Notifications.TryDequeue(out var notification)) {
                if (notification.Target) {
                    notification.Operation.NotifyTargetChanged();
                }
                if (notification.Progress) {
                    notification.Operation.NotifyProgressChanged();
                }
            }
        }
        finally {
            change.Dispatching = false;
        }
    }

    private async TASK Operate() {
        if (Log.Info()) {
            Log.Info(nameof(Operate));
        }
        var parentToken = Parent is null
            ? default
            : Synchronization.Read(() => Parent.TokenSource?.Token ?? default);
        var tokenSource = Parent is null
            ? new CancellationTokenSource()
            : CancellationTokenSource.CreateLinkedTokenSource(parentToken);
        var token = tokenSource.Token;
        try {
            var cancelToken = Synchronization.Update(() => {
                TokenSource = tokenSource;
                Stopwatch = Stopwatch.StartNew();
                Rate.Start(Progress, TimeSpan.Zero);
                Progressing = true;
                if (!CancellationRequested) {
                    return false;
                }
                TokenSourceCancellationCount++;
                return true;
            });
            if (cancelToken) {
                CancelTokenSource(tokenSource);
            }
            var progress = new ProgressWrapper(this);
            Exception taskError = null;
            try {
                if (!token.IsCancellationRequested && Synchronization.Read(() => !CancellationRequested)) {
                    var work = Task(progress, token);
                    await work;
                }
            }
            catch (Exception ex) {
                taskError = ex;
            }
            finally {
                Synchronization.Update(() => ChildCollectionClosed = true);
            }
            if (taskError is not null) {
                if (taskError is OperationCanceledException && token.IsCancellationRequested) {
                    if (Log.Debug()) {
                        Log.Debug($"Operation canceled: {Name}",
                                  $"              Data: {Data}");
                    }
                }
                else {
                    RecordFailure(taskError);
                }
            }
        }
        finally {
            Synchronization.Update(() => ChildCollectionClosed = true);
            try {
                var children = ChildCollection.ToArray();
                await TASK.WhenAll(children.Select(child => child.Completion));
            }
            finally {
                if (Parent is null) {
                    Synchronization.Update(() => {
                        RateSamplingClosed = true;
                        Stopwatch?.Stop();
                    });
                    await StopRateSampler();
                }
                Synchronization.Update(() => {
                    Stopwatch?.Stop();
                    CompleteRate();
                    var source = TokenSource;
                    TokenSource = null;
                    if (source is not null) {
                        if (TokenSourceCancellationCount == 0) {
                            source.Dispose();
                        }
                        else {
                            TokenSourceDisposalPending = true;
                        }
                    }
                    Progressing = false;
                    CompleteWithError = Error is not null || ChildCollection.Any(child => child.CompleteWithError);
                    if (CompleteWithError) {
                        Relevant = true;
                    }
                    else if (Target == 0) {
                        ProgressPercent = 100;
                        NotifyPropertyChanged(ProgressPercentEvent);
                    }
                    Complete = true;
                });
                Completed?.Invoke(this, EventArgs.Empty);
            }
            if (Parent is null && RateSamplerError is not null) {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(RateSamplerError).Throw();
            }
        }
    }

    private async Task RunOperation() {
        try {
            await Operate();
            CompletionSource.TrySetResult(true);
        }
        catch (Exception ex) {
            CompletionSource.TrySetException(ex);
        }
    }

    private static async void ObserveCompletion(Task completion) {
        /*
         * Surface unexpected root failures through the observer's captured synchronization context.
         */
        await completion;
    }

    private static void ObserveCompletion(Task completion, SynchronizationContext synchronizationContext) {
        if (completion.IsCompleted && !completion.IsFaulted && !completion.IsCanceled) {
            return;
        }
        if (synchronizationContext is null || SynchronizationContext.Current == synchronizationContext) {
            ObserveCompletion(completion);
        }
        else {
            synchronizationContext.Post(_ => ObserveCompletion(completion), null);
        }
    }

    internal event EventHandler RelevantCommitted;

    internal void Start() {
        Synchronization.Update(() => {
            if (Started) {
                throw new InvalidOperationException("The operation has already started.");
            }
            Started = true;
        });
        _ = RunOperation();
        if (Parent is null) {
            ObserveCompletion(CompletionSource.Task, Synchronization.SynchronizationContext);
        }
    }

    internal void RefreshRates(Func<OperationBase, TimeSpan> elapsedTime) {
        if (elapsedTime is null) {
            throw new ArgumentNullException(nameof(elapsedTime));
        }
        Synchronization.Update(() => {
            if (RateSamplingClosed || Complete) {
                return;
            }
            refresh(this);

            void refresh(OperationBase operation) {
                if (operation.Progressing && !operation.Complete) {
                    var previousRate = operation.Rate.ProgressRate;
                    var previousAverage = operation.Rate.ProgressRateAverage;
                    if (operation.Rate.Sample(operation.Progress, elapsedTime(operation))) {
                        if (operation.Rate.ProgressRate != previousRate) {
                            operation.NotifyPropertyChanged(ProgressRateEvent);
                        }
                        if (operation.Rate.ProgressRateAverage != previousAverage) {
                            operation.NotifyPropertyChanged(ProgressRateAverageEvent);
                        }
                    }
                }
                foreach (var child in operation.ChildCollection.ToArray()) {
                    refresh(child);
                }
            }
        });
    }

    internal void DisableRateSampling() {
        Synchronization.Update(() => {
            if (Started) {
                throw new InvalidOperationException("Rate sampling must be disabled before the operation starts.");
            }
            RateSamplingDisabled = true;
        });
    }

    protected sealed override void OnPropertyChanged(PropertyChangedEventArgs e) {
        Synchronization.Notify(() => PublishPropertyChanged(e));
    }

    protected virtual void PublishPropertyChanged(PropertyChangedEventArgs e) {
        base.OnPropertyChanged(e);
    }

    protected void Cancel() {
        CancellationTokenSource tokenSource = null;
        var leasedTokenSources = new List<(OperationBase Owner, CancellationTokenSource TokenSource)>();
        Synchronization.Update(() => {
            if (CancellationRequested) {
                return;
            }
            if (Log.Info()) {
                Log.Info($"Cancel: {Name}");
            }
            /*
             * Mark the tree before cancellation callbacks can complete it synchronously.
             */
            cancel(this);
            tokenSource = TokenSource;
            void cancel(OperationBase op) {
                if (op.CancellationRequested) {
                    return;
                }
                op.CancellationRequested = true;
                op.Canceling = true;
                if (op.TokenSource is not null) {
                    op.TokenSourceCancellationCount++;
                    leasedTokenSources.Add((op, op.TokenSource));
                }
                foreach (var child in op.ChildCollection.ToArray()) {
                    cancel(child);
                }
            }
        });
        CancelTokenSource(tokenSource, leasedTokenSources);
    }

    public event EventHandler Completed;
    public event EventHandler ProgressChanged;
    public event EventHandler RelevantChanged;
    public event EventHandler TargetChanged;

    public long Target {
        get => Synchronization.Read(() => _Target);
        private set => Synchronization.Update(() => _Target = value);
    }
    private long _Target;

    public long Progress {
        get => Synchronization.Read(() => _Progress);
        private set => Synchronization.Update(() => _Progress = value);
    }
    private long _Progress;

    public double ProgressRate => Synchronization.Read(() => Rate.ProgressRate);

    public double ProgressRateAverage => Synchronization.Read(() => Rate.ProgressRateAverage);

    public double ProgressPercent {
        get => Synchronization.Read(() => field);
        private set => Synchronization.Update(() => field = value);
    }

    public OperationProgressKind ProgressKind {
        get => Synchronization.Read(() => _ProgressKind);
    }
    private OperationProgressKind _ProgressKind;

    public string TargetString {
        get => Synchronization.Read(() => _TargetString ?? _ProgressKind switch {
            OperationProgressKind.FileSize =>
                OperationFileSize.ToString(_Target,
                                           _TargetStringFormat,
                                           _FormatProvider),
            _ => _Target.ToString()
        });
    }
    private string _TargetString;

    public string TargetStringFormat {
        get => Synchronization.Read(() => _TargetStringFormat);
    }
    private string _TargetStringFormat;

    public string ProgressString {
        get => Synchronization.Read(() => _ProgressString ?? _ProgressKind switch {
            OperationProgressKind.FileSize =>
                OperationFileSize.ToString(_Progress,
                                           _ProgressStringFormat,
                                           _FormatProvider),
            _ => _Progress.ToString()
        });
    }
    private string _ProgressString;

    public string ProgressStringFormat {
        get => Synchronization.Read(() => _ProgressStringFormat);
    }
    private string _ProgressStringFormat;

    public IFormatProvider FormatProvider {
        get => Synchronization.Read(() => _FormatProvider);
    }
    private IFormatProvider _FormatProvider;

    public string Name {
        get => Synchronization.Read(() => _Name);
        private set => Synchronization.Update(() => Change(ref _Name, value, NameEvent));
    }
    private string _Name;

    public string Data {
        get => Synchronization.Read(() => _Data);
    }
    private string _Data;

    public bool Relevant {
        get => Synchronization.Read(() => field);
        private set {
            Synchronization.Update(() => {
                if (Change(ref field, value, RelevantEvent)) {
                    if (value && Parent is not null) {
                        Parent.Relevant = true;
                    }
                    /*
                     * Commit collection relevance in this state transaction before publishing external notifications.
                     */
                    RelevantCommitted?.Invoke(this, EventArgs.Empty);
                    Synchronization.Notify(() => RelevantChanged?.Invoke(this, EventArgs.Empty));
                }
            });
        }
    }

    public bool Canceling {
        get => Synchronization.Read(() => field);
        private set => Synchronization.Update(() => Change(ref field, value, CancelingEvent));
    }

    public Exception Error {
        get => Synchronization.Read(() => field);
        private set => Synchronization.Update(() => Change(ref field, value, ErrorEvent));
    }

    public bool Progressing {
        get => Synchronization.Read(() => field);
        private set => Synchronization.Update(() => Change(ref field, value, ProgressingEvent));
    }

    public object ChildSource => ChildCollection.Source;

    public int Depth { get; }

    public string DepthString =>
        Synchronization.Update(() => field ??= new string('>', Depth));

    public bool CompleteWithError {
        get => Synchronization.Read(() => field);
        private set => Synchronization.Update(() => Change(ref field, value, CompleteWithErrorEvent));
    }

    public bool Complete {
        get => Synchronization.Read(() => field);
        private set => Synchronization.Update(() => Change(ref field, value, CompleteEvent));
    }

    public TASK Completion =>
        CompletionSource.Task;
    public OperationBase Parent { get; }
    public OperationDelegate Task { get; }

    public OperationBase(string name,
                        OperationBase parent,
                        OperationDelegate task,
                        SynchronizationContext synchronizationContext = null)
    : this(name, parent, task, parent?.Synchronization ?? new OperationSynchronization(
        synchronizationContext ?? SynchronizationContext.Current)) {
    }

    internal OperationBase(string name,
                           OperationBase parent,
                           OperationDelegate task,
                           OperationSynchronization synchronization) {
        Task = task ?? throw new ArgumentNullException(nameof(task));
        Parent = parent;
        Synchronization = synchronization ?? throw new ArgumentNullException(nameof(synchronization));
        ProgressChange = Parent?.ProgressChange ?? new ProgressChangeContext();
        Depth = Parent == null ? 0 : (Parent.Depth + 1);
        ChildCollection = new OperationBaseCollection(Synchronization);
        Name = name;
    }

    private sealed class ProgressWrapper : IOperationProgress {
        public OperationBase Operation { get; }

        public ProgressWrapper(OperationBase operation) {
            Operation = operation ?? throw new ArgumentNullException(nameof(operation));
        }

        public void Change(long? addProgress,
                           long? setProgress,
                           long? addTarget,
                           long? setTarget,
                           string progressString,
                           string targetString,
                           string name,
                           string data,
                           OperationProgressKind? kind,
                           string progressStringFormat,
                           string targetStringFormat,
                           IFormatProvider formatProvider) {
            Operation.Synchronization.Update(() => {
                if (name is not null) {
                    Operation.Change(ref Operation._Name, name, NameEvent);
                }
                if (data is not null) {
                    Operation.Change(ref Operation._Data, data, DataEvent);
                }
                var kindChanged = false;
                if (kind.HasValue) {
                    kindChanged = Operation.Change(
                        ref Operation._ProgressKind, kind.Value, ProgressKindEvent);
                }
                var progressStringFormatChanged = false;
                if (progressStringFormat is not null) {
                    progressStringFormatChanged = Operation.Change(
                        ref Operation._ProgressStringFormat, progressStringFormat, ProgressStringFormatEvent);
                }
                var targetStringFormatChanged = false;
                if (targetStringFormat is not null) {
                    targetStringFormatChanged = Operation.Change(
                        ref Operation._TargetStringFormat, targetStringFormat, TargetStringFormatEvent);
                }
                var formatProviderChanged = false;
                if (formatProvider is not null && !ReferenceEquals(Operation._FormatProvider, formatProvider)) {
                    formatProviderChanged = true;
                    Operation._FormatProvider = formatProvider;
                    Operation.NotifyPropertyChanged(FormatProviderEvent);
                }
                var notNone = Operation._ProgressKind != OperationProgressKind.None;
                var progressDisplayFormatChanged =
                    kindChanged || (notNone && (progressStringFormatChanged || formatProviderChanged));
                var targetDisplayFormatChanged =
                    kindChanged || (notNone && (targetStringFormatChanged || formatProviderChanged));
                var progressUsesDefault = Operation._ProgressString is null && progressString is null;
                var targetUsesDefault = Operation._TargetString is null && targetString is null;
                Operation.ChangeProgress(addProgress: addProgress,
                                         setProgress: setProgress,
                                         addTarget: addTarget,
                                         setTarget: setTarget);
                Operation.Change(ref Operation._ProgressString, progressString, ProgressStringEvent);
                Operation.Change(ref Operation._TargetString, targetString, TargetStringEvent);
                var notifyProgressStringChanged = progressDisplayFormatChanged &&
                                                  progressUsesDefault &&
                                                  !setProgress.HasValue &&
                                                  !addProgress.HasValue;
                if (notifyProgressStringChanged) {
                    Operation.NotifyPropertyChanged(ProgressStringEvent);
                }
                var notifyTargetStringChanged = targetDisplayFormatChanged &&
                                                targetUsesDefault &&
                                                !setTarget.HasValue &&
                                                !addTarget.HasValue;
                if (notifyTargetStringChanged) {
                    Operation.NotifyPropertyChanged(TargetStringEvent);
                }
                if (addProgress.HasValue || setProgress.HasValue) {
                    Operation.RequestRateSampler();
                }
            });
        }

        public async Task Child(string name, OperationDelegate task) {
            var child = Operation.Child(name, task);
            await child.Completion;
        }

        public async Task<bool> Children<T>(IEnumerable<T> source, Func<T, OperationChild> childFactory) {
            if (source is null) {
                throw new ArgumentNullException(nameof(source));
            }
            if (childFactory is null) {
                throw new ArgumentNullException(nameof(childFactory));
            }
            var tasks = source
                .Select(item => {
                    var child = childFactory(item);
                    return child is null
                        ? null
                        : Child(child.Name, child.Task);
                })
                .Where(child => child is not null)
                .ToList();
            if (tasks.Count == 0) {
                return false;
            }
            await TASK.WhenAll(tasks);
            return true;
        }
    }

    private sealed class ProgressChangeContext {
        public Queue<(OperationBase Operation, bool Target, bool Progress)> Notifications { get; } = [];
        public int Depth { get; set; }
        public bool Dispatching { get; set; }
    }
}
