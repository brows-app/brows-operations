using System;
using System.IO;
using System.Runtime.ExceptionServices;

namespace Brows.Operations;

[TestFixture]
internal sealed class OperationInfrastructureAggregateExceptionTest {
    [Test]
    public void Combine_ExpandsTypedAggregatesAndDeduplicatesInFirstSeenOrder() {
        var firstFailure = new IOException("first");
        var secondFailure = new IOException("second");
        var thirdFailure = new IOException("third");
        var first = ExceptionDispatchInfo.Capture(firstFailure);
        var second = ExceptionDispatchInfo.Capture(secondFailure);
        var third = ExceptionDispatchInfo.Capture(thirdFailure);
        var duplicateFirst = ExceptionDispatchInfo.Capture(firstFailure);
        var initial = OperationInfrastructureAggregateException.Combine(first, second);
        var remaining = OperationInfrastructureAggregateException.Combine(third, duplicateFirst);

        var observed = OperationInfrastructureAggregateException.Combine(initial, new[] { remaining });

        Assert.That(observed.SourceException, Is.InstanceOf<OperationInfrastructureAggregateException>());
        var aggregate = (OperationInfrastructureAggregateException)observed.SourceException;
        using (Assert.EnterMultipleScope()) {
            Assert.That(aggregate.Failures, Has.Count.EqualTo(3));
            Assert.That(aggregate.Failures[0], Is.SameAs(first));
            Assert.That(aggregate.Failures[1], Is.SameAs(second));
            Assert.That(aggregate.Failures[2], Is.SameAs(third));
            Assert.That(aggregate.InnerExceptions[0], Is.SameAs(firstFailure));
            Assert.That(aggregate.InnerExceptions[1], Is.SameAs(secondFailure));
            Assert.That(aggregate.InnerExceptions[2], Is.SameAs(thirdFailure));
        }
    }

    [Test]
    public void Combine_PreservesUserAggregateAsOneFailure() {
        var userAggregate = new AggregateException(
            "user aggregate",
            new IOException("user first"),
            new IOException("user second"));
        var callbackFailure = new IOException("callback");
        var userFailure = ExceptionDispatchInfo.Capture(userAggregate);
        var callback = ExceptionDispatchInfo.Capture(callbackFailure);

        var observed = OperationInfrastructureAggregateException.Combine(userFailure, new[] { callback });

        Assert.That(observed.SourceException, Is.InstanceOf<OperationInfrastructureAggregateException>());
        var aggregate = (OperationInfrastructureAggregateException)observed.SourceException;
        using (Assert.EnterMultipleScope()) {
            Assert.That(aggregate.Failures, Has.Count.EqualTo(2));
            Assert.That(aggregate.Failures[0], Is.SameAs(userFailure));
            Assert.That(aggregate.Failures[1], Is.SameAs(callback));
            Assert.That(aggregate.InnerExceptions[0], Is.SameAs(userAggregate));
            Assert.That(aggregate.InnerExceptions[1], Is.SameAs(callbackFailure));
        }
    }

    [Test]
    public void Combine_ReturnsOriginalDispatchInfoWhenOnlyOneDistinctFailureRemains() {
        var failure = new IOException("single failure");
        var original = ExceptionDispatchInfo.Capture(failure);
        var duplicate = ExceptionDispatchInfo.Capture(failure);

        var observed = OperationInfrastructureAggregateException.Combine(original, new[] { duplicate });

        Assert.That(observed, Is.SameAs(original));
        Assert.That(observed.SourceException, Is.SameAs(failure));
    }
}
