using Domore.Notification;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;

namespace Brows.Operations;

internal sealed class OperationCollection : Notifier, IOperationCollection {
    private static readonly PropertyChangedEventArgs CountEvent = new(nameof(Count));
    private static readonly PropertyChangedEventArgs RelevantEvent = new(nameof(Relevant));
    private static readonly PropertyChangedEventArgs RelevanceEvent = new(nameof(Relevance));
    private static readonly PropertyChangedEventArgs[] RelevanceDependents = [RelevantEvent];

    private readonly List<Operation> Core = [];
    private readonly ObservableCollection<Operation> Observable = [];
    private readonly ReadOnlyObservableCollection<Operation> ObservableSource;
    private readonly OperationSynchronization OperationSync;
    private int Relevance;

    private void Item_RelevantChanged(object sender, EventArgs e) {
        if (sender is Operation item) {
            OperationSync.Update(() => {
                Relevance += item.Relevant ? 1 : -1;
                NotifyPropertyChanged(RelevanceEvent, RelevanceDependents);
            });
        }
    }

    private Operation[] SnapshotCore() =>
        OperationSync.Read(() => Core.ToArray());

    public bool Relevant =>
        OperationSync.Read(() => Relevance > 0);

    public int Count =>
        OperationSync.Read(() => Core.Count);

    public IEnumerable Source =>
        ObservableSource;

    public OperationSynchronization Synchronization =>
        OperationSync;

    public OperationCollection(OperationSynchronization synchronization = null) {
        OperationSync = synchronization ?? new OperationSynchronization(
            System.Threading.SynchronizationContext.Current);
        ObservableSource = new ReadOnlyObservableCollection<Operation>(Observable);
    }

    public void Add(Operation item) {
        if (item is null) throw new ArgumentNullException(nameof(item));
        var relevanceChanged = false;
        OperationSync.Update(() => {
            Core.Add(item);
            if (item.Relevant) {
                Relevance++;
                relevanceChanged = true;
            }
            item.RelevantChanged += Item_RelevantChanged;
        }, () => Observable.Add(item), () => {
            if (relevanceChanged) {
                NotifyPropertyChanged(RelevanceEvent, RelevanceDependents);
            }
            NotifyPropertyChanged(CountEvent);
        });
    }

    public bool Remove(Operation item) {
        if (item is null) {
            return false;
        }
        var removed = false;
        var relevanceChanged = false;
        OperationSync.Update(() => {
            if (!item.Complete) {
                return;
            }
            removed = Core.Remove(item);
            if (removed) {
                if (item.Relevant) {
                    Relevance--;
                    relevanceChanged = true;
                }
                item.RelevantChanged -= Item_RelevantChanged;
            }
        }, () => {
            if (removed) {
                Observable.Remove(item);
            }
        }, () => {
            if (removed) {
                if (relevanceChanged) {
                    NotifyPropertyChanged(RelevanceEvent, RelevanceDependents);
                }
                NotifyPropertyChanged(CountEvent);
            }
        });
        return removed;
    }

    IReadOnlyList<IOperation> IOperationCollection.Snapshot() =>
        SnapshotCore();

    bool IOperationCollection.Remove(IOperation item) =>
        Remove(item as Operation);

    int IOperationCollection.RemoveComplete(bool? withError) {
        Func<IOperation, bool> predicate = withError switch {
            true => i => i.Complete && i.CompleteWithError,
            false => i => i.Complete && !i.CompleteWithError,
            _ => i => i.Complete
        };
        var removed = 0;
        foreach (var item in SnapshotCore().Where(item => predicate(item))) {
            if (Remove(item)) {
                removed++;
            }
        }
        return removed;
    }
}
