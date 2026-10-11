using System.Globalization;
using SSCapture.Services;

namespace SSCapture.Tests;

internal static partial class Program
{
    static partial void StatusTests()
    {
        var occurredAt = new DateTimeOffset(2026, 10, 10, 12, 34, 56, TimeSpan.FromHours(9));

        Run("結果表示は59.999秒で有効、60秒で期限切れ", () =>
        {
            var status = new StatusMessage("コピーしました。", false, occurredAt);
            Check(!status.IsExpired(occurredAt.AddMilliseconds(59_999)), "60秒未満で消えている");
            Check(status.IsExpired(occurredAt.AddSeconds(60)), "60秒ちょうどで残っている");
            Check(status.IsExpired(occurredAt.AddMilliseconds(60_001)), "60秒経過後に残っている");
        });

        Run("結果表示はローカル時刻と本文の改行を保持", () =>
        {
            const string message = "コピーしました。\r\n保存しました: C:\\保存先\\画像.png\n外部アプリへ送りました。";
            var status = new StatusMessage(message, true, occurredAt);
            Equal(occurredAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " " + message, status.DisplayText);
            Equal(message, status.Text);
            Check(status.IsError, "エラー種別が失われている");
        });

        Run("結果の再表示では発生日時と有効期限を更新しない", () =>
        {
            var status = new StatusMessage("保存しました。", false, occurredAt);
            string display = status.DisplayText;
            Check(!status.IsExpired(occurredAt.AddSeconds(59)), "期限内に消えている");
            Equal(display, status.DisplayText);
            Equal(occurredAt, status.OccurredAt);
            Check(status.IsExpired(occurredAt.AddMinutes(1)), "再表示で期限が延びている");
        });

        Run("新しい結果は自身の発生日時から60秒で期限切れ", () =>
        {
            var previous = new StatusMessage("コピーしました。", false, occurredAt);
            var current = new StatusMessage("保存しました。", false, occurredAt.AddSeconds(45));
            Check(previous.IsExpired(occurredAt.AddSeconds(60)), "古い結果の期限が延びている");
            Check(!current.IsExpired(occurredAt.AddSeconds(60)), "新しい結果が古い期限で消えている");
            Check(current.IsExpired(occurredAt.AddSeconds(105).ToOffset(TimeSpan.FromHours(-5))), "異なるUTCオフセットで期限がずれている");
        });
    }
}
