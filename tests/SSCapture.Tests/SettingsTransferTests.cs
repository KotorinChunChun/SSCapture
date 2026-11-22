using KotorinToys.Integration;
using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture.Tests;

internal static partial class Program
{
    static void SettingsTransferTests()
    {
        Run("旧開発版の廃止された編集先だけを設定の読込と取込で除く", () => WithSettingsDirectory(directory =>
        {
            var retired = new ExternalApplication { Id = "old-editor", Name = "旧編集アプリ", Launch = (ExternalApplicationLaunch)2 };
            var defaults = new AppSettings().ApplyDefaults();
            var options = defaults with
            {
                DefaultDestination = OutputDestination.File | OutputDestination.ExternalApp,
                ImageEditors = [..defaults.ImageEditors, retired],
                SelectedImageEditorId = retired.Id,
                NotificationApplicationId = retired.Id,
                NotificationClickAction = NotificationClickAction.ExternalApplication,
                CapturePresets = defaults.CapturePresets.Select(preset => preset.IsDefault
                    ? preset with { ImageEditorId = retired.Id, Destination = OutputDestination.File | OutputDestination.ExternalApp }
                    : preset).ToArray()
            };
            string path = Path.Combine(directory, "old-preview.json");
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(options, SettingsStore.Options));
            var read = new SettingsStore(path).Load(out string? warning);
            Equal<string?>(null, warning);
            foreach (var settings in new[] { read, SettingsTransfer.Read(path) })
            {
                Check(defaults.ImageEditors.SequenceEqual(settings.ImageEditors), "別の編集アプリに影響した");
                Equal("", settings.SelectedImageEditorId);
                Equal("", settings.NotificationApplicationId);
                Equal(NotificationClickAction.None, settings.NotificationClickAction);
                Equal(OutputDestination.File, settings.DefaultDestination);
                Check(string.IsNullOrEmpty(settings.CapturePresets.Single(preset => preset.IsDefault).ImageEditorId), "廃止済みの編集先がプリセットに残った");
            }
        }));

        Run("設定ファイルはUTF-8で往復し、端末固有の指定を起動せず確認する", () => WithSettingsDirectory(directory =>
        {
            var legacyPath = Path.Combine(directory, "legacy.json");
            File.WriteAllText(legacyPath, """
                {
                  "DefaultDestination": 1,
                  "SaveDirectory": "C:\\移行元",
                  "FileNamePattern": "移行_{yyyymmdd}.{ext}",
                  "ShowCaptureNotification": false,
                  "ScreenshotShortcut1": "Win+Ctrl+2"
                }
                """);
            var legacy = SettingsTransfer.Read(legacyPath);
            Equal(OutputDestination.File, legacy.DefaultDestination);
            Equal(0, legacy.NotificationDurationSeconds);
            Check(!legacy.DimCaptureBackground, "旧版の暗転OFFを保持していない");

            var displays = new[] { new DisplayLayout("old-display", "旧ディスプレイ", new Rectangle(0, 0, 800, 600), 96, 96) };
            var region = RegionCoordinates.FromBounds("移行する範囲", new Rectangle(10, 20, 50, 80), displays) with
            { Id = "keep-input-id", RecordMicrophone = true, MicrophoneDeviceId = "old-mic",
                SelectionMethod = Capture.CaptureSelectionMethod.TwoClicks };
            var app = new ExternalApplication { Id = "keep-editor-id", Name = "移行する編集アプリ", Executable = @"C:\旧端末\editor.exe" };
            var input = legacy with
            {
                SavedRegions = [.. legacy.SavedRegions, region], ImageEditors = [app], CapturePresets = legacy.CapturePresets
                    .Select(p => p with { ImageEditorId = app.Id, WindowCrop = new(1, 2, 3, 4) }).ToArray(), SelectedImageEditorId = app.Id,
                PresetCaptureShortcuts = [new("Ctrl+Alt+F6", region.Id, legacy.CapturePresets.Single(p => p.IsDefault).Id)],
                GridStepPixels = 5, ShowPointerGuide = false, KeepHistory = false, NotificationDisplay = NotificationDisplay.Display10,
                ShowMagnifier = true, ShareCapturePresentation = true,
                CaptureBeforeKeys = new(Ctrl: Capture.CaptureKeyAction.SelectControl)
            };
            var export = Path.Combine(directory, "export.json");
            SettingsTransfer.Export(export, input);
            byte[] first = File.ReadAllBytes(export);
            Check(first[0] == '{' && File.ReadAllText(export).Contains(app.Name, StringComparison.Ordinal),
                "UTF-8 BOMなしのJSONで日本語の設定を書き出していない");
            ExpectSettingsFailure<IOException>(() => SettingsTransfer.Export(export, legacy));
            Check(first.SequenceEqual(File.ReadAllBytes(export)), "書き出し先にある既存ファイルを変更した");
            foreach (string key in new[] { "\"LastRegion\"", "\"StartupRegistration\"", "\"CaptureHistory\"" })
                Check(!File.ReadAllText(export).Contains(key, StringComparison.Ordinal), "実行時のデータを持ち出した");

            var clone = SettingsTransfer.Read(export);
            Equal(region.Id, clone.SavedRegions.Last().Id);
            Equal(Capture.CaptureSelectionMethod.TwoClicks, clone.SavedRegions.Last().SelectionMethod);
            Equal(input.PresetCaptureShortcuts!.Single(), clone.PresetCaptureShortcuts!.Single());
            Equal(new WindowCrop(1, 2, 3, 4), clone.CapturePresets.Single(p => p.IsDefault).WindowCrop);
            Check(clone.ShowMagnifier && clone.ShareCapturePresentation, "拡大鏡・配信表示の設定が失われた");
            Equal(Capture.CaptureKeyAction.SelectControlPush, clone.CaptureBeforeKeys.Ctrl);
            Equal(app.Id, clone.SelectedImageEditorId);
            Equal(input.CapturePresets.Single(p => p.IsDefault).Id, clone.CapturePresets.Single(p => p.IsDefault).Id);
            Equal(input.FileNamePattern, clone.FileNamePattern);
            Equal(5, clone.GridStepPixels);
            Check(!clone.ShowPointerGuide, "表示設定が保存されていない");
            string preview = SettingsTransfer.Preview(legacy, clone, () => [], () => [],
                () => [new Capture.DisplayTarget(@"\\.\DISPLAY2", new Rectangle(0, 0, 800, 600), true)]);
            foreach (string entry in new[] { app.Name, app.Executable, app.Arguments, "未確認", "範囲を再登録", "マイク [未接続",
                "撮影履歴が消去", "通知先のモニター 画面10 [未接続", "即時撮影キーのモニター [確認済み] 画面1",
                "即時撮影キーのモニター [接続未確認] 画面2" })
                Check(preview.Contains(entry, StringComparison.Ordinal), "確認結果に項目がない " + entry);
            var target = new SettingsStore(Path.Combine(directory, "target.json"));
            target.Import(clone);
            var persisted = target.Load(out var warning);
            Equal<string?>(null, warning);
            Equal(region.Id, persisted.SavedRegions.Last().Id);
            Equal(input.PresetCaptureShortcuts!.Single(), persisted.PresetCaptureShortcuts!.Single());
        }));

        Run("未知の形式・設定世代、不正JSON、重複、キー競合、参照切れを拒否する", () => WithSettingsDirectory(directory =>
        {
            string file = Path.Combine(directory, "import.json");
            foreach (string json in new[]
            {
                "{", "null", "[]", "{}", """{"Other":true}""",
                """{"Format":"Unknown","FormatVersion":1,"Settings":{"KeepHistory":true}}""",
                """{"Format":"SSCapture.Settings","FormatVersion":99,"Settings":{"KeepHistory":true}}""",
                """{"SchemaVersion":99}""", """{"SchemaVersion":"3"}""", """{"PresetSchemaVersion":99}""",
                """{"KeepHistory":true,"KeepHistory":false}""", """{"CaptureKeys":{"None":0,"None":1}}""",
                """{"KeepHistory":null}"""
            })
            {
                File.WriteAllText(file, json);
                ExpectSettingsFailure<System.Text.Json.JsonException>(() => _ = SettingsTransfer.Read(file));
            }

            foreach (string json in new[]
            {
                """{"SchemaVersion":3,"SelectedImageEditorId":"deleted"}""",
                """{"ScreenshotShortcut1":"PrintScreen","ScreenshotShortcut2":"PrintScreen"}"""
            })
            {
                File.WriteAllText(file, json);
                ExpectSettingsFailure<ArgumentException>(() => _ = SettingsTransfer.Read(file));
            }
        }));

        Run("取込でキー準備か設定ファイルの置換に失敗しても現設定を保持する", () => WithSettingsDirectory(directory =>
        {
            string target = Path.Combine(directory, "settings.json");
            var store = new SettingsStore(target);
            var current = new AppSettings().ApplyDefaults();
            var candidate = current with { PresetCaptureShortcuts = [new("Win+Ctrl+2")] };
            store.Save(current);
            byte[] previous = File.ReadAllBytes(target);
            using var oldHook = new FakePreparedHook();
            var replacement = new FakePreparedHook();
            var active = oldHook;
            ExpectSettingsFailure<InvalidOperationException>(() => active = store.PrepareAndSave<FakePreparedHook>(
                candidate, import: true, () => throw new InvalidOperationException("キー準備失敗")));
            Check(!File.Exists(target + ".import.bak"), "キー準備の失敗で設定をバックアップした");

            using (File.Open(target, FileMode.Open, FileAccess.Read, FileShare.Read))
                ExpectSettingsFailure<IOException>(() => active = store.PrepareAndSave(candidate, import: true, () => replacement));
            Check(replacement.Disposed && ReferenceEquals(active, oldHook) && !active.Disposed,
                "置換失敗で旧キーが使えないか、新しいキーが残った");
            Check(previous.SequenceEqual(File.ReadAllBytes(target)), "置換失敗で旧ファイルを変更した");
            Check(previous.SequenceEqual(File.ReadAllBytes(target + ".import.bak")), "旧ファイルを回復できない");

            using var success = store.PrepareAndSave(candidate, import: true, () => new FakePreparedHook());
            Check(!success.Disposed, "適用に使うキーを破棄した");
            Equal(new PresetCaptureShortcut("Win+Ctrl+2"), store.Load(out _).PresetCaptureShortcuts!.Single());
            Check(previous.SequenceEqual(File.ReadAllBytes(target + ".import.bak")), "前回の内容を復元できない");
        }));

        Run("設定CLIの事前確認に書き込みはなく、明示適用と持ち運びができる", () => WithSettingsDirectory(directory =>
        {
            var startup = new FakeStartup();
            var output = new StringWriter();
            var target = Path.Combine(directory, "data", "settings.json");
            var store = new SettingsStore(target);
            var import = Path.Combine(directory, "candidate.json");
            var export = Path.Combine(directory, "export.json");
            File.WriteAllText(import, """{"GridStepPixels":20,"KeepHistory":false}""");
            string mutex = @"Local\SSCapture.Tests." + Guid.NewGuid().ToString("N");
            SettingsCommandLine.Run(["--settings-import", import, "--check"], store, output, startup, mutex);
            Check(output.ToString().Contains("ON → OFF", StringComparison.Ordinal) && !Directory.Exists(Path.GetDirectoryName(target)),
                "事前確認で設定に書き込んだか差分を表示していない");
            SettingsCommandLine.Run(["--settings-import", import], store, output, startup, mutex);
            SettingsCommandLine.Run(["--settings-export", export], store, output, startup, mutex);
            Equal(20, SettingsTransfer.Read(export).GridStepPixels);
            Equal(0, startup.Writes);
            ExpectSettingsFailure<ArgumentException>(() => SettingsCommandLine.Run(["--settings-import", import, "--unknown"], store, output, startup, mutex));
        }));

        Run("設定CLIの常駐適用は応答を受け取りCLI側で二重に書き込まない", () => WithSettingsDirectory(directory =>
        {
            using var dispatcher = new Control();
            string name = "SSCapture.Tests." + Guid.NewGuid().ToString("N");
            string mutexName = @"Local\" + name;
            using var singleton = new Mutex(true, mutexName);
            string candidate = Path.Combine(directory, "candidate.json");
            File.WriteAllText(candidate, """{"GridStepPixels":42}""");
            var clientPath = Path.Combine(directory, "client.json");
            var serverPath = Path.Combine(directory, "server.json");
            int calls = 0;
            using var endpoint = new ManagementEndpoint(name, dispatcher, () => { }, () => { }, importSettings: json =>
            {
                calls++;
                new SettingsStore(serverPath).Import(SettingsTransfer.Parse(json));
                return null;
            });

            var task = Task.Run(() => SettingsCommandLine.Run(["--settings-import", candidate],
                new SettingsStore(clientPath), TextWriter.Null, new FakeStartup(), mutexName, name));
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted && deadline.Elapsed < TimeSpan.FromSeconds(10))
            {
                Application.DoEvents();
                Thread.Sleep(10);
            }
            Check(task.IsCompleted, "常駐側の応答がなかった");
            task.GetAwaiter().GetResult();
            Equal(1, calls);
            Check(!File.Exists(clientPath), "CLIが常駐側の処理に加えて設定を書き込んだ");
            Equal(42, new SettingsStore(serverPath).Load(out _).GridStepPixels);
            singleton.ReleaseMutex();
        }));

        Run("取込画面は確認と適用を区別し、入力パスの変更で候補を破棄する", () =>
        {
            int reads = 0;
            using var dialog = new SettingsTransferForm(_ => { reads++; return new AppSettings(); }, _ => "確認内容");
            _ = dialog.Handle;
            var path = dialog.Controls.Find("TransferPath", true).Single();
            var preview = (Button)dialog.Controls.Find("PreviewOrExportSettings", true).Single();
            var apply = (Button)dialog.Controls.Find("ApplySettingsImport", true).Single();
            var cancel = (Button)dialog.Controls.Find("CancelSettingsTransfer", true).Single();
            path.Text = @"C:\settings.json";
            Check(!apply.Enabled, "確認前に適用できる");
            RecordingClick(preview);
            Check(apply.Enabled && reads == 1 && dialog.Candidate is not null, "事前確認後に適用できない");
            path.Text = @"C:\other.json";
            Check(!apply.Enabled && dialog.Candidate is null, "パス変更後に古い候補を適用できる");
            RecordingClick(preview);
            RecordingClick(cancel);
            Equal(DialogResult.Cancel, dialog.DialogResult);
            Check(reads == 2, "取消前に再読込した");
        });
    }

    private static void WithSettingsDirectory(Action<string> test)
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("SSCapture.SettingsTests-");
        try { test(directory.FullName); }
        finally { directory.Delete(true); }
    }

    private static void ExpectSettingsFailure<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"{typeof(T).Name}が発生しませんでした。");
    }

    private sealed class FakePreparedHook : IDisposable
    {
        internal bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
}
