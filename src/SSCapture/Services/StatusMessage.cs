using System.Globalization;

namespace SSCapture.Services;

internal sealed record StatusMessage(string Text, bool IsError, DateTimeOffset OccurredAt)
{
    public bool IsExpired(DateTimeOffset now) => now - OccurredAt >= TimeSpan.FromMinutes(1);

    public string DisplayText => OccurredAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " " + Text;
}
