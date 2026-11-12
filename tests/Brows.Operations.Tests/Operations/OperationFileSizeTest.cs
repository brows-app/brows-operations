using System.Globalization;

namespace Brows.Operations;

[TestFixture]
internal sealed class OperationFileSizeTest {
    [Test]
    public void ToString_PreservesPrecisionWhenRoundingMiB() {
        var formatted = OperationFileSize.ToString(1_294_992, "0.##", CultureInfo.InvariantCulture);

        Assert.That(formatted, Is.EqualTo("1.24 MiB"));
    }

    [TestCase(999.5, "N1", "1000 B")]
    [TestCase(1024, "N1", "1.0 KiB")]
    [TestCase(1_000_000, "N2", "976.56 KiB")]
    [TestCase(1_048_576, "N2", "1.00 MiB")]
    public void ToString_ScalesDoubleValuesLikeLongValues(double fileLength, string format, string expected) {
        var formatted = OperationFileSize.ToString(fileLength, format, CultureInfo.InvariantCulture);

        Assert.That(formatted, Is.EqualTo(expected));
    }

    [TestCase(long.MinValue, "-8 EiB")]
    [TestCase(long.MaxValue, "8 EiB")]
    public void ToString_FormatsSignedLongExtremes(long fileLength, string expected) {
        var formatted = OperationFileSize.ToString(fileLength, null, CultureInfo.InvariantCulture);

        Assert.That(formatted, Is.EqualTo(expected));
    }

    [Test]
    public void ToString_UsesFormatProviderForByteCounts() {
        var formatProvider = new NumberFormatInfo { NegativeSign = "~" };
        var formatted = OperationFileSize.ToString(-5, null, formatProvider);

        Assert.That(formatted, Is.EqualTo("~5 B"));
    }

    [TestCase(999, "999 B")]
    [TestCase(1023, "1023 B")]
    [TestCase(1024, "1 KiB")]
    [TestCase(2560, "2.5 KiB")]
    [TestCase(0x100000, "1 MiB")]
    [TestCase(0x40000000, "1 GiB")]
    [TestCase(0x10000000000, "1 TiB")]
    [TestCase(0x4000000000000, "1 PiB")]
    [TestCase(0x5000000000000, "1.25 PiB")]
    [TestCase(0x1000000000000000, "1 EiB")]
    [TestCase(-1024, "-1 KiB")]
    public void ToString_BoundaryValues(long fileLength, string expected) {
        var readable = OperationFileSize.ToString(fileLength, null, CultureInfo.InvariantCulture);
        Assert.That(readable, Is.EqualTo(expected));
    }
}
