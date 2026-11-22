namespace SSCapture.Services;

// 完了順が逆転しても新しい撮影を優先する。画像や設定の複製はレシピ側が担う。
internal sealed class CaptureRecipeBook
{
    private readonly object gate = new();
    private long sequence;
    private LastCaptureRecipe? current;
    private SavedRegion? lastRegion;

    internal LastCaptureRecipe? Current
    {
        get { lock (gate) return current; }
    }

    // 入力プリセットへの追加用。直前がウィンドウ等でも、最後の矩形は追加できる。
    internal SavedRegion? LastRegion
    {
        get { lock (gate) return lastRegion; }
    }

    internal void Remember(long generation, LastCaptureRecipe? recipe)
    {
        lock (gate)
        {
            if (generation <= sequence) return;
            sequence = generation;
            current = recipe;
            lastRegion = recipe?.RectangularRegion ?? lastRegion;
        }
    }
}
