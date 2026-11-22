using SSCapture.Capture;
using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture.Tests;
internal static partial class Program
{
    private static void ExternalApplicationTests()
    {
        Run("外部アプリ表は6件分の高さと通知・複数プリセットの使用先を表示する", () =>
        {
            var editors = Enumerable.Range(1, 7).Select(i => new ExternalApplication
                { Id = $"editor-{i}", Name = $"編集アプリ {i}", Executable = $@"C:\editor-{i}.exe" }).ToArray();
            CapturePreset[] presets =
            [
                new() { Name = "クリップボード", IsDefault = true, ImageEditorId = editors[0].Id },
                new() { Name = "資料用", Destination = OutputDestination.ExternalApp, ImageEditorId = editors[0].Id, VideoEditorId = editors[1].Id },
                new() { Name = "保存用", ImageEditorId = editors[1].Id }
            ];
            string notification = "";
            using var host = new Form { ClientSize = new Size(900, 500) };
            using var editor = new ExternalApplicationsEditor(CaptureIntent.Screenshot, editors, editors[0].Id, "", EditorArguments.Default,
                EditorPresets.Images, () => (presets, notification));
            host.Controls.Add(editor);
            _ = host.Handle; foreach (Control control in Descendants(host)) _ = control.Handle;
            LayoutTree(host);
            var table = (DataGridView)editor.Controls.Find("ImageEditorList", true).Single();
            Check(table.Columns.Cast<DataGridViewColumn>().Select(c => c.HeaderText)
                .SequenceEqual(["アプリ名", "通知", "出力プリセット"]), "一覧の列が違う");
            Equal(7, table.Rows.Count);
            Equal(6, table.DisplayedRowCount(false));
            Equal("出力プリセットのアプリを使う", table.Rows[0].Cells[1].Value);
            Equal("★ クリップボード、資料用", table.Rows[0].Cells[2].Value);
            Check(table.Rows[0].Cells[2].ToolTipText.Contains("自動で開く：OFF"), "自動起動OFFの説明がない");
            Equal("保存用", table.Rows[1].Cells[2].Value);
            notification = editors[1].Id;
            editor.RefreshUsage();
            Equal("—", table.Rows[0].Cells[1].Value);
            Equal("通知用に指定", table.Rows[1].Cells[1].Value);
            table.CurrentCell = table.Rows[1].Cells[0];
            Equal(editors[1].Name, editor.Controls.Find("ImageEditorName", true).Single().Text);
        });

        Run("PPPaintは既存の実行ファイルがある導入先だけを候補にする", () =>
        {
            const string installed = @"C:\KotorinToys\.installed\PPPaint\PPPaint.exe";
            string?[] locations = [null, @"relative\PPPaint", @"C:\missing", Path.GetDirectoryName(installed)];
            Equal(installed, EditorExecutable.FindPPPaint(locations, path => path == installed));
            Check(EditorExecutable.FindPPPaint(locations, _ => false) is null, "未導入のPPPaintが表示対象になった");
        });
        Run("ペイントは一度だけ初期登録し外部出力を有効化しない", () => WithServiceDirectory(directory =>
        {
            var settings = new AppSettings().ApplyDefaults();
            Equal(1, settings.ImageEditors.Length); Equal("ペイント", settings.ImageEditors[0].Name);
            Equal(settings.ImageEditors[0].Id, settings.SelectedImageEditorId);
            Equal(OutputDestination.Clipboard, settings.DefaultDestination);
            Check(ReferenceEquals(settings, settings.EnsurePresets()), "初期登録を繰り返した");
            var removed = settings with { ImageEditors = [], SelectedImageEditorId = "", ImageEditorPath = "",
                CapturePresets = settings.CapturePresets.Select(p => p with { ImageEditorId = null }).ToArray() };
            var store = new SettingsStore(Path.Combine(directory, "settings.json")); store.Save(removed);
            Equal(0, store.Load(out _).ImageEditors.Length);
        }));
        Run("初期登録は既存選択とペイント登録を保持する", () =>
        {
            var paint = new ExternalApplication { Id = "mine", Name = "私のペイント", Executable = EditorExecutable.PaintPreset, Arguments = "\"{file}\"" };
            var existing = new AppSettings { ImageEditors = [paint], SelectedImageEditorId = paint.Id }.ApplyDefaults();
            Equal(1, existing.ImageEditors.Length); Equal(paint, existing.ImageEditors[0]); Equal("mine", existing.SelectedImageEditorId);
            var custom = paint with { Id = "custom", Name = "別の編集", Executable = @"C:\custom.exe" };
            var next = new AppSettings { ImageEditors = [custom], SelectedImageEditorId = custom.Id }.ApplyDefaults();
            Equal(2, next.ImageEditors.Length); Equal(custom.Id, next.SelectedImageEditorId);
        });
        Run("旧登録と名前ID衝突・上限・明示なしを保持して一度だけ移行", () => WithServiceDirectory(directory =>
        {
            var legacy = new AppSettings { ImageEditorPath = @"C:\old.exe", ImageEditorArguments = "--open \"{file}\"" }.ApplyDefaults();
            Equal("legacy-image", legacy.SelectedImageEditorId); Equal(@"C:\old.exe", legacy.ImageEditors[0].Executable);
            var collision = new ExternalApplication { Id = "builtin-paint", Name = "ペイント", Executable = @"C:\custom.exe" };
            var migrated = new AppSettings { ImageEditors = [collision] }.ApplyDefaults();
            Equal("builtin-paint-2", migrated.ImageEditors[1].Id); Equal("ペイント 2", migrated.ImageEditors[1].Name); Equal("", migrated.SelectedImageEditorId);
            var full = new AppSettings { ImageEditors = Enumerable.Range(0, 50).Select(i => collision with { Id = "id" + i, Name = "app" + i }).ToArray() }.EnsurePresets();
            Equal(50, full.ImageEditors.Length); full.Validate();
            var current = new AppSettings().ApplyDefaults();
            var old = current with { ExternalApplicationsVersion = 0, ImageEditors = [], SelectedImageEditorId = "", ImageEditorPath = "",
                CapturePresets = current.CapturePresets.Select(p => p with { ImageEditorId = null }).ToArray() };
            string path = Path.Combine(directory, "settings.json"); string json = System.Text.Json.JsonSerializer.Serialize(old); File.WriteAllText(path, json);
            var store = new SettingsStore(path); var loaded = store.Load(out var warning); Equal<string?>(null, warning);
            Equal(1, loaded.ImageEditors.Length); Equal("", loaded.SelectedImageEditorId); Equal(json, File.ReadAllText(path));
            store.Save(loaded); Equal(json, File.ReadAllText(path + ".external-apps-v0.bak")); SettingsEqual(loaded, store.Load(out _));
        }));
        Run("外部アプリと既定出力は双方向同期し編集中の別項目を失わない", () => WithServiceDirectory(directory =>
        {
            var a = new ExternalApplication { Id = "a", Name = "A", Executable = @"C:\a.exe" };
            var b = a with { Id = "b", Name = "B", Executable = @"C:\b.exe" };
            var settings = new AppSettings { ImageEditors = [a, b], SelectedImageEditorId = "a" }.ApplyDefaults();
            using var apps = new ExternalApplicationsEditor(CaptureIntent.Screenshot, settings.ImageEditors, "a", "", EditorArguments.Default,
                EditorPresets.Images, () => ([], ""));
            using var presets = new CapturePresetEditor(settings, () => settings with { ImageEditors = apps.Applications });
            apps.SelectedChanged += id => presets.SetDefaultApplication(true, id);
            presets.DefaultApplicationsChanged += (image, _) => apps.SelectApplication(image ?? "");
            var name = (TextBox)presets.Controls.Find("CapturePresetName", true).Single(); name.Text = "編集を保持";
            ((ComboBox)apps.Controls.Find("ImageSelectedEditor", true).Single()).SelectedIndex = 2;
            Equal("編集を保持", name.Text);
            var combo = (ComboBox)presets.Controls.Find("PresetImageEditor", true).Single();
            Equal("b", ((ExternalApplication)combo.SelectedItem!).Id);
            combo.SelectedIndex = 1; Equal("a", apps.SelectedId);
            presets.StoreCurrent(); Equal<string?>(null, presets.Error);
            var store = new SettingsStore(Path.Combine(directory, "settings.json"));
            store.Save((settings with { CapturePresets = presets.Presets, SelectedImageEditorId = apps.SelectedId }).ApplyDefaults());
            var loaded = store.Load(out var warning); Equal<string?>(null, warning); Equal("a", loaded.SelectedImageEditorId);
            Equal("編集を保持", loaded.CapturePresets.Single(p => p.IsDefault).Name);
        }));
        Run("外部アプリの適用で画像動画の選択を保持し別プリセットの下書きは変えない", () =>
        {
            var a = new ExternalApplication { Id = "a", Name = "A", Executable = @"C:\a.exe" };
            var b = a with { Id = "b", Name = "B", Executable = @"C:\b.exe" };
            var settings = new AppSettings { ImageEditors = [a, b], VideoEditors = [a, b], SelectedImageEditorId = "a", SelectedVideoEditorId = "a" }.ApplyDefaults();
            AppSettings? saved = null;
            using var form = new SettingsForm(settings, next => { saved = next; return null; }, _ => { }, () => []);
            var appTable = (DataGridView)form.Controls.Find("ImageEditorList", true).Single();
            Equal("出力プリセットのアプリを使う", appTable.Rows[0].Cells[1].Value);
            var list = (ListBox)form.Controls.Find("CapturePresetList", true).Single(); list.SelectedIndex = 1;
            string? nonDefaultApp = ((CapturePreset)list.SelectedItem!).ImageEditorId;
            form.Controls.Find("CapturePresetName", true).Single().Text = "別出力を編集中";
            ((ComboBox)form.Controls.Find("ImageSelectedEditor", true).Single()).SelectedIndex = 2;
            ((ComboBox)form.Controls.Find("VideoSelectedEditor", true).Single()).SelectedIndex = 2;
            Check(appTable.Rows[1].Cells[2].Value is string usage && usage.StartsWith("★ ", StringComparison.Ordinal), "既定出力の変更を表に反映しない");
            Equal("別出力を編集中", form.Controls.Find("CapturePresetName", true).Single().Text);
            form.SelectCategory("外部アプリ");
            var pending = saved;
            typeof(Form).GetMethod("OnFormClosing", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(form, [new FormClosingEventArgs(CloseReason.UserClosing, false)]);
            Equal(pending, saved);
            ApplySettingsPage(form, "外部アプリ"); Check(saved is not null, "外部アプリを適用しない");
            Equal("b", saved!.SelectedImageEditorId); Equal("b", saved.SelectedVideoEditorId);
            Check(saved.CapturePresets.All(p => p.Name != "別出力を編集中"), "未確定の別出力まで適用された");
            Equal("別出力を編集中", form.Controls.Find("CapturePresetName", true).Single().Text);
            ConsolidationClick(form, "StoreCapturePreset");
            Equal(nonDefaultApp ?? "", saved.CapturePresets.Single(p => p.Name == "別出力を編集中").ImageEditorId ?? "");
            list.SelectedIndex = 0;
            var image = (ComboBox)form.Controls.Find("PresetImageEditor", true).Single();
            image.SelectedIndex = 1; Equal("a", ((ExternalApplicationsEditor)form.Controls.Find("ImageApplicationsEditor", true).Single()).SelectedId);
            typeof(Control).GetMethod("OnEnter", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(image, [EventArgs.Empty]);
            Equal("a", ((ExternalApplicationsEditor)form.Controls.Find("ImageApplicationsEditor", true).Single()).SelectedId);
            ConsolidationClick(form, "StoreCapturePreset"); Equal("a", saved!.SelectedImageEditorId); Equal("b", saved.SelectedVideoEditorId);
        });
        Run("Schema2の外部アプリは原本退避後に1回だけ移行し欠損exeを保持", () => WithServiceDirectory(directory =>
        {
            string path = Path.Combine(directory, "settings.json");
            string original = "{\"SchemaVersion\":2,\"ImageEditorPath\":\"C:\\\\missing\\\\editor.exe\",\"ImageEditorArguments\":\"--edit \\\"{file}\\\"\"}";
            File.WriteAllText(path, original);
            var store = new SettingsStore(path); var migrated = store.Load(out var warning);
            Equal<string?>(null, warning); Equal(3, migrated.SchemaVersion); Equal(2, migrated.ImageEditors.Length);
            Equal(original, File.ReadAllText(path)); Equal("legacy-image", migrated.SelectedImageEditorId);
            store.Save(migrated); Equal(original, File.ReadAllText(path + ".schema2.bak"));
            store.Save(store.Load(out _)); Equal(2, store.Load(out _).ImageEditors.Length); Equal(original, File.ReadAllText(path + ".schema2.bak"));
        }));
        Run("外部アプリの重複と既定・プリセットの参照切れを拒否", () =>
        {
            var app = new ExternalApplication { Id = "paint", Name = "編集", Executable = @"C:\missing\paint.exe" };
            var settings = new AppSettings { ImageEditors = [app], SelectedImageEditorId = app.Id };
            settings.Validate(); Equal(app.Executable, settings.ResolveEditors().ImageEditorPath);
            PaddingRejected(() => (settings with { ImageEditors = [] }).Validate());
            PaddingRejected(() => (settings with { ImageEditors = [app, app with { Id = "other" }] }).Validate());
            PaddingRejected(() => (settings with { CapturePresets = [new CapturePreset { Name = "資料", ImageEditorId = "missing" }] }).Validate());
        });
        Run("今回だけの外部アプリ選択とプリセット適用は既定に漏れない", () =>
        {
            var apps = new[] { new ExternalApplication { Id = "a", Name = "A", Executable = @"C:\a.exe" }, new ExternalApplication { Id = "b", Name = "B", Executable = @"C:\b.exe" } };
            var settings = new AppSettings { ImageEditors = apps, SelectedImageEditorId = "a" };
            using var editor = new CapturePresetEditor(settings, () => settings);
            var application = (ComboBox)editor.Controls.Find("PresetImageEditor", true).Single();
            Equal(3, application.Items.Count); Equal("A", ((ExternalApplication)application.SelectedItem!).Name);
            var session = OutputSession(settings);
            try
            {
                session.ChangeApplication("b"); Equal("b", session.OutputSettings.SelectedImageEditorId);
                Equal("a", settings.SelectedImageEditorId);
                var menu = (ToolStripMenuItem)session.SelectionMenu.Command("ExternalApplicationMenu")!;
                Equal(1, menu.DropDownItems.OfType<ToolStripMenuItem>().Count(i => i.Checked));
                session.ApplyPreset(new CapturePreset { Name = "Aへ", ImageEditorId = "a" }); Equal("a", session.OutputSettings.SelectedImageEditorId);
            }
            finally { session.Cancel(); }
        });
        Run("登録UIは複数登録・既定選択・参照中の削除拒否", () =>
        {
            using var ui = new ExternalApplicationsEditor(CaptureIntent.Screenshot, [], "", "", EditorArguments.Default,
                EditorPresets.Images, () => ([], ""));
            var name = (TextBox)ui.Controls.Find("ImageEditorName", true).Single();
            name.Text = "A"; ui.Executable.Text = @"C:\a.exe";
            ((Button)ui.Controls.Find("ImageEditorStore", true).Single()).PerformClick();
            Equal(1, ui.Applications.Length); Equal(ui.Applications[0].Id, ui.SelectedId);
            ((Button)ui.Controls.Find("ImageEditorDelete", true).Single()).PerformClick(); Equal(1, ui.Applications.Length);
            ((ToolStripMenuItem)ui.Controls.Find("ImageEditorNew", true).Single().ContextMenuStrip!.Items[0]).PerformClick();
            name.Text = "B"; ui.Executable.Text = @"C:\b.exe";
            ((Button)ui.Controls.Find("ImageEditorStore", true).Single()).PerformClick(); Equal(2, ui.Applications.Length);
        });
        Run("新規メニューは入力例から別の登録名とexeを入力するだけ", () =>
        {
            using var ui = new ExternalApplicationsEditor(CaptureIntent.Screenshot, [], "", "", EditorArguments.Default,
                EditorPresets.Images, () => ([], ""));
            var newButton = ui.Controls.Find("ImageEditorNew", true).Single();
            Check(!ui.Controls.Find("ImageEditorPreset", true).Any(), "旧コンボが残る");
            ((ToolStripMenuItem)newButton.ContextMenuStrip!.Items["MS ペイント"]!).PerformClick();
            var name = (TextBox)ui.Controls.Find("ImageEditorName", true).Single();
            Equal(Path.GetFileNameWithoutExtension(ui.Executable.Text), name.Text); Equal(0, ui.Applications.Length);
            ((Button)ui.Controls.Find("ImageEditorStore", true).Single()).PerformClick();
            Equal(1, ui.Applications.Length); Equal("保存", ui.Controls.Find("ImageEditorStore", true).Single().Text);
        });
        Run("壊れた旧設定からの明示保存は原本を退避して回復できる", () => WithServiceDirectory(directory =>
        {
            string path = Path.Combine(directory, "settings.json"); File.WriteAllText(path, "{broken");
            var store = new SettingsStore(path); var settings = store.Load(out var warning); Check(warning is not null, "破損警告なし");
            store.Save(settings); Equal("{broken", File.ReadAllText(path + ".invalid.bak")); SettingsEqual(settings, store.Load(out warning));
            Equal<string?>(null, warning);
        }));
    }
}
