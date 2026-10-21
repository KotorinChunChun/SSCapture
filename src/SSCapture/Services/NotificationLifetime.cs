namespace SSCapture.Services;

internal sealed class NotificationLifetime(long started, int duration, bool holdWhileApproaching = true)
{
    private long deadline = started + duration;
    private double? previousDistance;
    private bool held;
    internal bool Held => held;

    internal bool Expired(long now, Point pointer, Rectangle bounds, int dpi)
    {
        if (!holdWhileApproaching) return now >= deadline;
        int margin = (int)Math.Ceiling((held ? 48 : 32) * Math.Max(96, dpi) / 96d);
        // longで比較し、負座標や画面端のInflateオーバーフローを避ける。
        bool near = (long)pointer.X >= (long)bounds.X - margin && (long)pointer.X < (long)bounds.X + bounds.Width + margin &&
            (long)pointer.Y >= (long)bounds.Y - margin && (long)pointer.Y < (long)bounds.Y + bounds.Height + margin;
        double dx = Math.Max(Math.Max((double)bounds.Left - pointer.X, 0), (double)pointer.X - bounds.Right);
        double dy = Math.Max(Math.Max((double)bounds.Top - pointer.Y, 0), (double)pointer.Y - bounds.Bottom);
        double distance = Math.Sqrt(dx * dx + dy * dy);
        bool approaching = previousDistance is double previous && previous - distance >= Math.Max(1, dpi / 96d);
        previousDistance = distance;
        if (near || approaching)
        {
            deadline = now + duration;
            held = true;
            return false;
        }
        if (held) { held = false; deadline = now + duration; }
        return now >= deadline;
    }
}
