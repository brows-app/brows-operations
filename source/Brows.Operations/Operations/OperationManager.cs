using System;

namespace Brows.Operations;

internal sealed class OperationManager {
    private void Detach(Operation operation) {
        operation.Removed -= Operation_Removed;
        operation.RemovedFromCollection -= Operation_RemovedFromCollection;
    }

    private void Remove(Operation operation) {
        if (operation is null) {
            throw new ArgumentNullException(nameof(operation));
        }
        Operations.Remove(operation);
    }

    private void Operation_Removed(object sender, EventArgs e) {
        if (sender is Operation operation) {
            Remove(operation);
        }
    }

    private void Operation_RemovedFromCollection(object sender, EventArgs e) {
        if (sender is Operation operation) {
            Detach(operation);
        }
    }

    private void Operation_Completed(object sender, EventArgs e) {
        var operation = sender as Operation;
        if (operation is not null) {
            operation.Completed -= Operation_Completed;
            if (operation.Relevant == false) {
                Remove(operation);
            }
            if (operation.CompleteWithError == false) {
                if (RemoveCompletedOperations) {
                    Remove(operation);
                }
            }
        }
    }

    private Operation Operation(string name, OperationDelegate task) {
        var operation = new Operation(name, task, Operations.Synchronization);
        operation.Completed += Operation_Completed;
        operation.Removed += Operation_Removed;
        operation.RemovedFromCollection += Operation_RemovedFromCollection;
        return operation;
    }

    public bool RemoveCompletedOperations { get; set; } = true;

    public OperationCollection Operations { get; }

    public OperationManager(OperationCollection operations) {
        Operations = operations ?? throw new ArgumentNullException(nameof(operations));
    }

    public void Operate(string name, OperationDelegate task, Action<Operation> beforeStart = null) {
        var
        operation = Operation(name, task);
        Operations.Add(operation);
        beforeStart?.Invoke(operation);
        operation.Start();
    }
}
