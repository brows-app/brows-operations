namespace Brows.Operations;

internal sealed class OperationRate {
    private bool _Completed;
    private double _ProgressRate;
    private double _ProgressRateAverage;
    private long _InitialProgress;
    private long _PreviousProgress;
    private TimeSpan _Elapsed;

    private static double ProgressDifference(long progress, long baseline) {
        return (double)((decimal)progress - baseline);
    }

    internal double ProgressRate => _ProgressRate;

    internal double ProgressRateAverage => _ProgressRateAverage;

    internal void Start(long progress, TimeSpan elapsed) {
        _InitialProgress = progress;
        _PreviousProgress = progress;
        _Elapsed = elapsed;
        _ProgressRate = 0;
        _ProgressRateAverage = 0;
        _Completed = false;
    }

    internal bool Sample(long progress, TimeSpan elapsed) {
        if (_Completed || elapsed <= _Elapsed) {
            return false;
        }
        var elapsedSeconds = elapsed.TotalSeconds;
        _ProgressRate = ProgressDifference(progress, _PreviousProgress) / (elapsed - _Elapsed).TotalSeconds;
        _ProgressRateAverage = ProgressDifference(progress, _InitialProgress) / elapsedSeconds;
        _PreviousProgress = progress;
        _Elapsed = elapsed;
        return true;
    }

    internal bool Complete(long progress, TimeSpan elapsed) {
        if (_Completed) {
            return false;
        }
        if (elapsed >= _Elapsed) {
            var elapsedSeconds = elapsed.TotalSeconds;
            _ProgressRateAverage = elapsedSeconds == 0
                ? 0
                : ProgressDifference(progress, _InitialProgress) / elapsedSeconds;
        }
        _ProgressRate = 0;
        _Elapsed = elapsed;
        _PreviousProgress = progress;
        _Completed = true;
        return true;
    }
}
