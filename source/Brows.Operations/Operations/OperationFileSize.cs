using System;

namespace Brows.Operations;

internal static class OperationFileSize {
    private static readonly string[] Units = [
        "B",
        "KiB",
        "MiB",
        "GiB",
        "TiB",
        "PiB",
        "EiB"
    ];

    internal static string ToString(long fileLength, string format, IFormatProvider formatProvider) {
        string unit;
        double size;
        var abs = fileLength < 0
            ? (ulong)(-(fileLength + 1)) + 1
            : (ulong)fileLength;
        var frmt = format;
        switch (abs) {
            case >= 0x1000000000000000UL:
                unit = "EiB";
                size = (double)fileLength / 0x1000000000000000;
                frmt = frmt ?? "0.##";
                break;
            case >= 0x4000000000000UL:
                unit = "PiB";
                size = (double)fileLength / 0x4000000000000;
                frmt = frmt ?? "0.##";
                break;
            case >= 0x10000000000UL:
                unit = "TiB";
                size = (double)fileLength / 0x10000000000;
                frmt = frmt ?? "0.##";
                break;
            case >= 0x40000000UL:
                unit = "GiB";
                size = (double)fileLength / 0x40000000;
                frmt = frmt ?? "0.##";
                break;
            case >= 0x100000UL:
                unit = "MiB";
                size = (double)fileLength / 0x100000;
                frmt = frmt ?? "0.##";
                break;
            case >= 0x400UL:
                unit = "KiB";
                size = (double)fileLength / 0x400;
                frmt = frmt ?? "0.#";
                break;
            default:
                return fileLength.ToString("0 B", formatProvider);
        }
        return size.ToString(frmt, formatProvider) + " " + unit;
    }

    internal static string ToString(double fileLength, string format, IFormatProvider formatProvider) {
        var size = fileLength;
        var absoluteSize = Math.Abs(size);
        var unitIndex = 0;
        while (absoluteSize >= 1024 && unitIndex < Units.Length - 1) {
            size /= 1024;
            absoluteSize /= 1024;
            unitIndex++;
        }

        if (unitIndex == 0) {
            return fileLength.ToString("0 B", formatProvider);
        }

        var numericFormat = format ?? (unitIndex switch {
            1 => "0.#",
            _ => "0.##"
        });
        return size.ToString(numericFormat, formatProvider) + " " + Units[unitIndex];
    }
}
