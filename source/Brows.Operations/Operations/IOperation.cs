namespace Brows.Operations;

/// <summary>Exposes the completion state of an operation and its descendants.</summary>
public interface IOperation {
    /// <summary>Gets whether the operation's delegate and all registered descendants have finished.</summary>
    /// <value><see langword="true"/> after work finishes, including work that failed or was canceled.</value>
    bool Complete { get; }

    /// <summary>Gets whether the completed operation or any of its descendants recorded an error.</summary>
    /// <value><see langword="true"/> if finalization found an error; otherwise, <see langword="false"/>.</value>
    /// <remarks>
    /// Inspect this value after <see cref="Complete"/> becomes <see langword="true"/>.
    /// Cancellation alone does not count as an error.
    /// </remarks>
    bool CompleteWithError { get; }
}
