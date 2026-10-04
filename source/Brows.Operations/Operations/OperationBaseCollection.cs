using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Brows.Operations;

internal sealed class OperationBaseCollection : IEnumerable<OperationBase> {
    private readonly OperationSynchronization Synchronization;
    private readonly List<OperationBase> Core = [];
    private readonly ObservableCollection<OperationBase> Observable = [];

    private OperationBase[] Snapshot() =>
        Synchronization.Read(() => Core.ToArray());

    public IEnumerable Source =>
        Observable;

    public OperationBaseCollection(OperationSynchronization synchronization) {
        Synchronization = synchronization;
    }

    public void Add(OperationBase item, System.Action validate) {
        Synchronization.Update(() => {
            validate();
            Core.Add(item);
        }, () => Observable.Add(item));
    }

    IEnumerator<OperationBase> IEnumerable<OperationBase>.GetEnumerator() =>
        ((IEnumerable<OperationBase>)Snapshot()).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() =>
        Snapshot().GetEnumerator();
}
