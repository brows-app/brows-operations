namespace Brows.Operations;

[TestFixture]
internal sealed class OperationRateTest {
    [Test]
    public void Sample_CalculatesIntervalAndRunningAverageRates() {
        var rate = new OperationRate();
        rate.Start(0, TimeSpan.Zero);

        using (Assert.EnterMultipleScope()) {
            Assert.That(rate.Sample(0, TimeSpan.Zero), Is.False);
            AssertRates(rate, 0, 0);
        }
        rate.Sample(10, TimeSpan.FromSeconds(1));
        AssertRates(rate, 10, 10);
        rate.Sample(50, TimeSpan.FromSeconds(3));
        AssertRates(rate, 20, 50d / 3);
        rate.Sample(50, TimeSpan.FromSeconds(4));
        AssertRates(rate, 0, 12.5);
        rate.Sample(60, TimeSpan.FromSeconds(5));
        AssertRates(rate, 10, 12);
    }

    [Test]
    public void Sample_CalculatesFractionalRatesAndNetCorrections() {
        var rate = new OperationRate();
        rate.Start(0, TimeSpan.Zero);
        rate.Sample(1, TimeSpan.FromSeconds(2));
        rate.Sample(-2, TimeSpan.FromSeconds(4));

        AssertRates(rate, -1.5, -0.5);
    }

    [Test]
    public void Sample_UsesInitialProgressAsBaseline() {
        var rate = new OperationRate();
        rate.Start(5, TimeSpan.Zero);
        rate.Sample(9, TimeSpan.FromSeconds(2));

        AssertRates(rate, 2, 2);
    }

    [TestCase(9_007_199_254_740_992L, 9_007_199_254_740_993L, 1)]
    [TestCase(9_007_199_254_740_994L, 9_007_199_254_740_993L, -1)]
    public void SampleAndComplete_PreserveSmallSignedDeltasAboveDoublePrecision(
        long initialProgress,
        long sampledProgress,
        double expectedRate) {
        var rate = new OperationRate();
        rate.Start(initialProgress, TimeSpan.Zero);

        rate.Sample(sampledProgress, TimeSpan.FromSeconds(1));
        AssertRates(rate, expectedRate, expectedRate);

        rate.Complete(sampledProgress, TimeSpan.FromSeconds(2));
        AssertRates(rate, 0, expectedRate / 2);
    }

    [Test]
    public void Sample_IgnoresRepeatedAndEarlierTimestampsWithoutConsumingProgress() {
        var rate = new OperationRate();
        rate.Start(0, TimeSpan.Zero);
        rate.Sample(10, TimeSpan.FromSeconds(1));

        Assert.That(rate.Sample(25, TimeSpan.FromSeconds(1)), Is.False);
        Assert.That(rate.Sample(25, TimeSpan.FromMilliseconds(500)), Is.False);
        AssertRates(rate, 10, 10);

        rate.Sample(30, TimeSpan.FromSeconds(2));
        AssertRates(rate, 20, 15);
    }

    [Test]
    public void Sample_HandlesLongExtremesWithoutIntegerSubtractionOverflow() {
        var rate = new OperationRate();
        rate.Start(long.MinValue, TimeSpan.Zero);
        rate.Sample(long.MaxValue, TimeSpan.FromSeconds(2));

        var expectedRate = ((double)long.MaxValue - long.MinValue) / 2;
        AssertRates(rate, expectedRate, expectedRate);
    }

    [Test]
    public void Complete_SetsCurrentRateToZeroAndFreezesFinalAverage() {
        var rate = new OperationRate();
        rate.Start(0, TimeSpan.Zero);
        rate.Sample(10, TimeSpan.FromSeconds(1));

        Assert.That(rate.Complete(20, TimeSpan.FromSeconds(1)), Is.True);
        AssertRates(rate, 0, 20);
        Assert.That(rate.Complete(30, TimeSpan.FromSeconds(3)), Is.False);
        Assert.That(rate.Sample(30, TimeSpan.FromSeconds(3)), Is.False);
        AssertRates(rate, 0, 20);
    }

    [Test]
    public void Complete_BeforeFirstTickAndAtZeroDurationReturnsFiniteZeroRates() {
        var rate = new OperationRate();
        rate.Start(0, TimeSpan.Zero);

        Assert.That(rate.Complete(10, TimeSpan.Zero), Is.True);
        AssertRates(rate, 0, 0);
        Assert.That(double.IsNaN(rate.ProgressRateAverage) || double.IsInfinity(rate.ProgressRateAverage), Is.False);
    }

    private static void AssertRates(OperationRate rate, double progressRate, double progressRateAverage) {
        Assert.That(rate.ProgressRate, Is.EqualTo(progressRate).Within(0.000001));
        Assert.That(rate.ProgressRateAverage, Is.EqualTo(progressRateAverage).Within(0.000001));
    }
}
