namespace SSCapture.Services;

// Waiting is separate from delivery: reaching the deadline never cancels output.
internal static class DeliveryWait
{
    internal static async Task<bool> CompletedWithinAsync(Task delivery, TimeSpan timeout, Task? interactionFinished = null)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        // The image and busy state must remain owned by the caller until naming
        // finishes. A delivery failure before naming must also release this wait.
        if (interactionFinished is not null) await Task.WhenAny(delivery, interactionFinished);
        try
        {
            await delivery.WaitAsync(timeout);
            return true;
        }
        catch (TimeoutException)
        {
            if (!delivery.IsCompleted) return false;
            // Preserve an output operation's own TimeoutException, and resolve
            // a completion racing the deadline from the original task's result.
            await delivery;
            return true;
        }
    }
}
