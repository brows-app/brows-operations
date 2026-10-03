using Domore.Notification;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;

namespace Brows.Operations;

internal sealed class OperationCollection : Notifier, IOperationCollection {
    private static readonly PropertyChangedEventArgs CountEvent = new(nameof(Count));
    private static readonly PropertyChangedEventArgs RelevantEvent = new(nameof(Relevant));
    private static readonly PropertyChangedEventArgs RelevanceEvent = new(nameof(Relevance));
    private static readonly PropertyChangedEventArgs[] RelevanceDependents = [RelevantEvent];

    private readonly ObservableCollection<Operation> Collection = [];

    private int Relevance {
        get;
        set => Change(ref field, value, RelevanceEvent, RelevanceDependents);
    }

    private void Item_RelevantChanged(object sender, EventArgs e) {
        var item = sender as Operation;
        if (item != null) {
            if (item.Relevant) {
                Relevance++;
            }
            else {
                Relevance--;
            }
        }
    }

    private IReadOnlyList<IOperation> Snapshot() {
        lock (Collection) {
            return [.. Collection];
        }
    }

    private bool RemoveCore(Operation item) {
        if (item.Complete != true) {
            return false;
        }
        bool removed;
        lock (Collection) {
            removed = Collection.Remove(item);
        }
        if (removed) {
            if (item.Relevant) {
                Relevance--;
            }
            NotifyPropertyChanged(CountEvent);
        }
        item.RelevantChanged -= Item_RelevantChanged;
        return removed;
    }

    public OperationContext Context { get; }
    public bool Relevant => Relevance > 0;
    public int Count {
        get {
            lock (Collection) {
                return Collection.Count;
            }
        }
    }
    public IEnumerable Source => field ??= new ReadOnlyObservableCollection<Operation>(Collection);

    public OperationCollection() : this(new OperationContext(SynchronizationContext.Current)) {
    }

    public OperationCollection(OperationContext context) {
        Context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public void Add(Operation item) {
        if (item is null) throw new ArgumentNullException(nameof(item));
        if (item.Context != Context) {
            throw new ArgumentException(paramName: nameof(item), message: "Operation context mismatch.");
        }
        Context.Invoke(() => {
            if (item.Relevant) {
                Relevance++;
            }
            item.RelevantChanged += Item_RelevantChanged;
            lock (Collection) {
                Collection.Add(item);
            }
            NotifyPropertyChanged(CountEvent);
        });
    }

    public bool Remove(Operation item) {
        if (item is null) {
            return false;
        }
        return Context.Send(() => RemoveCore(item));
    }

    IReadOnlyList<IOperation> IOperationCollection.Snapshot() {
        return Snapshot();
    }

    bool IOperationCollection.Remove(IOperation item) {
        return Remove(item as Operation);
    }

    int IOperationCollection.RemoveComplete(bool? withError) {
        Func<IOperation, bool> predicate = withError switch {
            true => i => i.Complete && i.CompleteWithError,
            false => i => i.Complete && !i.CompleteWithError,
            _ => i => i.Complete
        };
        return Context.Send(() => {
            var itemsRemoved = 0;
            var itemsToRemove = Snapshot().Where(predicate).ToList();
            foreach (var item in itemsToRemove) {
                if (RemoveCore((Operation)item)) {
                    itemsRemoved++;
                }
            }
            return itemsRemoved;
        });
    }
}
