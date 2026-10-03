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

    private int Started;
    private bool MakingRelevant;
    private bool ChildCollectionClosed;
    private bool CancellationRequested;
    private Stopwatch Stopwatch;
    private CancellationTokenSource TokenSource;
    private readonly TaskCompletionSource<bool> CompletionSource = new();
    private readonly ProgressChangeContext ProgressChange;
    private readonly OperationBaseCollection ChildCollection = [];

    private void RecordFailure(Exception ex) {
        if (Log.Warn()) {
            Log.Warn(ex);
        }
        if (Error is null) {
            Error = ex;
        }
        Relevant = true;
    }

    private OperationBase Register(string name, OperationDelegate task) {
        if (CancellationRequested) {
            throw new OperationCanceledException("The parent operation has been canceled.");
        }
        if (ChildCollectionClosed) {
            throw new InvalidOperationException("Child operations can only be added while the parent is running.");
        }
        if (Log.Info()) {
            Log.Info(nameof(Child) + " > " + name);
        }
        var child = new OperationBase(name, this, task, Context);
        ChildCollection.Add(child);
        child.Prepare();
        return child;
    }

    private async Task<OperationBase> Child(string name, OperationDelegate task) {
        var child = await Context.InvokeAsync(() => Register(name, task));
        child.Start();
        return child;
    }

    private async void MakeRelevant() {
        if (Relevant) {
            return;
        }
        if (MakingRelevant) {
            return;
        }
        MakingRelevant = true;

        try {
            var token = TokenSource?.Token ?? default;
            try {
                await TASK.Delay(1000, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) {
            }
            Context.Invoke(() => {
                if (Progressing) {
                    Relevant = true;
                }
            });
        }
        finally {
            Context.Invoke(() => MakingRelevant = false);
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

    private void Apply(ProgressReport report) {
        if (report.Name is not null) {
            Name = report.Name;
        }
        if (report.Data is not null) {
            Data = report.Data;
        }
        ChangeProgress(addProgress: report.AddProgress,
                       setProgress: report.SetProgress,
                       addTarget: report.AddTarget,
                       setTarget: report.SetTarget);
        ProgressString = report.ProgressString;
        TargetString = report.TargetString;
    }

    private void ChangeProgress(long? setProgress = null,
                                long? addProgress = null,
                                long? setTarget = null,
                                long? addTarget = null) {
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

    private void Finish() {
        Stopwatch?.Stop();
        var source = TokenSource;
        TokenSource = null;
        if (source is not null) {
            Context.Dispose(source);
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
        Completed?.Invoke(this, EventArgs.Empty);
    }

    private async Task Operate() {
        if (Log.Info()) {
            Log.Info(nameof(Operate));
        }
        try {
            var token = TokenSource.Token;
            Exception taskError = null;
            try {
                if (!token.IsCancellationRequested) {
                    await Task(new ProgressWrapper(this), token);
                }
            }
            catch (Exception ex) {
                taskError = ex;
            }
            finally {
                await Context.InvokeAsync(() => ChildCollectionClosed = true);
            }
            if (taskError is not null) {
                if (taskError is OperationCanceledException && token.IsCancellationRequested) {
                    if (Log.Debug()) {
                        Log.Debug($"Operation canceled: {Name}",
                                  $"              Data: {Data}");
                    }
                }
                else {
                    await Context.InvokeAsync(() => RecordFailure(taskError));
                }
            }
        }
        finally {
            try {
                var children = await Context.InvokeAsync(() => {
                    ChildCollectionClosed = true;
                    return ChildCollection.ToArray();
                });
                await TASK.WhenAll(children.Select(child => child.Completion));
            }
            finally {
                await Context.InvokeAsync(Finish);
            }
        }
    }

    private async Task RunOperation() {
        try {
            await Operate();
            CompletionSource.TrySetResult(true);
        }
        catch (OperationCanceledException) {
            CompletionSource.TrySetCanceled();
        }
        catch (Exception ex) {
            CompletionSource.TrySetException(ex);
        }
    }

    private void CancelCore() {
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
        Context.BeginCancellation();
        try {
            TokenSource?.Cancel();
        }
        catch (AggregateException ex) {
            throw new AggregateException($"Cancellation of '{Name}' encountered callback failures.", ex)
                .Flatten();
        }
        finally {
            Context.EndCancellation();
        }
        static void cancel(OperationBase op) {
            if (op.CancellationRequested) {
                return;
            }
            op.CancellationRequested = true;
            op.Canceling = true;
            foreach (var child in op.ChildCollection.ToArray()) {
                cancel(child);
            }
        }
    }

    private static async void ObserveCompletion(Task completion) {
        /*
         * Surface unexpected root failures through the operator's synchronization context.
         */
        await completion;
    }

    internal OperationContext Context { get; }

    internal void Prepare() {
        TokenSource = Parent is null
            ? new CancellationTokenSource()
            : CancellationTokenSource.CreateLinkedTokenSource(Parent.TokenSource?.Token ?? default);
        if (CancellationRequested && !TokenSource.IsCancellationRequested) {
            Context.BeginCancellation();
            try {
                TokenSource.Cancel();
            }
            finally {
                Context.EndCancellation();
            }
        }
        Stopwatch = Stopwatch.StartNew();
        Progressing = true;
    }

    internal void Start() {
        if (Interlocked.Exchange(ref Started, 1) != 0) {
            throw new InvalidOperationException("The operation has already started.");
        }
        _ = RunOperation();
        if (Parent is null) {
            Context.Invoke(() => ObserveCompletion(Completion));
        }
    }

    protected void Cancel() {
        Context.Invoke(CancelCore);
    }

    public event EventHandler Completed;
    public event EventHandler ProgressChanged;
    public event EventHandler RelevantChanged;
    public event EventHandler TargetChanged;

    public long Target { get; private set; }
    public long Progress { get; private set; }
    public double ProgressPercent { get; private set; }

    public string TargetString {
        get => field ?? Target.ToString();
        private set => Change(ref field, value, TargetStringEvent);
    }

    public string ProgressString {
        get => field ?? Progress.ToString();
        private set => Change(ref field, value, ProgressStringEvent);
    }

    public string Name {
        get;
        private set => Change(ref field, value, NameEvent);
    }

    public string Data {
        get;
        private set => Change(ref field, value, DataEvent);
    }

    public bool Relevant {
        get;
        private set {
            if (Change(ref field, value, RelevantEvent)) {
                if (value && Parent is not null) {
                    Parent.Relevant = true;
                }
                RelevantChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public bool Canceling {
        get;
        private set => Change(ref field, value, CancelingEvent);
    }

    public Exception Error {
        get;
        private set => Change(ref field, value, ErrorEvent);
    }

    public bool Progressing {
        get;
        private set => Change(ref field, value, ProgressingEvent);
    }

    public object ChildSource => ChildCollection.Source;

    public int Depth { get; }

    public string DepthString => field ??=
        new string('>', Depth);

    public bool CompleteWithError {
        get;
        private set => Change(ref field, value, CompleteWithErrorEvent);
    }

    public bool Complete {
        get;
        private set => Change(ref field, value, CompleteEvent);
    }

    public Task Completion => CompletionSource.Task;
    public OperationBase Parent { get; }
    public OperationDelegate Task { get; }

    public OperationBase(string name,
                         OperationBase parent,
                         OperationDelegate task,
                         OperationContext context) {
        Task = task ?? throw new ArgumentNullException(nameof(task));
        Context = context ?? throw new ArgumentNullException(nameof(context));
        if (parent is not null && parent.Context != context) {
            throw new ArgumentException(paramName: nameof(context), message: "Operation context mismatch.");
        }
        Name = name;
        Parent = parent;
        ProgressChange = Parent?.ProgressChange ?? new ProgressChangeContext();
        Depth = Parent == null ? 0 : (Parent.Depth + 1);
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
            var report = new ProgressReport(addProgress: addProgress,
                                            setProgress: setProgress,
                                            addTarget: addTarget,
                                            setTarget: setTarget,
                                            progressString: progressString,
                                            targetString: targetString,
                                            name: name,
                                            data: data);
            Operation.Context.Report(Operation, report, Operation.Apply);
        }

        public async Task Child(string name, OperationDelegate task) {
            var child = await Operation.Child(name, task);
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
