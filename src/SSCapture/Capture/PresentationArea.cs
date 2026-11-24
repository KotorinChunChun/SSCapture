namespace SSCapture.Capture;

// 操作風景動画の録画用。環境変数で画面上の範囲を指定すると、ポインタ横の案内と拡大鏡をその範囲の内側に配置する。
// 録画は画面の一部だけを切り取るため、モニターの端までを使う通常の配置では、案内が録画範囲の外へはみ出す。
// 指定がない通常の利用では、モニター全体を使う。
internal static class PresentationArea
{
    internal const string EnvironmentVariable = "SSCAPTURE_PRESENTATION_AREA";

    internal static Rectangle? Current { get; } = Parse(Environment.GetEnvironmentVariable(EnvironmentVariable));

    // 「x,y,幅,高さ」（画面座標の整数）を読む。形式が違う・幅や高さが0以下のときは指定なしとして扱う。
    internal static Rectangle? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string[] parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 4) return null;
        var numbers = new int[4];
        for (int index = 0; index < 4; index++)
            if (!int.TryParse(parts[index], System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out numbers[index]))
                return null;
        return numbers[2] > 0 && numbers[3] > 0 ? new Rectangle(numbers[0], numbers[1], numbers[2], numbers[3]) : null;
    }

    // 撮影画面（モニターごと）のクライアント座標で、案内を置いてよい範囲を返す。
    // 指定がないとき、または指定範囲がこのモニターと重ならないときは、モニター全体を返す。
    internal static Rectangle Within(Rectangle client, Rectangle monitor, Rectangle? area)
    {
        if (area is not Rectangle screen) return client;
        screen.Offset(-monitor.X, -monitor.Y);
        Rectangle clipped = Rectangle.Intersect(screen, client);
        return clipped.Width > 0 && clipped.Height > 0 ? clipped : client;
    }
}
