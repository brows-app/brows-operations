namespace Brows.Operations;

internal sealed class OperatorFactory : IOperatorFactory {
    IOperator IOperatorFactory.Create() {
        return new Operator();
    }
}
