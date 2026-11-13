namespace Brows.Operations;

/// <summary>
/// Describes the outcome of a child operation and its registered descendants.
/// </summary>
/// <param name="Kind">
/// The terminal work outcome.
/// </param>
/// <param name="Error">
/// The original failure or distinct-failure aggregate, or the original cancellation exception when one is
/// available. If cancellation has no original exception, the package supplies an
/// <see cref="OperationCanceledException"/> carrying the canceled operation token. Package-produced successful
/// results have a null error; failed and canceled results have a non-null error.
/// </param>
public sealed record OperationResult(OperationResultKind Kind, Exception Error);
