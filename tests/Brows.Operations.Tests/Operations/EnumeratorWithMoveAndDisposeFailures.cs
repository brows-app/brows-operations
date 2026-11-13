using System;
using System.Collections;
using System.Collections.Generic;

namespace Brows.Operations;

internal sealed class EnumeratorWithMoveAndDisposeFailures : IEnumerable<int>, IEnumerator<int> {
    private readonly Exception MoveFailure;
    private readonly Exception DisposeFailure;
    private int State;

    public int Current => 1;

    public EnumeratorWithMoveAndDisposeFailures(Exception moveFailure, Exception disposeFailure) {
        MoveFailure = moveFailure;
        DisposeFailure = disposeFailure;
    }

    public IEnumerator<int> GetEnumerator() =>
        this;

    public bool MoveNext() {
        if (State++ == 0) {
            return true;
        }
        throw MoveFailure;
    }

    public void Reset() =>
        throw new NotSupportedException();

    public void Dispose() =>
        throw DisposeFailure;

    IEnumerator IEnumerable.GetEnumerator() =>
        GetEnumerator();

    object IEnumerator.Current =>
        Current;
}
