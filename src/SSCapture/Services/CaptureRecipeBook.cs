namespace SSCapture.Services;

internal enum LastRangeReplay { SavedRectangle, TrackedTarget, Reselect }

// 完了順が逆転しても新しい撮影を優先する。画像や設定の複製はレシピ側が担う。
internal sealed class CaptureRecipeBook
{
    private readonly object gate = new();
    private long sequence;
    private LastCaptureRecipe? current;

    internal LastCaptureRecipe? Current
    {
        get { lock (gate) return current; }
    }

    internal LastRangeReplay LastRange
    {
        get
        {
            lock (gate)
            {
                if (current?.HasTrackedTarget == true) return LastRangeReplay.TrackedTarget;
                return sequence != 0 && current is null ? LastRangeReplay.Reselect : LastRangeReplay.SavedRectangle;
            }
        }
    }

    internal void Remember(long generation, LastCaptureRecipe? recipe)
    {
        lock (gate)
        {
            if (generation <= sequence) return;
            sequence = generation;
            current = recipe;
        }
    }
}
