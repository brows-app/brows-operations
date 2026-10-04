using System;
using System.ComponentModel;
using System.Threading;

namespace Brows.Operations;

internal sealed class Operation : OperationBase {
    protected sealed override void PublishPropertyChanged(PropertyChangedEventArgs e) {
        base.PublishPropertyChanged(e);
        if (e?.PropertyName is nameof(Complete)) {
            CanRemoveChanged?.Invoke(this, EventArgs.Empty);
            NotifyPropertyChanged(nameof(CanRemove));
        }
        else if (e?.PropertyName is nameof(Progressing) or nameof(Canceling)) {
            CanCancelChanged?.Invoke(this, EventArgs.Empty);
            NotifyPropertyChanged(nameof(CanCancel));
        }
    }

    public event EventHandler Removed;
    public event EventHandler CanCancelChanged;
    public event EventHandler CanRemoveChanged;

    public bool CanCancel => Progressing && !Canceling;
    public bool CanRemove => Complete;

    new public void Cancel() {
        base.Cancel();
    }

    public void Remove() {
        Removed?.Invoke(this, EventArgs.Empty);
    }

    public Operation(string name, OperationDelegate task, SynchronizationContext synchronizationContext = null)
    : this(name, task, new OperationSynchronization(synchronizationContext ?? SynchronizationContext.Current)) {
    }

    internal Operation(string name, OperationDelegate task, OperationSynchronization synchronization)
    : base(name, null, task, synchronization) {
    }
}
