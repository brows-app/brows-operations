using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Brows.Operations;

internal sealed class OperationBaseCollection : IEnumerable<OperationBase> {
    private readonly ObservableCollection<OperationBase> Observable = [];

    public IEnumerable Source =>
        Observable;

    public void Add(OperationBase item) {
        item.SynchronizeCollectionChange(() => Observable.Add(item));
    }

    IEnumerator<OperationBase> IEnumerable<OperationBase>.GetEnumerator() {
        return ((IEnumerable<OperationBase>)Observable).GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() {
        return ((IEnumerable)Observable).GetEnumerator();
    }
}
