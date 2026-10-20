using SSCapture.Capture;
using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture.Tests;
internal static partial class Program
{
    private static void ExternalApplicationTests()
    {
        Run("Schema2の外部アプリは原本退避後に1回だけ移行し欠損exeを保持", () => WithServiceDirectory(directory =>
        {
            string path = Path.Combine(directory, "settings.json");
            string original = "{\"SchemaVersion\":2,\"ImageEditorPath\":\"C:\\\\missing\\\\editor.exe\",\"ImageEditorArguments\":\"--edit \\\"{file}\\\"\"}";
            File.WriteAllText(path, original);
            var store = new SettingsStore(path); var migrated = store.Load(out var warning);
            Equal<string?>(null, warning); Equal(3, migrated.SchemaVersion); Equal(1, migrated.ImageEditors.Length);
            Equal(original, File.ReadAllText(path)); Equal("legacy-image", migrated.SelectedImageEditorId);
            store.Save(migrated); Equal(original, File.ReadAllText(path + ".schema2.bak"));
            store.Save(store.Load(out _)); Equal(1, store.Load(out _).ImageEditors.Length); Equal(original, File.ReadAllText(path + ".schema2.bak"));
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
            var session = OutputSession(settings);
            try
            {
                session.ChangeApplication("b"); Equal("b", session.OutputSettings.SelectedImageEditorId);
                Equal("a", settings.SelectedImageEditorId);
                var menu = (ToolStripMenuItem)session.SelectionMenu.Items["ExternalApplicationMenu"]!;
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
            ((Button)ui.Controls.Find("ImageEditorNew", true).Single()).PerformClick();
            name.Text = "B"; ui.Executable.Text = @"C:\b.exe";
            ((Button)ui.Controls.Find("ImageEditorStore", true).Single()).PerformClick(); Equal(2, ui.Applications.Length);
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
