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
    private sealed class ProgressChangeContext {
        public Queue<(OperationBase Operation, bool Target, bool Progress)> Notifications { get; } = [];
        public int Depth { get; set; }
        public bool Dispatching { get; set; }
    }

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
    private static readonly PropertyChangedEventArgs ProgressStringEvent = new(nameof(ProgressString));
    private static readonly PropertyChangedEventArgs RelevantEvent = new(nameof(Relevant));
    private static readonly PropertyChangedEventArgs TargetEvent = new(nameof(Target));
    private static readonly PropertyChangedEventArgs TargetStringEvent = new(nameof(TargetString));
    private static readonly PropertyChangedEventArgs[] ProgressDependents = [ProgressPercentEvent, ProgressStringEvent];
    private static readonly PropertyChangedEventArgs[] TargetDependents = [ProgressPercentEvent, TargetStringEvent];

    private bool MakingRelevant;
    private bool Started;
    private bool ChildCollectionClosed;
    private bool CancellationRequested;
    private int TokenSourceCancellationCount;
    private bool TokenSourceDisposalPending;
    private Stopwatch Stopwatch;
    private CancellationTokenSource TokenSource;
    private long TargetValue;
    private long ProgressValue;
    private double ProgressPercentValue;
    private string TargetStringValue;
    private string ProgressStringValue;
    private string NameValue;
    private string DataValue;
    private bool RelevantValue;
    private bool CancelingValue;
    private Exception ErrorValue;
    private bool ProgressingValue;
    private string DepthStringValue;
    private bool CompleteWithErrorValue;
    private bool CompleteValue;
    private readonly TaskCompletionSource<bool> CompletionSource =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly OperationSynchronization Synchronization;
    private readonly ProgressChangeContext ProgressChange;
    private readonly OperationBaseCollection ChildCollection;

    private void ReleaseTokenSourceCancellation(CancellationTokenSource tokenSource) {
        Synchronization.Update(() => {
            TokenSourceCancellationCount--;
            if (TokenSourceCancellationCount == 0 && TokenSourceDisposalPending) {
                TokenSourceDisposalPending = false;
                tokenSource.Dispose();
            }
        });
    }

    private void CancelTokenSource(CancellationTokenSource tokenSource,
                                  IEnumerable<CancellationTokenSource> leasedTokenSources = null) {
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
                foreach (var leasedTokenSource in leasedTokenSources) {
                    ReleaseTokenSourceCancellation(leasedTokenSource);
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
        TargetChanged?.Invoke(this, EventArgs.Empty);
        MakeRelevant();
    }

    private void NotifyProgressChanged() {
        NotifyPropertyChanged(ProgressEvent, ProgressDependents);
        ProgressChanged?.Invoke(this, EventArgs.Empty);
        MakeRelevant();
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

    private async Task Operate() {
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
                    await Task(progress, token);
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
                Synchronization.Update(() => {
                    Stopwatch?.Stop();
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

    protected void Cancel() {
        CancellationTokenSource tokenSource = null;
        var leasedTokenSources = new List<CancellationTokenSource>();
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
                    leasedTokenSources.Add(op.TokenSource);
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
        get => Synchronization.Read(() => TargetValue);
        private set => Synchronization.Update(() => TargetValue = value);
    }

    public long Progress {
        get => Synchronization.Read(() => ProgressValue);
        private set => Synchronization.Update(() => ProgressValue = value);
    }

    public double ProgressPercent {
        get => Synchronization.Read(() => ProgressPercentValue);
        private set => Synchronization.Update(() => ProgressPercentValue = value);
    }

    public string TargetString {
        get => Synchronization.Read(() => TargetStringValue ?? TargetValue.ToString());
        private set => Synchronization.Update(() => Change(ref TargetStringValue, value, TargetStringEvent));
    }

    public string ProgressString {
        get => Synchronization.Read(() => ProgressStringValue ?? ProgressValue.ToString());
        private set => Synchronization.Update(() => Change(ref ProgressStringValue, value, ProgressStringEvent));
    }

    public string Name {
        get => Synchronization.Read(() => NameValue);
        private set => Synchronization.Update(() => Change(ref NameValue, value, NameEvent));
    }

    public string Data {
        get => Synchronization.Read(() => DataValue);
        private set => Synchronization.Update(() => Change(ref DataValue, value, DataEvent));
    }

    public bool Relevant {
        get => Synchronization.Read(() => RelevantValue);
        private set {
            Synchronization.Update(() => {
                if (Change(ref RelevantValue, value, RelevantEvent)) {
                    if (value && Parent is not null) {
                        Parent.Relevant = true;
                    }
                    RelevantChanged?.Invoke(this, EventArgs.Empty);
                }
            });
        }
    }

    public bool Canceling {
        get => Synchronization.Read(() => CancelingValue);
        private set => Synchronization.Update(() => Change(ref CancelingValue, value, CancelingEvent));
    }

    public Exception Error {
        get => Synchronization.Read(() => ErrorValue);
        private set => Synchronization.Update(() => Change(ref ErrorValue, value, ErrorEvent));
    }

    public bool Progressing {
        get => Synchronization.Read(() => ProgressingValue);
        private set => Synchronization.Update(() => Change(ref ProgressingValue, value, ProgressingEvent));
    }

    public object ChildSource => ChildCollection.Source;

    public int Depth { get; }

    public string DepthString =>
        Synchronization.Update(() => DepthStringValue ??= new string('>', Depth));

    public bool CompleteWithError {
        get => Synchronization.Read(() => CompleteWithErrorValue);
        private set => Synchronization.Update(() => Change(ref CompleteWithErrorValue, value, CompleteWithErrorEvent));
    }

    public bool Complete {
        get => Synchronization.Read(() => CompleteValue);
        private set => Synchronization.Update(() => Change(ref CompleteValue, value, CompleteEvent));
    }

    public Task Completion =>
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
                           string data) {
            Operation.Synchronization.Update(() => {
                if (name is not null) {
                    Operation.Name = name;
                }
                if (data is not null) {
                    Operation.Data = data;
                }
                Operation.ChangeProgress(addProgress: addProgress,
                                         setProgress: setProgress,
                                         addTarget: addTarget,
                                         setTarget: setTarget);
                Operation.ProgressString = progressString;
                Operation.TargetString = targetString;
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
}
