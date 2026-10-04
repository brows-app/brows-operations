using Brows.Composition;
using System;
using System.Threading;

namespace Brows.Operations;

internal sealed class OperatorFactory : IOperatorFactory {
    IOperator IOperatorFactory.Create() {
        return new Operator();
    }

    IOperator IOperatorFactory.Create(SynchronizationContext synchronizationContext) {
        return new Operator(synchronizationContext ?? throw new ArgumentNullException(nameof(synchronizationContext)));
    }
}
