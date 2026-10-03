using System.Threading;

namespace Brows.Operations;

internal sealed class Operator : IOperator {
    private readonly OperationCollection OperationCollection;
    private readonly OperationManager OperationManager;

    internal Operator(SynchronizationContext synchronizationContext) {
        OperationCollection = new(new OperationContext(synchronizationContext));
        OperationManager = new(OperationCollection);
    }

    public IOperationCollection Operations =>
        OperationCollection;

    public Operator() : this(SynchronizationContext.Current) {
    }

    public void Operate(string name, OperationDelegate task) {
        OperationManager.Operate(name, task);
    }
}