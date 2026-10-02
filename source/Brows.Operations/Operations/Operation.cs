using System;
using System.ComponentModel;
using System.Threading;

namespace Brows.Operations;

internal sealed class Operation : OperationBase {
    protected sealed override void OnPropertyChanged(PropertyChangedEventArgs e) {
        base.OnPropertyChanged(e);
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
    : base(name, null, task, synchronizationContext) {
    }
}
