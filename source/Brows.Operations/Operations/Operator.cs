namespace Brows.Operations;

internal sealed class Operator : IOperator {
    private readonly OperationCollection OperationCollection = new();

    public IOperationCollection Operations =>
        OperationCollection;

    public void Operate(string name, OperationDelegate task) {
        var
        manager = new OperationManager(OperationCollection);
        manager.Operate(name, task);
    }
}
