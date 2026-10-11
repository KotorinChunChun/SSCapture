using SSCapture.Services;

namespace SSCapture.Tests;
internal static partial class Program
{
    static partial void NamingTests()
    {
        Run("空OCRは拡張子直前のアンダースコアと変数だけ省略", () =>
        {
            var time = new DateTimeOffset(2026, 10, 10, 12, 34, 56, TimeSpan.FromHours(9));
            foreach (string suffix in new[] { ".png", ".PNG", ".mp4", ".MP4", "" })
            foreach (var intent in Enum.GetValues<CaptureIntent>())
            {
                string extension = intent == CaptureIntent.Screenshot ? ".png" : ".mp4";
                Equal("20261010_123456" + extension, FileNamePattern.Expand("{yyyymmdd}_{hhmmss}_{ocr}" + suffix, time, intent));
                Equal("123456_01" + extension, new FileNamePlan("{hhmmss}_{##}_{ocr}" + suffix, time, intent).Candidate(0));
                Equal("123456_02" + extension, new FileNamePlan("{hhmmss}_{##}_{ocr}" + suffix, time, intent).Candidate(1));
            }
        });
        Run("空OCRの省略でもリテラルのアンダースコアや他の位置は保持", () =>
        {
            var time = DateTimeOffset.Now;
            foreach (var (pattern, expected) in new[] {
                ("資料__{ocr}.png", "資料_.png"), ("資料_.png", "資料_.png"),
                ("{ocr}_資料.png", "_資料.png"), ("資料_{ocr}_続き.png", "資料__続き.png"),
                ("{region}_{ocr}.png", "範囲_.png") })
                Equal(expected, FileNamePattern.Expand(pattern, time, CaptureIntent.Screenshot, region: "範囲_"));
            Equal("資料_採用_.png", FileNamePattern.Expand("資料_{ocr}.png", time, CaptureIntent.Screenshot, "採用_"));
            Equal("資料_原文_{ocr}.png", FileNamePattern.Expand("資料_{ocr}.png", time, CaptureIntent.Screenshot, "原文_{ocr}"));
            Equal("資料_0001.png", new FileNamePlan("資料_{ocr}.png", time, CaptureIntent.Screenshot).Candidate(1));
        });
        Run("連番の桁数・桁あふれ・OCRリテラルを保持", () =>
        {
            var time = new DateTimeOffset(2026, 10, 10, 12, 34, 56, TimeSpan.FromHours(9));
            var plan = new FileNamePlan("{yyyymmdd}_{hhmmss}_{#}_{##}_{ocr}.png", time, CaptureIntent.Screenshot, "原文{##}");
            Equal("20261010_123456_1_01_原文{##}.png", plan.Candidate(0));
            Equal("20261010_123456_100_100_原文{##}.png", plan.Candidate(99));
            Equal("原文{##}_0001.png", new FileNamePlan("{ocr}.png", time, CaptureIntent.Screenshot, "原文{##}").Candidate(1));
        });
        Run("同時PNG保存はパターン内で採番し別の秒では1から", () => WithServiceDirectory(directory =>
        {
            var time = new DateTimeOffset(2026, 10, 10, 12, 34, 56, TimeSpan.FromHours(9));
            var plan = new FileNamePlan("{hhmmss}_{##}.png", time, CaptureIntent.Screenshot);
            var paths = new System.Collections.Concurrent.ConcurrentBag<string>();
            Parallel.For(0, 12, _ =>
            {
                using var bitmap = new Bitmap(4, 4);
                paths.Add(CaptureStorage.SavePng(bitmap, directory, time, namePlan: plan));
            });
            Equal(12, paths.Distinct().Count());
            foreach (int n in Enumerable.Range(1, 12)) Check(File.Exists(Path.Combine(directory, $"123456_{n:D2}.png")), "番号が欠落");
            using var image = new Bitmap(4, 4);
            Equal("123457_01.png", Path.GetFileName(CaptureStorage.SavePng(image, directory, time, namePlan: plan with { CapturedAt = time.AddSeconds(1) })));
        }));
        Run("MP4も指定位置で連番を増やし元動画を保持", () => WithServiceDirectory(directory =>
        {
            string source = Path.Combine(directory, "source.mp4"); File.WriteAllBytes(source, [1, 2, 3]);
            var plan = new FileNamePlan("録画_{#}.png", DateTimeOffset.Now, CaptureIntent.Recording);
            Equal("録画_1.mp4", Path.GetFileName(OutputRouter.CopyVideo(source, directory, namePlan: plan)));
            Equal("録画_2.mp4", Path.GetFileName(OutputRouter.CopyVideo(source, directory, namePlan: plan)));
            Check(File.ReadAllBytes(source).SequenceEqual(new byte[] { 1, 2, 3 }), "元動画変更");
        }));
        Run("共通命名パターンは固定日時でPNG/MP4を切り替える", () =>
        {
            var time = new DateTimeOffset(2026, 10, 10, 23, 4, 5, TimeSpan.FromHours(9));
            Equal("20261010_230405.png", FileNamePattern.Expand(FileNamePattern.Default, time, CaptureIntent.Screenshot));
            Equal("20261010_230405.mp4", FileNamePattern.Expand(FileNamePattern.Default, time, CaptureIntent.Recording));
            Equal("レポート_A{hhmmss}.png", FileNamePattern.Expand("レポート_{ocr}.mp4", time, CaptureIntent.Screenshot, "A{hhmmss}"));
            Equal("固定.png", FileNamePattern.Expand("固定", time, CaptureIntent.Screenshot));
        });
        Run("不正名・未知変数・パス・予約名を拒否", () =>
        {
            foreach (string pattern in new[] { "", "../a.png", "C:\\a.png", "CON.png", "com1.png", "A?.png", "{unknown}.png", "{ocr.png", "a.png ", new string('a', 241) })
            {
                bool failed = false;
                try { FileNamePattern.Validate(pattern, "_"); } catch (ArgumentException) { failed = true; }
                Check(failed, "拒否されない: " + pattern);
            }
        });
        Run("OCRは選択順・3行制限・半角空白除去・区切り指定", () =>
        {
            Equal("東京資料-第2章-Ａ　Ｂ", FileNamePattern.JoinOcr(["東京 資料", "第 2 章", "Ａ　Ｂ"], "-"));
            Equal("a_b", FileNamePattern.JoinOcr(["a:/?", "  ", "b"], "_"));
            bool failed = false;
            try { FileNamePattern.JoinOcr(["1", "2", "3", "4"], "_"); } catch (ArgumentException) { failed = true; }
            Check(failed, "4行は拒否");
            failed = false;
            try { FileNamePattern.ValidateSeparator("/"); } catch (ArgumentException) { failed = true; }
            Check(failed, "パス区切りを拒否");
        });
        Run("指定名PNGは同名を上書きせず保存", () => WithServiceDirectory(directory =>
        {
            using var bitmap = new Bitmap(4, 4);
            string first = CaptureStorage.SavePng(bitmap, directory, DateTimeOffset.Now, "文字列.png");
            byte[] bytes = File.ReadAllBytes(first);
            string second = CaptureStorage.SavePng(bitmap, directory, DateTimeOffset.Now, "文字列.png");
            Equal("文字列_0001.png", Path.GetFileName(second));
            Check(bytes.SequenceEqual(File.ReadAllBytes(first)), "既存データ保持");
        }));
        Run("旧設定は新しい命名既定値を受け取りカスタム値を保存", () => WithServiceDirectory(directory =>
        {
            string path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, "{\"SchemaVersion\":2,\"DefaultDestination\":\"Clipboard\"}");
            var store = new SettingsStore(path);
            var settings = store.Load(out var warning);
            Equal<string?>(null, warning); Equal(FileNamePattern.Default, settings.FileNamePattern);
            store.Save(settings with { FileNamePattern = "{ocr}_{hhmmss}.png", OcrSeparator = "-" });
            Equal("{ocr}_{hhmmss}.png", store.Load(out warning).FileNamePattern);
        }));
    }
}
