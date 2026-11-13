using System;
using System.Collections.Generic;

namespace Brows.Operations;

[TestFixture]
internal sealed class OperationBatchResultTest {
    [Test]
    public void Constructor_NullResults_ThrowsArgumentNullException() {
        Assert.That(() => new OperationBatchResult(null), Throws.ArgumentNullException);
    }

    [Test]
    public void Constructor_NullEntry_ThrowsArgumentException() {
        var results = new OperationResult[] { null };

        Assert.That(() => new OperationBatchResult(results), Throws.ArgumentException);
    }

    [Test]
    public void Constructor_UndefinedOutcome_ThrowsArgumentException() {
        var results = new[] { new OperationResult((OperationResultKind)123, null) };

        Assert.That(() => new OperationBatchResult(results), Throws.ArgumentException);
    }

    [Test]
    public void Constructor_EmptyResults_HasZeroCounts() {
        var result = new OperationBatchResult(Array.Empty<OperationResult>());

        using (Assert.EnterMultipleScope()) {
            Assert.That(result.Results, Is.Empty);
            Assert.That(result.SucceededCount, Is.Zero);
            Assert.That(result.FailedCount, Is.Zero);
            Assert.That(result.CanceledCount, Is.Zero);
        }
    }

    [Test]
    public void Constructor_CopiesResultsAndCalculatesCountsInSourceOrder() {
        var expected = new[] {
            new OperationResult(OperationResultKind.Succeeded, null),
            new OperationResult(OperationResultKind.Failed, new InvalidOperationException("failed one")),
            new OperationResult(OperationResultKind.Failed, new InvalidOperationException("failed two")),
            new OperationResult(OperationResultKind.Canceled, new OperationCanceledException()),
            new OperationResult(OperationResultKind.Succeeded, null),
            new OperationResult(OperationResultKind.Failed, new InvalidOperationException("failed three"))
        };
        var source = new List<OperationResult>(expected);
        var result = new OperationBatchResult(source);
        source.Clear();

        using (Assert.EnterMultipleScope()) {
            Assert.That(result.Results, Is.EqualTo(expected));
            Assert.That(result.SucceededCount, Is.EqualTo(2));
            Assert.That(result.FailedCount, Is.EqualTo(3));
            Assert.That(result.CanceledCount, Is.EqualTo(1));
        }
    }

    [Test]
    public void Results_ReadOnlyWrapper_RejectsMutation() {
        var result = new OperationBatchResult(new[] {
            new OperationResult(OperationResultKind.Succeeded, null)
        });
        var results = (IList<OperationResult>)result.Results;

        Assert.That(() => results.Add(new OperationResult(OperationResultKind.Failed, new Exception())),
                    Throws.InstanceOf<NotSupportedException>());
        Assert.That(result.Results.Count, Is.EqualTo(1));
    }
}
