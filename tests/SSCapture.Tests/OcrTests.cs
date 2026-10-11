using SSCapture.Services;
using SSCapture.UI;
using SSCapture.Capture;

namespace SSCapture.Tests;
internal static partial class Program
{
    internal static Bitmap OcrFixture()
    {
        var image = new Bitmap(1200, 280);
        using var graphics = Graphics.FromImage(image);
        graphics.Clear(Color.White);
        using var font = new Font("Yu Gothic UI", 32F);
        graphics.DrawString("SSCapture PROJECT 2026", font, Brushes.Black, 30, 25);
        graphics.DrawString("東京 資料 第二章", font, Brushes.Black, 30, 115);
        return image;
    }

    internal static int RunOcrSmoke()
    {
        using var image = OcrFixture();
        OcrResult result = OneOcr.RecognizeAsync(image, "", CancellationToken.None).GetAwaiter().GetResult();
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result));
        string text = string.Join(" ", result.Lines);
        return result.Error is null && text.Contains("PROJECT", StringComparison.Ordinal) && text.Contains("東京", StringComparison.Ordinal) ? 0 : 1;
    }

    internal static int OcrPreview()
    {
        using var image = OcrFixture();
        using var form = new OcrNameForm(image, new AppSettings { FileNamePattern = "{yyyymmdd}_{hhmmss}_{ocr}.png" }, CaptureIntent.Screenshot,
            new DateTimeOffset(2026, 10, 10, 12, 34, 56, TimeSpan.FromHours(9)));
        Application.Run(form);
        Console.WriteLine(form.FileName ?? "CANCELED");
        return 0;
    }

    static partial void OcrTests()
    {
        Run("OCR未選択をボタンとCtrlEnterで確定してPNGとMP4を保存", () => WithServiceDirectory(directory =>
        {
            using var image = OcrFixture();
            var captured = new DateTimeOffset(2026, 10, 10, 12, 34, 56, TimeSpan.FromHours(9));
            var settings = new AppSettings { FileNamePattern = "{yyyymmdd}_{hhmmss}_{ocr}.png" };
            foreach (var intent in Enum.GetValues<CaptureIntent>())
            foreach (bool keyboard in new[] { false, true })
            {
                using var form = new OcrNameForm(image, settings, intent, captured);
                var candidates = (ListBox)form.Controls.Find("OcrCandidates", true).Single();
                candidates.Items.Add("未採用の認識行"); candidates.SelectedIndex = 0;
                string expected = "20261010_123456" + (intent == CaptureIntent.Screenshot ? ".png" : ".mp4");
                Equal(expected, form.Controls.Find("FileNamePreview", true).Single().Text);
                Check(form.Controls.Find("ConfirmOcrName", true).Single().Enabled, "OCR未選択でも確定可能");
                if (keyboard) DispatchOcrCommand(form, Keys.Control | Keys.Enter); else ClickOcrConfirmation(form);
                Equal(DialogResult.OK, form.DialogResult); Equal(expected, form.FileName);
                Equal("", form.NamePlan!.Ocr); Equal(settings.FileNamePattern, form.NamePlan.Pattern);
                string path;
                if (intent == CaptureIntent.Screenshot)
                    path = CaptureStorage.SavePng(image, directory, captured, namePlan: form.NamePlan);
                else
                {
                    string source = Path.Combine(directory, "source.mp4"); File.WriteAllBytes(source, [1, 2, 3]);
                    path = OutputRouter.CopyVideo(source, directory, namePlan: form.NamePlan);
                    Check(File.ReadAllBytes(path).SequenceEqual(new byte[] { 1, 2, 3 }), "動画コピーの内容を保持");
                }
                Equal(keyboard ? expected[..^4] + "_0001" + expected[^4..] : expected, Path.GetFileName(path));
                Check(File.Exists(path), "空OCRで確定したファイルが保存されない");
                Check(File.Exists(Path.Combine(directory, expected)), "衝突時に先に保存したファイルを失わない");
            }
        }));
        Run("OCRの全消去と半角空白だけの入力も末尾を省略して確定", () =>
        {
            using var image = OcrFixture();
            using var form = new OcrNameForm(image, new AppSettings { FileNamePattern = "{yyyymmdd}_{hhmmss}_{ocr}.png" },
                CaptureIntent.Screenshot, new DateTimeOffset(2026, 10, 10, 12, 34, 56, TimeSpan.FromHours(9)));
            var edits = Enumerable.Range(1, 3).Select(i => (TextBox)form.Controls.Find($"OcrText{i}", true).Single()).ToArray();
            foreach (var edit in edits) edit.Text = "採用";
            Equal("20261010_123456_採用_採用_採用.png", form.Controls.Find("FileNamePreview", true).Single().Text);
            foreach (var edit in edits) edit.Clear();
            Equal("20261010_123456.png", form.Controls.Find("FileNamePreview", true).Single().Text);
            foreach (var edit in edits) edit.Text = "   ";
            Equal("20261010_123456.png", form.Controls.Find("FileNamePreview", true).Single().Text);
            DispatchOcrCommand(form, Keys.Control | Keys.Enter);
            Equal(DialogResult.OK, form.DialogResult); Equal("20261010_123456.png", form.FileName);
        });
        Run("OCRのCtrlEnterと確定ボタンが同じ名前を一度だけ確定", () =>
        {
            using var image = OcrFixture();
            foreach (bool keyboard in new[] { true, false })
            {
                using var form = new OcrNameForm(image, new AppSettings { FileNamePattern = "{ocr}_{yyyymmdd}_{hhmmss}.png" },
                    CaptureIntent.Screenshot, new DateTimeOffset(2026, 10, 10, 12, 34, 56, TimeSpan.FromHours(9)));
                var edit = (TextBox)form.Controls.Find("OcrText1", true).Single();
                edit.Text = "東 京 資料";
                _ = form.Handle; _ = edit.Handle;
                form.ActiveControl = edit;
                Check(ReferenceEquals(edit, form.ActiveControl), "テキスト欄が入力対象");
                if (keyboard) Check(DispatchOcrCommand(form, Keys.Control | Keys.Enter), "CtrlEnterを処理");
                else ClickOcrConfirmation(form);
                Equal(DialogResult.OK, form.DialogResult);
                Equal("東京資料_20261010_123456.png", form.FileName);
                var plan = form.NamePlan;
                DispatchOcrCommand(form, Keys.Control | Keys.Enter);
                Check(ReferenceEquals(plan, form.NamePlan), "押しっぱなしや終了後のキーで二重確定しない");
            }
        });
        Run("OCR区切りや日時が不正な間はCtrlEnterもボタンも確定しない", () =>
        {
            using var image = OcrFixture();
            using var form = new OcrNameForm(image, new AppSettings { FileNamePattern = "{ocr}_{yyyymmdd}_{hhmmss}.png" },
                CaptureIntent.Screenshot, new DateTimeOffset(2026, 10, 10, 12, 34, 56, TimeSpan.FromHours(9)));
            var edit = (TextBox)form.Controls.Find("OcrText1", true).Single();
            var date = (TextBox)form.Controls.Find("OcrDate", true).Single();
            var time = (TextBox)form.Controls.Find("OcrTime", true).Single();
            var separator = (TextBox)form.Controls.Find("OcrSeparator", true).Single();
            foreach (Action invalidate in new Action[]
            {
                () => separator.Text = "/",
                () => { separator.Text = "_"; date.Text = "20250230"; },
                () => { date.Text = "20261010"; time.Text = "250000"; }
            })
            {
                invalidate();
                Check(!form.Controls.Find("ConfirmOcrName", true).Single().Enabled, "入力不備で確定を無効化");
                Check(DispatchOcrCommand(form, Keys.Control | Keys.Enter), "無効なCtrlEnterも消費する");
                ClickOcrConfirmation(form);
                Equal(DialogResult.None, form.DialogResult);
                Check(form.NamePlan is null && form.FileName is null, "入力不備の結果を生成しない");
            }
            time.Text = "123456";
            Check(DispatchOcrCommand(form, Keys.Control | Keys.Enter), "修正後はCtrlEnterで確定");
            Equal(DialogResult.OK, form.DialogResult);
        });
        Run("OCR確定ショートカットはCtrlEnterの完全一致だけ", () =>
        {
            using var image = OcrFixture();
            using var form = new OcrNameForm(image, new AppSettings { FileNamePattern = "{ocr}.png" },
                CaptureIntent.Screenshot, DateTimeOffset.Now);
            ((TextBox)form.Controls.Find("OcrText1", true).Single()).Text = "確定可能";
            foreach (Keys key in new[] { Keys.Enter, Keys.Shift | Keys.Enter, Keys.Alt | Keys.Enter,
                Keys.Control | Keys.Shift | Keys.Enter, Keys.Control | Keys.Alt | Keys.Enter })
            {
                DispatchOcrCommand(form, key);
                Equal(DialogResult.None, form.DialogResult);
                Check(form.FileName is null, "別キーやIME用Enterで確定しない");
            }
            Check(form.AcceptButton is null, "通常Enterの既存動作を変更しない");
        });
        Run("OCR画面のパターン・日時・保存後表示を撮影別に編集", () =>
        {
            using var image = OcrFixture();
            var settings = new AppSettings { FileNamePattern = "{yyyymmdd}_{hhmmss}_{ocr}.png", OpenFolderAfterSave = true };
            foreach (var intent in Enum.GetValues<CaptureIntent>())
            {
                using var form = new OcrNameForm(image, settings, intent, new DateTimeOffset(2026, 10, 10, 12, 34, 56, TimeSpan.FromHours(9)));
                var pattern = (TextBox)form.Controls.Find("OcrFileNamePattern", true).Single();
                var date = (TextBox)form.Controls.Find("OcrDate", true).Single();
                var time = (TextBox)form.Controls.Find("OcrTime", true).Single();
                var open = (CheckBox)form.Controls.Find("OcrOpenFolderAfterSave", true).Single();
                Check(open.Checked, "常用設定を初期値にする");
                ((TextBox)form.Controls.Find("OcrText1", true).Single()).Text = "東 京";
                pattern.Text = "資料_{ocr}_{yyyymmdd}_{hhmmss}.png"; date.Text = "20250228"; time.Text = "235901"; open.Checked = false;
                Equal("資料_東京_20250228_235901" + (intent == CaptureIntent.Screenshot ? ".png" : ".mp4"), form.Controls.Find("FileNamePreview", true).Single().Text);
                Equal(false, form.OutputSettings.OpenFolderAfterSave);
                Equal(pattern.Text, form.OutputSettings.FileNamePattern);
                Check(settings.OpenFolderAfterSave && settings.FileNamePattern == "{yyyymmdd}_{hhmmss}_{ocr}.png", "常用設定を変更しない");
                date.Text = "20250230";
                Check(!form.Controls.Find("ConfirmOcrName", true).Single().Enabled, "存在しない日を拒否");
                date.Text = "20240229"; time.Text = "240000";
                Check(!form.Controls.Find("ConfirmOcrName", true).Single().Enabled, "不正時刻を拒否");
                pattern.Text = "固定.png";
                ((TextBox)form.Controls.Find("OcrText1", true).Single()).Clear();
                Check(form.Controls.Find("ConfirmOcrName", true).Single().Enabled, "トークンを外せばOCRや日時は不要");
                pattern.Text = "{unknown}.png";
                Check(!form.Controls.Find("ConfirmOcrName", true).Single().Enabled, "未知変数を拒否");
            }
        });
        Run("動画OCRは固定OFFでも選択開始画像を切り出す", () =>
        {
            var bounds = new Rectangle(-5, -5, 12, 12);
            using var outcome = CaptureSession.SelectAsync(new CaptureOptions(CaptureMode.FullScreen, CaptureIntent.Recording,
                OutputDestination.File, false, true), new DesktopSnapshot(PatternBitmap(), bounds, [bounds]),
                currentFrameFactory: () => throw new InvalidOperationException("未来画像は取得しない")).GetAwaiter().GetResult();
            Check(outcome.Image is not null, "動画OCRの画像がない");
            Equal(bounds.Size, outcome.Image!.Size);
        });
        Run("OCRフォルダ不正は結果エラーとして返す", () =>
        {
            using var image = new Bitmap(2, 2);
            var result = OneOcr.RecognizeAsync(image, Path.Combine(Path.GetTempPath(), "missing-ocr-" + Guid.NewGuid()), CancellationToken.None).GetAwaiter().GetResult();
            Check(result.Error is not null && result.Lines.Length == 0, "失敗を通知する");
        });
        Run("OCR選択画面の主要領域・編集・空白除去・3枠上限", () =>
        {
            using var image = OcrFixture();
            var settings = new AppSettings { FileNamePattern = "{ocr}_{hhmmss}.png" };
            using var form = new OcrNameForm(image, settings, CaptureIntent.Recording,
                new DateTimeOffset(2026, 10, 10, 12, 34, 56, TimeSpan.FromHours(9)));
            _ = form.Handle;
            foreach (var child in Descendants(form)) _ = child.Handle;
            LayoutTree(form);
            var picture = form.Controls.Find("OcrImage", true).Single();
            var list = (ListBox)form.Controls.Find("OcrCandidates", true).Single();
            Check(picture.Width >= 480 && picture.Height >= 180, "画像プレビューが狭い");
            Check(list.Width >= 390 && list.Height >= 140, "候補が狭い");
            foreach (string name in new[] { "OcrText1", "OcrText2", "OcrText3", "OcrSeparator", "OcrFileNamePattern", "OcrDate", "OcrTime", "OcrOpenFolderAfterSave", "ConfirmOcrName", "CancelOcrName" })
            {
                var control = form.Controls.Find(name, true).Single();
                Check(control.Parent!.ClientRectangle.Contains(control.Bounds), name + " が親領域外");
            }
            var confirm = (Button)form.Controls.Find("ConfirmOcrName", true).Single();
            Check(confirm.Text.Contains("Ctrl+Enter", StringComparison.Ordinal), "ボタンに確定キーを表示");
            Check(confirm.Width >= 220, "確定ボタンにショートカット表示用の幅を確保");
            Size caption = TextRenderer.MeasureText(confirm.Text, confirm.Font);
            Check(caption.Width + 16 <= confirm.ClientSize.Width, "確定ボタンの文字が切れる");
            form.Size = form.MinimumSize;
            LayoutTree(form);
            Check(confirm.Parent!.ClientRectangle.Contains(confirm.Bounds), "最小サイズで確定ボタンが親領域外");
            var cancel = form.Controls.Find("CancelOcrName", true).Single();
            Check(cancel.Parent!.ClientRectangle.Contains(cancel.Bounds), "最小サイズでキャンセルボタンが親領域外");
            ((TextBox)form.Controls.Find("OcrText1", true).Single()).Text = "東 京";
            ((TextBox)form.Controls.Find("OcrText2", true).Single()).Text = "PROJECT 2026";
            ((TextBox)form.Controls.Find("OcrText3", true).Single()).Text = "第三章";
            Equal("東京_PROJECT2026_第三章_123456.mp4", form.Controls.Find("FileNamePreview", true).Single().Text);
            list.Items.Add("4行目"); list.SelectedIndex = 0;
            typeof(OcrNameForm).GetMethod("AddCandidate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(form, null);
            Equal("第三章", form.Controls.Find("OcrText3", true).Single().Text);
            ((TextBox)form.Controls.Find("OcrSeparator", true).Single()).Text = "/";
            Check(!form.Controls.Find("ConfirmOcrName", true).Single().Enabled, "不正区切りで確定可能");
            picture.Dock = DockStyle.None; picture.Size = new Size(100, 80); LayoutTree(form);
            Check(picture.Width < 480, "Dock断線を検出できない");
        });
    }

    private static bool DispatchOcrCommand(OcrNameForm form, Keys key)
    {
        var method = typeof(OcrNameForm).GetMethod("ProcessCmdKey", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        object[] arguments = [new Message(), key];
        return (bool)method.Invoke(form, arguments)!;
    }

    private static void ClickOcrConfirmation(OcrNameForm form)
    {
        var button = (Button)form.Controls.Find("ConfirmOcrName", true).Single();
        // Raise the same click handler without showing a native window or moving focus.
        typeof(Button).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(button, [EventArgs.Empty]);
    }
}
