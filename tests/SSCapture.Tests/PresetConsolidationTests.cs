using SSCapture.Capture;
using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture.Tests;
internal static partial class Program
{
    private static void PresetConsolidationTests()
    {
        Run("範囲選択の共通入口は保存済み精度と表示設定を渡す", () =>
        {
            var settings = new AppSettings { SnapDistancePixels = 5, GridStepPixels = 7, Grid20 = true, ShowSelectionGuide = false, LimitToStartingMonitor = false };
            foreach (Size? size in new Size?[] { null, new(80, 60) })
            {
                var options = CaptureOptions.ForInputSelection(settings, size, CaptureAnchor.BottomRight);
                Equal(size is null ? CaptureMode.Rectangle : CaptureMode.FixedRegion, options.Mode);
                Equal(CaptureIntent.Recording, options.Intent); Equal(OutputDestination.None, options.Destination);
                Equal(size, options.FixedSize); Equal(CaptureAnchor.BottomRight, options.FixedAnchor);
                Check(options.Grid20 && !options.ShowSelectionGuide && !options.LimitToStartingMonitor, "表示設定が渡らない");
                var rect = new Rectangle(0, 0, 200, 200);
                var session = new SessionController(options, new DesktopSnapshot(new Bitmap(200, 200), rect, [rect]), []);
                try { Equal(5, session.OutputSettings.SnapDistancePixels); Equal(7, session.OutputSettings.GridStepPixels); } finally { session.Cancel(); }
            }
        });
        Run("位置未指定の範囲確認は無効・座標指定で有効に戻る", () =>
        {
            using var form = new SettingsForm(new AppSettings(), _ => null, () => { }, () => []);
            var check = form.Controls.Find("PreviewRegion", true).Single();
            Check(!check.Enabled, "位置未指定なのに確認できる");
            var coordinates = (CheckBox)form.Controls.Find("InputFixedCoordinates", true).Single();
            coordinates.Checked = true; Check(check.Enabled, "座標指定後も確認できない");
            coordinates.Checked = false; Check(!check.Enabled, "無効状態へ戻らない");
            using var tips = new ToolTip(); SettingsHelp.Apply(form, tips);
            Check((tips.GetToolTip(check) ?? "").Contains("位置が未指定"), "無効理由の説明がない");
        });
        Run("既定入力の概要リンクは編集中の名前と余白を再読込しない", () =>
        {
            using var form = new SettingsForm(new AppSettings(), _ => null, () => { }, () => []);
            form.Controls.Find("RegionName", true).Single().Text = "編集中の既定";
            ((NumericUpDown)form.Controls.Find("ScreenshotPaddingPixels", true).Single()).Value = 23;
            ConsolidationClick(form, "DefaultInputSummary");
            Equal("編集中の既定", form.Controls.Find("RegionName", true).Single().Text);
            Equal(23m, ((NumericUpDown)form.Controls.Find("ScreenshotPaddingPixels", true).Single()).Value);
        });
        Run("別の入力から既定への概要移動は編集を画面内一覧へ保持し永続保存しない", () =>
        {
            int saves = 0; using var form = new SettingsForm(new AppSettings(), _ => { saves++; return null; }, () => { }, () => []);
            var editor = (RegionManagerForm)form.Controls.Find("RegionManagerForm", true).Single();
            var list = (ListBox)form.Controls.Find("SavedRegions", true).Single(); list.SelectedIndex = 1;
            string id = ((SavedRegion)list.SelectedItem!).Id;
            form.Controls.Find("RegionName", true).Single().Text = "別入力の編集";
            ConsolidationClick(form, "DefaultInputSummary");
            Equal("別入力の編集", editor.Regions.Single(p => p.Id == id).Name);
            Check(((SavedRegion)list.SelectedItem!).IsDefault, "既定へ移動しない"); Equal(0, saves);
        });
        Run("出力概要への移動は既定の未保存編集と別出力の編集を保持", () =>
        {
            int saves = 0; using var form = new SettingsForm(new AppSettings(), _ => { saves++; return null; }, () => { }, () => []);
            var editor = (CapturePresetEditor)form.Controls.Find("CapturePresetEditor", true).Single();
            var list = (ListBox)form.Controls.Find("CapturePresetList", true).Single();
            form.Controls.Find("CapturePresetName", true).Single().Text = "既定を編集中";
            ConsolidationClick(form, "DefaultOutputSummary"); Equal("既定を編集中", form.Controls.Find("CapturePresetName", true).Single().Text);
            list.SelectedIndex = 1; string id = ((CapturePreset)list.SelectedItem!).Id;
            form.Controls.Find("CapturePresetName", true).Single().Text = "別出力の編集";
            ConsolidationClick(form, "DefaultOutputSummary");
            Equal("別出力の編集", editor.Presets.Single(p => p.Id == id).Name); Check(((CapturePreset)list.SelectedItem!).IsDefault, "既定に戻らない"); Equal(0, saves);
        });
        Run("不正な編集中の値がある概要移動は拒否して入力を残す", () =>
        {
            using var form = new SettingsForm(new AppSettings(), _ => null, () => { }, () => []);
            var list = (ListBox)form.Controls.Find("SavedRegions", true).Single(); list.SelectedIndex = 1;
            form.Controls.Find("RegionName", true).Single().Text = "";
            ConsolidationClick(form, "DefaultInputSummary"); Equal(1, list.SelectedIndex); Equal("", form.Controls.Find("RegionName", true).Single().Text);
            Check(form.Controls.Find("Status", true).Single().Text.Contains("切り替えられません"), "拒否理由がない");
            var outputList = (ListBox)form.Controls.Find("CapturePresetList", true).Single(); outputList.SelectedIndex = 1;
            form.Controls.Find("CapturePresetName", true).Single().Text = "";
            ConsolidationClick(form, "DefaultOutputSummary"); Equal(1, outputList.SelectedIndex); Equal("", form.Controls.Find("CapturePresetName", true).Single().Text);
        });
        Run("概要は日本語で無関係な固定寸法・保存先と旧余白説明を表示しない", () =>
        {
            var settings = new AppSettings().ApplyDefaults();
            string input = SettingsForm.InputSummary(settings.SavedRegions.Single(p => p.IsDefault));
            Check(!input.Contains("1280") && !input.Contains("True") && !input.Contains("False"), "矩形へ固定寸法か生の真偽値が出た");
            string output = SettingsForm.OutputSummary(settings.CapturePresets.Single(p => p.IsDefault));
            Check(output.Contains("クリップボード") && output.Contains("原色") && !output.Contains("Original") && !output.Contains(settings.SaveDirectory), "コピーだけに未使用保存先が出た");
            using var form = new SettingsForm(settings, _ => null, () => { }, () => []); using var tips = new ToolTip(); SettingsHelp.Apply(form, tips);
            var help = tips.GetToolTip(form.Controls.Find("CapturePresetEditor", true).Single()) ?? "";
            Check(help.Contains("白い余白は入力プリセット") && !help.Contains("登録・更新"), "旧説明のまま");
        });
        Run("複製と既定ボタンは最小画面でも36px以上で文字と操作領域が収まる", () =>
        {
            using var form = new SettingsForm(new AppSettings(), _ => null, () => { }, () => []);
            form.SelectCategory("入力プリセット"); form.Size = form.MinimumSize; _ = form.Handle;
            foreach (Control child in Descendants(form)) _ = child.Handle; LayoutTree(form);
            foreach (string name in new[] { "DuplicateInputPreset", "DefaultInputPreset" })
            {
                var button = (Button)form.Controls.Find(name, true).Single();
                Size text = TextRenderer.MeasureText(button.Text, button.Font);
                Check(button.Height >= 36 && button.Height >= text.Height + 6, $"{name}の高さ不足: {button.Size} / 文字{text}");
                Check(button.Width >= text.Width + 8 && button.Parent!.ClientRectangle.Contains(button.Bounds), $"{name}が親領域外か文字幅不足");
            }
            var first = form.Controls.Find("DuplicateInputPreset", true).Single(); var second = form.Controls.Find("DefaultInputPreset", true).Single();
            Check(first.Right <= second.Left && first.Top == second.Top, "ボタンが重なるか並びがずれる");
        });
        Run("新規は入力出力各3件と矩形コピー既定・暗転ON", () =>
        {
            var settings = new AppSettings().ApplyDefaults(); settings.Validate();
            Equal(2, settings.PresetSchemaVersion); Equal(3, settings.SavedRegions.Length); Equal(3, settings.CapturePresets.Length);
            Equal(CaptureMode.Rectangle, settings.DefaultMode); Equal(OutputDestination.Clipboard, settings.DefaultDestination);
            Check(settings.DimCaptureBackground && settings.FreezeFrame && !settings.IncludeCursor && !settings.Grid20, "標準入力の初期条件が異なる");
            Equal(0, settings.ScreenshotPaddingPixels); Equal(ScreenshotColorDepth.Original, settings.ScreenshotColorDepth);
            Equal(CaptureMetadataMode.None, settings.MetadataMode); Equal("Win+Shift+S", settings.ScreenshotShortcut1);
            Equal("Rectangle,Window,FixedRegion", string.Join(',', settings.SavedRegions.Select(p => p.Mode)));
            Check(settings.SavedRegions.All(p => !p.UseFixedCoordinates && p.FreezeFrame && p.PaddingPixels == 0 && !p.IncludeCursor && p.Shortcut == ""), "初期入力に座標・キー・加工が混入");
            Equal(new Size(1280, 720), settings.SavedRegions.Single(p => p.Mode == CaptureMode.FixedRegion).Bounds.Size);
            Check(settings.CapturePresets.Select(p => p.Destination).ToHashSet().SetEquals([OutputDestination.Clipboard, OutputDestination.File, OutputDestination.Clipboard | OutputDestination.File]), "初期出力の組合せが異なる");
            Check(settings.CapturePresets.All(p => !p.OpenFolderAfterSave && p.ColorDepth == ScreenshotColorDepth.Original && string.IsNullOrEmpty(p.ImageEditorId) && string.IsNullOrEmpty(p.VideoEditorId)), "初期出力がアプリやフォルダを開く");
        });
        Run("初期候補追加はv1の既定・ID・値と重複名を保持", () =>
        {
            var baseline = new AppSettings { DefaultMode = CaptureMode.Freeform, ScreenshotPaddingPixels = 17, IncludeCursor = true, DimCaptureBackground = false };
            var input = SavedRegion.FromSettings("ウィンドウ", baseline) with { Id = "initial-input-window", IsDefault = true };
            var output = CapturePreset.FromSettings("ファイル保存", baseline) with { Id = "initial-output-file", IsDefault = true };
            var legacy = baseline with { PresetSchemaVersion = 1, SavedRegions = [input], CapturePresets = [output] };
            var next = legacy.ApplyDefaults(); next.Validate();
            Equal(3, next.SavedRegions.Length); Equal(3, next.CapturePresets.Length);
            Equal(input, next.SavedRegions.Single(p => p.IsDefault)); Equal(output, next.CapturePresets.Single(p => p.IsDefault));
            Equal(17, next.ScreenshotPaddingPixels); Equal(CaptureMode.Freeform, next.DefaultMode); Check(next.IncludeCursor && !next.DimCaptureBackground, "既存条件が上書きされた");
            Equal(3, next.SavedRegions.Select(p => p.Id).Distinct().Count()); Equal(3, next.SavedRegions.Select(p => p.Name).Distinct().Count());
            Equal(3, next.CapturePresets.Select(p => p.Id).Distinct().Count()); Equal(3, next.CapturePresets.Select(p => p.Name).Distinct().Count());
        });
        Run("初期候補は3件以上の既存登録へ追加せず削除後も復活しない", () =>
        {
            var settings = new AppSettings().EnsurePresets();
            var many = settings with { PresetSchemaVersion = 1,
                SavedRegions = [..settings.SavedRegions, settings.SavedRegions[0] with { Id = "custom-input", Name = "追加", IsDefault = false }],
                CapturePresets = [..settings.CapturePresets, settings.CapturePresets[0] with { Id = "custom-output", Name = "追加", IsDefault = false }] };
            var next = many.EnsurePresets(); Equal(4, next.SavedRegions.Length); Equal(4, next.CapturePresets.Length);
            var removed = settings with { SavedRegions = [settings.SavedRegions.Single(p => p.IsDefault)], CapturePresets = [settings.CapturePresets.Single(p => p.IsDefault)] };
            Check(ReferenceEquals(removed, removed.EnsurePresets()), "削除済み候補を再追加した"); removed.Validate();
        });
        Run("コピー用の無効保存先は新規ファイル候補へ流用しない", () =>
        {
            var settings = new AppSettings { SaveDirectory = "relative", FileNamePattern = "{unknown}" }.ApplyDefaults();
            settings.Validate(); Equal("relative", settings.SaveDirectory); Equal("{unknown}", settings.FileNamePattern);
            foreach (var preset in settings.CapturePresets.Where(p => p.Destination.HasFlag(OutputDestination.File)))
            { preset.Validate(); Check(Path.IsPathFullyQualified(preset.SaveDirectory), "無効な保存先を流用"); Equal(FileNamePattern.Default, preset.FileNamePattern); }
        });
        Run("v1候補追加は読込だけでは保存せず初回保存時に原本退避", () => WithServiceDirectory(directory =>
        {
            string path = Path.Combine(directory, "settings.json");
            var current = new AppSettings().EnsurePresets(); var legacy = current with { PresetSchemaVersion = 1, SavedRegions = [current.SavedRegions[0]], CapturePresets = [current.CapturePresets[0]], DimCaptureBackground = false };
            string json = System.Text.Json.JsonSerializer.Serialize(legacy); File.WriteAllText(path, json);
            var store = new SettingsStore(path); var loaded = store.Load(out var warning); Equal<string?>(null, warning); Equal(3, loaded.SavedRegions.Length); Equal(json, File.ReadAllText(path));
            store.Save(loaded); Equal(json, File.ReadAllText(path + ".presets-v1.bak"));
            var restored = store.Load(out warning); Equal<string?>(null, warning); SettingsEqual(loaded, restored); Check(!restored.DimCaptureBackground, "明示OFFを変更");
            store.Save(restored); Equal(json, File.ReadAllText(path + ".presets-v1.bak"));
        }));
        Run("初期3候補は設定一覧と撮影中メニューの双方から選択可能", () =>
        {
            var settings = new AppSettings().ApplyDefaults();
            using var form = new SettingsForm(settings, _ => null, () => { }, () => []);
            Equal(3, ((ListBox)form.Controls.Find("SavedRegions", true).Single()).Items.Count);
            Equal(3, ((ListBox)form.Controls.Find("CapturePresetList", true).Single()).Items.Count);
            Check(((CheckBox)form.Controls.Find("DimCaptureBackground", true).Single()).Checked, "新規設定UIが暗転OFF");
            using var menu = new CaptureContextMenu(settings.DefaultDestination, _ => { }, () => { }, () => { },
                inputPresets: settings.SavedRegions, applyInputPreset: _ => { }, presets: settings.CapturePresets, applyPreset: _ => { });
            Equal(3, menu.Command("InputPresetMenu").DropDownItems.Count); Equal(3, menu.Command("CapturePresetMenu").DropDownItems.Count);
        });
        Run("統合画面は9分類と内部区分で旧入力・命名ページを重複表示しない", () =>
        {
            using var form = new SettingsForm(new AppSettings(), _ => null, () => { }, () => []);
            var tabs = (TabControl)form.Controls.Find("SettingsTabs", true).Single();
            Equal("基本,入力プリセット,録画・音声,出力プリセット,外部アプリ,ショートカット,即時撮影キー,通知,履歴", string.Join(',', tabs.TabPages.Cast<TabPage>().Select(p => p.Text)));
            var input = tabs.TabPages.Cast<TabPage>().Single(p => p.Text == "入力プリセット"); var output = tabs.TabPages.Cast<TabPage>().Single(p => p.Text == "出力プリセット");
            Check(input.Contains(form.Controls.Find("ScreenshotPaddingPixels", true).Single()), "入力に余白がない");
            Check(output.Contains(form.Controls.Find("ScreenshotColorDepth", true).Single()), "出力に減色がない");
            Check(tabs.TabPages[0].Contains(form.Controls.Find("LimitToStartingMonitor", true).Single()), "モニタ制限が基本にない");
            Check(!((RegionManagerForm)input.Controls[0]).AutoScroll, "入力にスクロールがある");
            Check(!input.Contains(form.Controls.Find("SystemAudio", true).Single()), "録画音声が入力に混入");
        });
        Run("入力の複製・既定・削除保護と基本概要からの編集", () =>
        {
            AppSettings? saved = null;
            using var form = new SettingsForm(new AppSettings(), s => { saved = s; return null; }, () => { }, () => []);
            var editor = (RegionManagerForm)form.Controls.Find("RegionManagerForm", true).Single();
            ConsolidationClick(form, "DuplicateInputPreset"); form.Controls.Find("RegionName", true).Single().Text = "ウィンドウ用";
            ((ComboBox)form.Controls.Find("CaptureMode", true).Single()).SelectedIndex = (int)CaptureMode.Window;
            ConsolidationClick(form, "DefaultInputPreset"); Equal(4, editor.Regions.Length); Equal(1, editor.Regions.Count(p => p.IsDefault));
            ConsolidationClick(form, "DeleteRegion"); Equal(4, editor.Regions.Length);
            SaveConsolidation(form); Check(saved is not null, "保存しない"); Equal(CaptureMode.Window, saved!.DefaultMode);
            ConsolidationClick(form, "DefaultInputSummary"); Equal("入力プリセット", ((TabControl)form.Controls.Find("SettingsTabs", true).Single()).SelectedTab!.Text);
            Equal("ウィンドウ用", form.Controls.Find("RegionName", true).Single().Text);
        });
        Run("出力の複製・既定は減色と命名を切り替え入力余白を維持", () =>
        {
            AppSettings? saved = null;
            using var form = new SettingsForm(new AppSettings { ScreenshotPaddingPixels = 12 }, s => { saved = s; return null; }, () => { }, () => []);
            var editor = (CapturePresetEditor)form.Controls.Find("CapturePresetEditor", true).Single();
            ConsolidationClick(form, "DuplicateCapturePreset"); form.Controls.Find("CapturePresetName", true).Single().Text = "16色";
            ((ComboBox)form.Controls.Find("ScreenshotColorDepth", true).Single()).SelectedIndex = (int)ScreenshotColorDepth.Indexed4;
            form.Controls.Find("FileNamePattern", true).Single().Text = "sample.{ext}";
            ConsolidationClick(form, "DefaultCapturePreset"); ConsolidationClick(form, "DeleteCapturePreset"); Equal(4, editor.Presets.Length);
            SaveConsolidation(form); Check(saved is not null, "出力の既定が保存されない");
            Equal(ScreenshotColorDepth.Indexed4, saved!.ScreenshotColorDepth); Equal("sample.{ext}", saved.FileNamePattern); Equal(12, saved.ScreenshotPaddingPixels);
            ConsolidationClick(form, "DefaultOutputSummary"); Equal("16色", form.Controls.Find("CapturePresetName", true).Single().Text);
        });
        Run("スナップ数値と共通表示設定を保存してプリセットと独立維持", () =>
        {
            AppSettings? saved = null;
            using var form = new SettingsForm(new AppSettings(), s => { saved = s; return null; }, () => { }, () => []);
            ((NumericUpDown)form.Controls.Find("SnapDistancePixels", true).Single()).Value = 5;
            ((NumericUpDown)form.Controls.Find("GridStepPixels", true).Single()).Value = 7;
            ((CheckBox)form.Controls.Find("GridEnabled", true).Single()).Checked = true;
            SaveConsolidation(form); Equal(5, saved!.SnapDistancePixels); Equal(7, saved.GridStepPixels); Check(saved.Grid20, "刻み設定が反映されない");
            saved.Validate();
        });
        Run("入力と出力の一時差分とリセットは相互に影響しない", () =>
        {
            var session = MenuSession();
            try
            {
                Check(!session.InputPresetModified && !session.OutputPresetModified, "開始直後に変更扱い");
                session.ChangePadding(10); session.ChangeColor(ScreenshotColorDepth.Indexed4);
                Check(session.InputPresetModified && session.OutputPresetModified, "差分が表示されない");
                Check(session.SelectionMenu.Command("InputPresetMenu").Text!.Contains("変更あり"), "入力の変更表示がない");
                session.ResetOutputPreset(); Equal(10, session.OutputSettings.ScreenshotPaddingPixels); Check(!session.OutputPresetModified && session.InputPresetModified, "出力復元が入力へ影響");
                session.ChangeColor(ScreenshotColorDepth.Indexed8); WaitHistory(session.ResetInputPresetAsync());
                Check(!session.InputPresetModified && session.OutputPresetModified, "入力復元が出力へ影響");
                Equal(ScreenshotColorDepth.Indexed8, session.OutputSettings.ScreenshotColorDepth);
            }
            finally { session.Cancel(); }
        });
        Run("撮影中の保存詳細は検証後だけ適用しプリセットを不変に保つ", () =>
        {
            var session = MenuSession();
            try
            {
                string original = session.OutputSettings.FileNamePattern;
                session.ChangeOutputDetails(session.OutputSettings.SaveDirectory, "test_{##}.{ext}", "-");
                Check(session.OutputPresetModified, "詳細編集を差分としない");
                try { session.ChangeOutputDetails("relative", "{unknown}", "-"); throw new Exception("無効な詳細を適用"); } catch (ArgumentException) { }
                Equal("test_{##}.{ext}", session.OutputSettings.FileNamePattern);
                session.ResetOutputPreset(); Equal(original, session.OutputSettings.FileNamePattern);
            }
            finally { session.Cancel(); }
        });
        Run("入力プリセットの画面再取得失敗は元のプリセット選択も保持", () =>
        {
            var session = MenuSession((_, _) => throw new IOException("失敗"));
            try
            {
                var preset = SavedRegion.FromSettings("別入力", session.OutputSettings with { IncludeCursor = true, FixedWidth = 100, FixedHeight = 80, DefaultMode = CaptureMode.FixedRegion });
                WaitHistory(session.ApplyInputPresetAsync(preset));
                Check(!session.OutputSettings.IncludeCursor && !session.InputPresetModified, "失敗した入力を採用");
                Check(!session.SelectionMenu.Command("InputPresetMenu").Text!.Contains("別入力"), "失敗したプリセット名を表示");
            }
            finally { session.Cancel(); }
        });
        Run("プリセット移行は初回保存時に旧原本を退避し再移行しない", () => WithServiceDirectory(directory =>
        {
            string path = Path.Combine(directory, "settings.json"), old = "{\"SchemaVersion\":3,\"ScreenshotPaddingPixels\":12}";
            File.WriteAllText(path, old); var store = new SettingsStore(path); var settings = store.Load(out var warning);
            Equal<string?>(null, warning); Equal(old, File.ReadAllText(path)); store.Save(settings);
            Equal(old, File.ReadAllText(path + ".presets-v0.bak")); var loaded = store.Load(out warning); SettingsEqual(settings, loaded);
            store.Save(loaded); Equal(old, File.ReadAllText(path + ".presets-v0.bak"));
        }));
        Run("旧設定から既定を生成し入出力と固有余白を欠落なく移行", () =>
        {
            var old = new AppSettings { IncludeCursor = true, ScreenshotPaddingPixels = 12, RecordMicrophone = true,
                ScreenshotColorDepth = ScreenshotColorDepth.Indexed4, CapturePresets = [new() { Name = "資料", PaddingPixels = 50 }] };
            var migrated = old.EnsurePresets(); migrated.Validate();
            Equal(1, migrated.SavedRegions.Count(p => p.IsDefault)); Equal(1, migrated.CapturePresets.Count(p => p.IsDefault));
            Check(migrated.SavedRegions.Any(p => p.PaddingPixels == 50), "旧出力の余白を失った");
            var projected = migrated.ApplyDefaults(); Equal(12, projected.ScreenshotPaddingPixels);
            Check(projected.IncludeCursor && projected.RecordMicrophone, "移行で取得条件や録画音声を失った");
            Check(ReferenceEquals(migrated, migrated.EnsurePresets()), "重複移行した");
            var output = CapturePreset.FromSettings("出力", old with { ScreenshotColorDepth = ScreenshotColorDepth.Indexed8 });
            var result = output.Apply(projected); Equal(12, result.ScreenshotPaddingPixels); Equal(ScreenshotColorDepth.Indexed8, result.ScreenshotColorDepth);
            var input = SavedRegion.FromSettings("入力", old with { ScreenshotPaddingPixels = 20, DefaultMode = CaptureMode.Window });
            result = input.Apply(result); Equal(20, result.ScreenshotPaddingPixels); Equal(CaptureMode.Window, result.DefaultMode);
            Equal(ScreenshotColorDepth.Indexed8, result.ScreenshotColorDepth); Check(result.RecordMicrophone, "入力プリセットが録画音声を変えた");
        });
        Run("座標なしプリセットは画面構成なしで有効・既定は各1件", () =>
        {
            var settings = new AppSettings().EnsurePresets(); settings.Validate();
            settings.SavedRegions[0].Validate();
            try { (settings with { SavedRegions = [] }).Validate(); throw new Exception("既定なしを受理した"); } catch (ArgumentException) { }
            var duplicate = settings.SavedRegions[0] with { Id = "second" };
            try { (settings with { SavedRegions = [settings.SavedRegions[0], duplicate] }).Validate(); throw new Exception("複数既定を受理した"); } catch (ArgumentException) { }
        });
        Run("スナップ距離と刻み幅は20既定・独立した物理px", () =>
        {
            Equal(20, new AppSettings().SnapDistancePixels); Equal(20, new AppSettings().GridStepPixels);
            var snap = new SelectionSnap([], [new(100, 100, 100, 100)], 5);
            Equal(new Point(100, 130), snap.Apply(new(105, 130))); Equal(new Point(106, 130), snap.Apply(new(106, 130)));
            var gesture = new SelectionGesture(CaptureMode.Rectangle, [new(-200, -200, 600, 600)], []) { Grid20 = true };
            gesture.SetSnapPrecision(5, 7); gesture.Begin(new(-100, -100), new(1, 1)); gesture.Move(new(-83, -83));
            Equal(new Size(14, 14), gesture.Selection.Size);
            gesture.Move(new(-110, -110)); Equal(new Size(7, 7), gesture.Selection.Size);
            gesture.ExpandMonitors([new(-200, -200, 700, 700)]); Equal(5, gesture.SnapDistancePixels); Equal(7, gesture.GridStepPixels);
        });
        Run("extは静止画・録画・OCR省略・連番で重複拡張子を付けない", () =>
        {
            var at = new DateTimeOffset(2026, 10, 22, 12, 34, 56, TimeSpan.Zero);
            Equal("20261022_123456.png", FileNamePattern.Expand(FileNamePattern.Default, at, CaptureIntent.Screenshot));
            Equal("20261022_123456.mp4", FileNamePattern.Expand(FileNamePattern.Default, at, CaptureIntent.Recording));
            Equal("sample.png", FileNamePattern.Expand("sample_{ocr}.{ext}", at, CaptureIntent.Screenshot));
            Equal("sample_02.mp4", FileNamePattern.Expand("sample_{##}.{ext}", at, CaptureIntent.Recording, sequence: 2));
            Equal("legacy.mp4", FileNamePattern.Expand("legacy.png", at, CaptureIntent.Recording));
        });
        Run("スナップ精度の不正値を拒否し次の撮影へ一時値を漏らさない", () =>
        {
            foreach (int value in new[] { 0, -1, 32769 })
            {
                try { new AppSettings { GridStepPixels = value }.Validate(); throw new Exception("刻み幅を受理した"); } catch (ArgumentException) { }
                try { new SelectionSnap([], [], value); throw new Exception("吸着距離を受理した"); } catch (ArgumentException) { }
            }
            var settings = new AppSettings(); var session = MenuSession(settings: settings);
            try { session.ChangeSnapPrecision(3, 9); Equal(3, session.OutputSettings.SnapDistancePixels); Equal(20, settings.SnapDistancePixels); }
            finally { session.Cancel(); }
        });
    }
    private static void ConsolidationClick(Control form, string name) => typeof(Button).GetMethod("OnClick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(form.Controls.Find(name, true).Single(), [EventArgs.Empty]);
    private static void SaveConsolidation(SettingsForm form) => typeof(SettingsForm).GetMethod("SaveSettings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(form, null);
}
