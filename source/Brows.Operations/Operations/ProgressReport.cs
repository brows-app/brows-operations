namespace Brows.Operations;

internal sealed class ProgressReport {
    private static long? Sum(long? first, long? second) =>
        first.HasValue || second.HasValue
            ? (first ?? 0) + (second ?? 0)
            : null;

    public long? AddProgress { get; }
    public long? SetProgress { get; }
    public long? AddTarget { get; }
    public long? SetTarget { get; }
    public string ProgressString { get; }
    public string TargetString { get; }
    public string Name { get; }
    public string Data { get; }

    public ProgressReport(long? addProgress,
                          long? setProgress,
                          long? addTarget,
                          long? setTarget,
                          string progressString,
                          string targetString,
                          string name,
                          string data) {
        AddProgress = addProgress;
        SetProgress = setProgress;
        AddTarget = addTarget;
        SetTarget = setTarget;
        ProgressString = progressString;
        TargetString = targetString;
        Name = name;
        Data = data;
    }

    public ProgressReport Merge(ProgressReport next) {
        if (next is null) {
            throw new ArgumentNullException(nameof(next));
        }
        /*
         * Each report applies its set value before its added value. A later set value therefore
         * replaces everything earlier, while later added values accumulate.
         */
        return new ProgressReport(
            addProgress: next.SetProgress.HasValue ? next.AddProgress : Sum(AddProgress, next.AddProgress),
            setProgress: next.SetProgress ?? SetProgress,
            addTarget: next.SetTarget.HasValue ? next.AddTarget : Sum(AddTarget, next.AddTarget),
            setTarget: next.SetTarget ?? SetTarget,
            progressString: next.ProgressString,
            targetString: next.TargetString,
            name: next.Name ?? Name,
            data: next.Data ?? Data);
    }
}
