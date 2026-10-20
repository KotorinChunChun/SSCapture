namespace SSCapture.Services;

internal sealed class NotificationLifetime(long started, int duration)
{
    private long deadline = started + duration;
    private long remaining;
    private bool held;
    internal bool Held => held;

    internal bool Expired(long now, Point pointer, Rectangle bounds, int dpi)
    {
        int margin = (int)Math.Ceiling((held ? 48 : 32) * Math.Max(96, dpi) / 96d);
        // longで比較し、負座標や画面端のInflateオーバーフローを避ける。
        bool near = (long)pointer.X >= (long)bounds.X - margin && (long)pointer.X < (long)bounds.X + bounds.Width + margin &&
            (long)pointer.Y >= (long)bounds.Y - margin && (long)pointer.Y < (long)bounds.Y + bounds.Height + margin;
        if (near)
        {
            if (!held) remaining = Math.Max(0, deadline - now);
            held = true;
            return false;
        }
        if (held) { held = false; deadline = now + Math.Max(remaining, 1000); }
        return now >= deadline;
    }
}
