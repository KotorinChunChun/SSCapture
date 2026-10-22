using SSCapture.Capture;
using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture.Tests;
internal static partial class Program
{
    private static void ExternalApplicationTests()
    {
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
            using var apps = new ExternalApplicationsEditor("Image", settings.ImageEditors, "a", "", EditorArguments.Default, EditorPresets.Images, () => []);
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
        Run("設定全体の保存で画像動画の選択を保持し別プリセットの編集は変えない", () =>
        {
            var a = new ExternalApplication { Id = "a", Name = "A", Executable = @"C:\a.exe" };
            var b = a with { Id = "b", Name = "B", Executable = @"C:\b.exe" };
            var settings = new AppSettings { ImageEditors = [a, b], VideoEditors = [a, b], SelectedImageEditorId = "a", SelectedVideoEditorId = "a" }.ApplyDefaults();
            AppSettings? saved = null;
            using var form = new SettingsForm(settings, next => { saved = next; return null; }, () => { }, () => []);
            var list = (ListBox)form.Controls.Find("CapturePresetList", true).Single(); list.SelectedIndex = 1;
            string? nonDefaultApp = ((CapturePreset)list.SelectedItem!).ImageEditorId;
            form.Controls.Find("CapturePresetName", true).Single().Text = "別出力を編集中";
            ((ComboBox)form.Controls.Find("ImageSelectedEditor", true).Single()).SelectedIndex = 2;
            ((ComboBox)form.Controls.Find("VideoSelectedEditor", true).Single()).SelectedIndex = 2;
            Equal("別出力を編集中", form.Controls.Find("CapturePresetName", true).Single().Text);
            SaveConsolidation(form); Check(saved is not null, "設定を保存しない");
            Equal("b", saved!.SelectedImageEditorId); Equal("b", saved.SelectedVideoEditorId);
            Equal(nonDefaultApp ?? "", saved.CapturePresets.Single(p => p.Name == "別出力を編集中").ImageEditorId ?? "");
            list.SelectedIndex = 0;
            var image = (ComboBox)form.Controls.Find("PresetImageEditor", true).Single();
            image.SelectedIndex = 1; Equal("a", ((ExternalApplicationsEditor)form.Controls.Find("ImageApplicationsEditor", true).Single()).SelectedId);
            typeof(Control).GetMethod("OnEnter", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(image, [EventArgs.Empty]);
            Equal("a", ((ExternalApplicationsEditor)form.Controls.Find("ImageApplicationsEditor", true).Single()).SelectedId);
            SaveConsolidation(form); Equal("a", saved!.SelectedImageEditorId); Equal("b", saved.SelectedVideoEditorId);
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
            using var ui = new ExternalApplicationsEditor("Image", [], "", "", EditorArguments.Default, EditorPresets.Images, () => ["referenced"]);
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
            using var ui = new ExternalApplicationsEditor("Image", [], "", "", EditorArguments.Default, EditorPresets.Images, () => []);
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
