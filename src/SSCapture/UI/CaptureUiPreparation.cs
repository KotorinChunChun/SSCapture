using SSCapture.Capture;

namespace SSCapture.UI;

internal static class CaptureUiPreparation
{
    internal static void Prepare(IEnumerable<Form?> windows, Action hideExtras, bool syncRequired = false,
        Action<Form>? disableTransitions = null, Action? synchronize = null)
    {
        var visible = windows.Where(f => f is { IsDisposed: false, Visible: true }).Cast<Form>().Distinct().ToArray();
        foreach (var window in visible) (disableTransitions ?? CaptureNative.DisableTransitions)(window);
        foreach (var window in visible) window.Hide();
        hideExtras();
        if (visible.Any(f => !f.IsDisposed && f.Visible)) throw new IOException("撮影前に自分の画面を非表示にできません。");
        if (syncRequired || visible.Length > 0) (synchronize ?? CaptureNative.SynchronizeDesktop)();
    }
}
