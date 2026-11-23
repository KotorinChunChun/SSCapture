using SSCapture.Services;

namespace SSCapture.Tests;

internal static partial class Program
{
    private static void OcrCopyTests()
    {
        Run("文字OCRは座標の有無にかかわらず改行・空行・空白・全行を保つ", () =>
        {
            string[] lines = ["  東 京  ", "", "資料  0012", "4行目", "5行目"];
            var recognition = new OcrResult(lines);
            var text = OcrCopyService.CreateCopyResult(recognition, asTable: false);
            Equal(OcrRows(lines), text.Text);
            Equal(OcrTableStatus.NotRequested, text.Table.Status);
            var table = OcrCopyService.CreateCopyResult(recognition, asTable: true);
            Equal(text.Text, table.Text);
            Equal(OcrTableStatus.PositionUnavailable, table.Table.Status);
            Check(table.Table.Tsv is null && table.Error is null, "座標なしでも文字を使える");
        });
        Run("表OCRは座標順に並べ、先頭・中間・末尾の空セルを保つ", () =>
        {
            var result = Table(
                OcrCell("40", 212, 37, 16), OcrCell("Name", 10, 10, 36), OcrCell("34", 112, 60, 16),
                OcrCell("Alpha", 11, 35, 39), OcrCell("Total", 200, 10, 28), OcrCell("Count", 100, 9, 28),
                OcrCell("Beta", 12, 85, 28), OcrCell("12", 112, 84, 16));
            Equal(OcrTableStatus.Ready, result.Status);
            Equal(OcrRows("Name\tCount\tTotal", "Alpha\t\t40", "\t34\t", "Beta\t12\t"), result.Tsv);
        });
        Run("表OCRは完全な行がない表でも繰り返す列を復元する", () =>
        {
            var result = Table(OcrCell("A", 5, 10), OcrCell("C", 180, 10), OcrCell("B", 90, 32), OcrCell("D", 180, 32),
                OcrCell("E", 5, 54), OcrCell("F", 90, 54));
            Equal(OcrTableStatus.Ready, result.Status);
            Equal(OcrRows("A\t\tC", "\tB\tD", "E\tF\t"), result.Tsv);
        });
        Run("表OCRはセル内の空白・句読点・数値表記を維持する", () =>
        {
            var result = Table(
                ("東京、  資料 0012", new OcrLineLayout(new(10, 10, 165, 24),
                    [new("東京", new(10, 10, 28, 24)), new("、", new(28, 10, 34, 24)),
                     new("資料", new(38, 10, 56, 24)), new("0012", new(128, 10, 165, 24))])),
                OcrCell("大阪", 10, 35, 20), OcrCell("1,250.00", 107, 35, 58));
            Equal(OcrTableStatus.Ready, result.Status);
            Equal(OcrRows("東京、  資料\t0012", "大阪\t1,250.00"), result.Tsv);
        });
        Run("表OCRは空の認識行を座標なしで扱える", () =>
        {
            var recognition = new OcrResult(["Name", "", "Count", "Alpha", "12"])
            {
                Layout = [OcrCell("Name", 5, 10).Layout, null, OcrCell("Count", 90, 10).Layout,
                    OcrCell("Alpha", 5, 35).Layout, OcrCell("12", 90, 35).Layout]
            };
            Equal(OcrRows("Name\tCount", "Alpha\t12"), OcrCopyService.ConvertTable(recognition).Tsv);
        });
        Run("表OCRは座標なし・一部欠損・不正座標を区別して案内する", () =>
        {
            var valid = OcrCell("Name", 5, 10);
            var invalid = valid.Layout! with { Words = [new("Name", new(float.NaN, 10, 20, 24))] };
            foreach (var recognition in new[] { new OcrResult(["Name"]),
                new OcrResult(["Name"]) { Layout = [] }, new OcrResult(["Name"]) { Layout = [null] },
                new OcrResult(["Name"]) { Layout = [valid.Layout! with { Words = null }] },
                new OcrResult(["Name"]) { Layout = [invalid] } })
            {
                var result = OcrCopyService.ConvertTable(recognition);
                Equal(OcrTableStatus.PositionUnavailable, result.Status);
                Check(result.Tsv is null, "座標欠損から表を作らない");
            }
        });
        Run("表OCRは段落・列をまたぐ見出し・単語欠落を低確信にする", () =>
        {
            var paragraph = Table(OcrCell("first sentence", 5, 10, 100), OcrCell("second sentence", 10, 32, 110));
            var merged = Table(OcrCell("Joined heading", 5, 10, 150), OcrCell("A", 5, 32), OcrCell("B", 90, 32),
                OcrCell("C", 5, 54), OcrCell("D", 90, 54));
            var missingWord = Table(("A? B", new OcrLineLayout(new(5, 10, 108, 24),
                [new("A", new(5, 10, 20, 24)), new("B", new(90, 10, 108, 24))])),
                OcrCell("C", 5, 32), OcrCell("D", 90, 32));
            foreach (var result in new[] { paragraph, merged, missingWord })
            {
                Equal(OcrTableStatus.LowConfidence, result.Status);
                Check(result.Tsv is null, "低確信から表を作らない");
            }
        });
        Run("OCRコピーは空・認識エラー・取消を区別する", () =>
        {
            Equal(OcrTableStatus.Empty, OcrCopyService.ConvertTable(new(["", " "])).Status);
            Equal(OcrTableStatus.RecognitionFailed, OcrCopyService.ConvertTable(new([], "OCRエラー")).Status);
            using var image = new Bitmap(2, 2);
            string missing = Path.Combine(Path.GetTempPath(), "missing-ocr-" + Guid.NewGuid());
            var result = OcrCopyService.RecognizeAsync(image, missing, CancellationToken.None).GetAwaiter().GetResult();
            Equal(OcrTableStatus.RecognitionFailed, result.Table.Status);
            Check(result.Error is not null && result.Text.Length == 0 && result.Table.Tsv is null, "認識エラーを返す");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            try
            {
                OcrCopyService.RecognizeAsync(image, missing, cancellation.Token).GetAwaiter().GetResult();
                throw new InvalidOperationException("取消が返らなかった");
            }
            catch (OperationCanceledException) { }
        });
    }

    private static OcrTableResult Table(params (string Text, OcrLineLayout? Layout)[] lines) =>
        OcrCopyService.ConvertTable(new(lines.Select(line => line.Text).ToArray()) { Layout = lines.Select(line => line.Layout).ToArray() });

    private static (string Text, OcrLineLayout? Layout) OcrCell(string text, int x, int y, int width = 18)
    {
        var bounds = new OcrBounds(x, y, x + width, y + 14);
        return (text, new(bounds, [new(text, bounds)]));
    }

    private static string OcrRows(params string[] lines) => string.Join(Environment.NewLine, lines);

    internal static int RunOcrCopySmoke(string configured = "")
    {
        using var image = new Bitmap(660, 240);
        using (var graphics = Graphics.FromImage(image))
        using (var font = new Font("Arial", 28, FontStyle.Regular, GraphicsUnit.Pixel))
        {
            graphics.Clear(Color.White);
            foreach (var (text, x, y) in new[] { ("Item", 20, 20), ("Count", 240, 20), ("Total", 460, 20),
                ("Alpha", 20, 80), ("12", 240, 80), ("40", 460, 80), ("Beta", 20, 140), ("80", 460, 140) })
                graphics.DrawString(text, font, Brushes.Black, x, y);
        }

        var result = OcrCopyService.RecognizeAsync(image, configured, CancellationToken.None).GetAwaiter().GetResult();
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result));
        if (result.Error is not null || !result.Text.Contains(Environment.NewLine, StringComparison.Ordinal)
            || result.Table.Status != OcrTableStatus.Ready
            || result.Table.Tsv != OcrRows("Item\tCount\tTotal", "Alpha\t12\t40", "Beta\t\t80")) return 1;

        var textOnly = OcrCopyService.RecognizeAsync(image, configured, CancellationToken.None, asTable: false).GetAwaiter().GetResult();
        return textOnly.Error is null && textOnly.Layout is null && textOnly.Text == result.Text
            && textOnly.Table.Status == OcrTableStatus.NotRequested ? 0 : 1;
    }
}
