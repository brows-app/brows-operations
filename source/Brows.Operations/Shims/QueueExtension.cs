using System.Collections.Generic;

namespace Brows.Shims;

internal static class QueueExtension {
#if NETFRAMEWORK
    public static bool TryDequeue<T>(this Queue<T> queue, out T result) {
        if (queue is null) {
            throw new ArgumentNullException(nameof(queue));
        }
        if (queue.Count == 0) {
            result = default;
            return false;
        }
        result = queue.Dequeue();
        return true;
    }
#endif
}

