using System.Collections.Concurrent;
using System.Text;
using SSCapture.Services;

namespace SSCapture.Tests;

internal static partial class Program
{
    private static void SettingsEqual(AppSettings expected, AppSettings actual)
    {
        Check(expected.InstantShortcuts.SequenceEqual(actual.InstantShortcuts), "即時キー設定の往復不一致" );
        Equal(System.Text.Json.JsonSerializer.Serialize(expected.SavedRegions), System.Text.Json.JsonSerializer.Serialize(actual.SavedRegions));
        Equal(System.Text.Json.JsonSerializer.Serialize(expected.CapturePresets), System.Text.Json.JsonSerializer.Serialize(actual.CapturePresets));
        Equal(expected, actual with { InstantShortcuts = expected.InstantShortcuts, SavedRegions = expected.SavedRegions, CapturePresets = expected.CapturePresets });
    }
    static partial void ServiceTests()
    {
        Run("通知は撮影画面に1秒の既定値で旧設定を読み込み原本保持", () => WithServiceDirectory(directory =>
        {
            string path = Path.Combine(directory, "settings.json");
            string old = "{\"SchemaVersion\":2,\"FileNamePattern\":\"sample_{##}.png\"}";
            File.WriteAllText(path, old);
            var store = new SettingsStore(path); var restored = store.Load(out var warning);
            Equal<string?>(null, warning); Check(restored.ShowCaptureNotification && restored.PlayCaptureSound, "通知の初期値");
            Equal(NotificationDisplay.Captured, new AppSettings().NotificationDisplay);
            Equal(1, new AppSettings().NotificationDurationSeconds);
            Equal(NotificationDisplay.Captured, restored.NotificationDisplay);
            Equal(1, restored.NotificationDurationSeconds);
            Equal(NotificationCorner.BottomRight, restored.NotificationCorner); Equal(old, File.ReadAllText(path));
            var changed = restored with { NotificationDurationSeconds = 0, PlayCaptureSound = true, NotificationCorner = NotificationCorner.BottomLeft };
            store.Save(changed); SettingsEqual(changed, store.Load(out warning)); Equal<string?>(null, warning);
            Check(!changed.ShowCaptureNotification && changed.PlayCaptureSound, "0秒の非表示と効果音を独立して保持");
        }));
        Run("旧通知OFFは0秒へ移行し新しい秒数指定を優先する", () => WithServiceDirectory(directory =>
        {
            string path = Path.Combine(directory, "settings.json");
            var store = new SettingsStore(path);
            foreach ((string json, int expected) in new (string, int)[]
            {
                ("{\"ShowCaptureNotification\":false}", 0),
                ("{\"ShowCaptureNotification\":true}", 1),
                ("{\"ShowCaptureNotification\":false,\"NotificationDurationSeconds\":7}", 7),
                ("{\"ShowCaptureNotification\":true,\"NotificationDurationSeconds\":0}", 0),
                ("{\"NotificationDurationSeconds\":3600}", 3600)
            })
            {
                File.WriteAllText(path, json);
                var loaded = store.Load(out var warning);
                Equal<string?>(null, warning);
                Equal(expected, loaded.NotificationDurationSeconds);
                Equal(expected > 0, loaded.ShowCaptureNotification);
                Equal(json, File.ReadAllText(path));
            }
        }));
        Run("通知の全表示先と秒数を文字列enumで保存して往復", () => WithServiceDirectory(directory =>
        {
            string path = Path.Combine(directory, "settings.json");
            var store = new SettingsStore(path);
            Equal(12, Enum.GetValues<NotificationDisplay>().Length);
            foreach (var display in Enum.GetValues<NotificationDisplay>())
            {
                var settings = new AppSettings { NotificationDisplay = display, NotificationDurationSeconds = 12,
                    NotificationCorner = NotificationCorner.BottomLeft, PlayCaptureSound = false };
                store.Save(settings);
                SettingsEqual(settings, store.Load(out var warning));
                Equal<string?>(null, warning);
                using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                Equal(display.ToString(), document.RootElement.GetProperty("NotificationDisplay").GetString());
                Equal(12, document.RootElement.GetProperty("NotificationDurationSeconds").GetInt32());
                Check(!document.RootElement.TryGetProperty("ShowCaptureNotification", out _), "旧表示フラグを再保存しない");
            }
        }));
        Run("通知の未知enumと不正秒数は拒否し破損設定を保持", () => WithServiceDirectory(directory =>
        {
            foreach (var invalid in new[]
            {
                new AppSettings { NotificationCorner = (NotificationCorner)99 },
                new AppSettings { NotificationDisplay = (NotificationDisplay)99 },
                new AppSettings { NotificationDurationSeconds = -1 },
                new AppSettings { NotificationDurationSeconds = 3601 }
            })
            {
                bool rejected = false;
                try { invalid.Validate(); } catch (ArgumentException) { rejected = true; }
                Check(rejected, "不正な通知設定を受理した");
            }
            foreach (int duration in new[] { 0, 1, 3600 }) new AppSettings { NotificationDurationSeconds = duration }.Validate();
            string path = Path.Combine(directory, "settings.json");
            var store = new SettingsStore(path);
            foreach (string json in new[] { "{\"NotificationDisplay\":99}", "{\"NotificationDisplay\":\"missing\"}",
                "{\"NotificationDurationSeconds\":-1}", "{\"NotificationDurationSeconds\":3601}", "{\"NotificationDurationSeconds\":1.5}" })
            {
                File.WriteAllText(path, json);
                SettingsEqual(new AppSettings(), store.Load(out var warning));
                Check(!string.IsNullOrWhiteSpace(warning), "不正通知設定の警告なし");
                Equal(json, File.ReadAllText(path));
            }
        }));
        Run("固定サイズ基点4隅を保存し旧設定の左上既定値と不正値拒否を維持", () => WithServiceDirectory(directory =>
        {
            string path = Path.Combine(directory, "settings.json");
            var store = new SettingsStore(path);
            Equal(CaptureAnchor.TopLeft, new AppSettings().FixedCaptureAnchor);
            File.WriteAllText(path, "{}");
            Equal(CaptureAnchor.TopLeft, store.Load(out var warning).FixedCaptureAnchor);
            Equal<string?>(null, warning); Equal("{}", File.ReadAllText(path));
            foreach (var anchor in Enum.GetValues<CaptureAnchor>())
            {
                var settings = new AppSettings { FixedCaptureAnchor = anchor };
                store.Save(settings);
                SettingsEqual(settings, store.Load(out warning));
                Equal<string?>(null, warning);
                using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                Equal(anchor.ToString(), document.RootElement.GetProperty("FixedCaptureAnchor").GetString());
            }
            bool rejected = false;
            try { new AppSettings { FixedCaptureAnchor = (CaptureAnchor)99 }.Validate(); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "不正基点を受理した");
            const string invalidJson = "{\"FixedCaptureAnchor\":99}";
            File.WriteAllText(path, invalidJson);
            SettingsEqual(new AppSettings(), store.Load(out warning));
            Check(!string.IsNullOrWhiteSpace(warning), "不正基点の警告なし");
            Equal(invalidJson, File.ReadAllText(path));
        }));
        Run("Win/Shift左右の組合せでS初回だけ発火", () =>
        {
            foreach (int win in new[] { 0x5B, 0x5C })
            foreach (int shift in new[] { 0xA0, 0xA1 })
            {
                var state = new HotkeyState();
                Equal(default, state.Process(win, true, true));
                Equal(default, state.Process(shift, true, true));
                Equal(new HotkeyDecision(true, true), state.Process(0x53, true, true));
                Equal(new HotkeyDecision(true, false), state.Process(0x53, true, true));
                Equal(new HotkeyDecision(true, false), state.Process(0x53, false, true));
                Equal(new HotkeyDecision(true, true), state.Process(0x53, true, true));
            }
        });
        Run("左右Shiftの一方を離しても他方の押下は有効", () =>
        {
            var state = new HotkeyState();
            state.Process(0x5B, true, true);
            state.Process(0xA0, true, true);
            state.Process(0xA1, true, true);
            state.Process(0xA0, false, true);
            Equal(new HotkeyDecision(true, true), state.Process(0x53, true, true));
        });
        Run("修飾キー先離し・途中停止でも抑止済Sのrepeat/upを吸収", () =>
        {
            var state = new HotkeyState(KeyboardModifiers.LeftWindows | KeyboardModifiers.LeftShift);
            Equal(new HotkeyDecision(true, true), state.Process(0x53, true, true));
            state.Process(0x5B, false, false);
            state.Process(0xA0, false, false);
            Equal(new HotkeyDecision(true, false), state.Process(0x53, true, false));
            Equal(new HotkeyDecision(true, false), state.Process(0x53, false, false));
            Equal(default, state.Process(0x53, true, true));
            Equal(default, state.Process(0x53, false, true));
        });
        Run("通常S押しっぱなしの途中から修飾キーを追加しても発火しない", () =>
        {
            var state = new HotkeyState();
            Equal(default, state.Process(0x53, true, true));
            state.Process(0x5C, true, true);
            state.Process(0xA1, true, true);
            Equal(default, state.Process(0x53, true, true));
            Equal(default, state.Process(0x53, false, true));
            Equal(new HotkeyDecision(true, true), state.Process(0x53, true, true));
        });
        Run("Ctrl/Alt付き・他キー・一時停止は通過", () =>
        {
            foreach (int extra in new[] { 0xA2, 0xA3, 0x11, 0xA4, 0xA5, 0x12 })
            {
                var state = new HotkeyState(KeyboardModifiers.RightWindows | KeyboardModifiers.LeftShift);
                state.Process(extra, true, true);
                Equal(default, state.Process(0x53, true, true));
                state.Process(extra, false, true);
                Equal(default, state.Process(0x53, true, true));
                Equal(default, state.Process(0x53, false, true));
                Equal(default, state.Process(0x41, true, true));
                Equal(default, state.Process(0x53, true, false));
                Equal(default, state.Process(0x53, false, false));
                Equal(new HotkeyDecision(true, true), state.Process(0x53, true, true));
            }
        });
        Run("監視開始前に押されていたSはrepeatで撮影しない", () =>
        {
            var state = new HotkeyState(KeyboardModifiers.LeftWindows | KeyboardModifiers.RightShift, true);
            Equal(default, state.Process(0x53, true, true));
            Equal(default, state.Process(0x53, false, true));
            Equal(new HotkeyDecision(true, true), state.Process(0x53, true, true));
        });
        Run("欠落した修飾key-upを同期で復旧・S押下状態は維持", () =>
        {
            var state = new HotkeyState(KeyboardModifiers.LeftWindows | KeyboardModifiers.RightShift);
            state.SynchronizeModifiers(KeyboardModifiers.None);
            Equal(default, state.Process(0x53, true, true));
            state.SynchronizeModifiers(KeyboardModifiers.RightWindows | KeyboardModifiers.LeftShift);
            Equal(default, state.Process(0x53, true, true));
            Equal(default, state.Process(0x53, false, true));
            Equal(new HotkeyDecision(true, true), state.Process(0x53, true, true));
            state.SynchronizeModifiers(KeyboardModifiers.None);
            Equal(new HotkeyDecision(true, false), state.Process(0x53, false, true));
        });
        Run("4枠キー指定を正規化・空欄は未割当", () =>
        {
            Equal("Win+Shift+S", HotkeyBinding.Parse(" shift + windows + s ")!.Value.ToString());
            Equal("Ctrl+Alt+F8", HotkeyBinding.Parse("alt+control+f8")!.Value.ToString());
            Equal("PrintScreen", HotkeyBinding.Parse("PrtSc")!.Value.ToString());
            Equal(0x30, HotkeyBinding.Parse("Ctrl+0")!.Value.VirtualKey);
            Equal(0x60, HotkeyBinding.Parse("NumPad0")!.Value.VirtualKey);
            Equal(0xBB, HotkeyBinding.Parse("Ctrl+Plus")!.Value.VirtualKey);
            Equal<HotkeyBinding?>(null, HotkeyBinding.Parse("  "));
            Equal<HotkeyBinding?>(null, HotkeyBinding.Parse(null));
            HotkeyAssignment[] defaults = HotkeyBinding.ReadAssignments(new AppSettings());
            Equal(14, defaults.Length);
            Equal(13, defaults.Count(item => item.Intent == CaptureIntent.Screenshot));
            Equal(1, defaults.Count(item => item.Intent == CaptureIntent.Recording));
        });
        Run("不正キー・Escape・修飾単独・重複修飾を拒否", () =>
        {
            foreach (string invalid in new[] { "Win", "Ctrl+Shift", "Escape", "Win+Esc", "Ctrl+Control+K", "Win+Windows+S", "Ctrl++S", "Ctrl+", "S+R", "F25", "UnknownKey", "123", "S,R", "LButton", "ControlKey", "LWin", "Packet", "ProcessKey" })
            {
                bool rejected = false;
                try { HotkeyBinding.Parse(invalid); }
                catch (ArgumentException) { rejected = true; }
                Check(rejected, $"不正な指定を受け付けた: {invalid}");
            }
        });
        Run("全4枠の重複は順序・別名にかかわらず拒否", () =>
        {
            string[] unique = ["Win+Shift+S", "PrintScreen", "Win+Shift+R", "Ctrl+Alt+F8"];
            for (int first = 0; first < unique.Length; first++)
            for (int second = first + 1; second < unique.Length; second++)
            {
                string[] values = (string[])unique.Clone();
                values[second] = values[first].ToLowerInvariant();
                var settings = new AppSettings { ScreenshotShortcut1 = values[0], ScreenshotShortcut2 = values[1], RecordingShortcut1 = values[2], RecordingShortcut2 = values[3] };
                bool rejected = false;
                try { HotkeyBinding.ValidateSettings(settings); }
                catch (ArgumentException) { rejected = true; }
                Check(rejected, $"キー枠{first}/{second}の重複を受け付けた");
            }
            bool aliasRejected = false;
            try { HotkeyBinding.ValidateSettings(new AppSettings { RecordingShortcut2 = "shift+windows+s" }); }
            catch (ArgumentException) { aliasRejected = true; }
            Check(aliasRejected, "修飾順序と別名の重複を拒否する");
            HotkeyBinding.ValidateSettings(new AppSettings { ScreenshotShortcut1 = "", ScreenshotShortcut2 = "", RecordingShortcut1 = "", RecordingShortcut2 = "" });
        });
        Run("任意4枠は静止画と録画を独立 dispatch", () =>
        {
            var settings = new AppSettings { ScreenshotShortcut1 = "Ctrl+Alt+F8", ScreenshotShortcut2 = "Shift+Insert", RecordingShortcut1 = "Win+R", RecordingShortcut2 = "Alt+PrintScreen" };
            var state = new HotkeyState(HotkeyBinding.ReadAssignments(settings));
            (int Key, KeyboardModifiers Mods, CaptureIntent Intent)[] cases =
            [
                (0x77, KeyboardModifiers.RightControl | KeyboardModifiers.LeftAlt, CaptureIntent.Screenshot),
                (0x2D, KeyboardModifiers.RightShift, CaptureIntent.Screenshot),
                (0x52, KeyboardModifiers.RightWindows, CaptureIntent.Recording),
                (0x2C, KeyboardModifiers.RightAlt, CaptureIntent.Recording)
            ];
            foreach (var (key, modifiers, intent) in cases)
            {
                state.SynchronizeModifiers(modifiers);
                Equal(new HotkeyDecision(true, true, intent), state.Process(key, true, true));
                state.SynchronizeModifiers(KeyboardModifiers.None);
                Equal(new HotkeyDecision(true, false, intent), state.Process(key, true, false));
                Equal(new HotkeyDecision(true, false, intent), state.Process(key, false, false));
            }
            Equal(default, state.Process(0x53, true, true));
            Equal(default, state.Process(0x53, false, true));
            Equal(default, state.Process(0x2C, true, true));
            Equal(default, state.Process(0x2C, false, true));
        });
        Run("WinShift SとRの押下状態・動作を混同しない", () =>
        {
            var state = new HotkeyState(HotkeyBinding.ReadAssignments(new AppSettings()), KeyboardModifiers.LeftWindows | KeyboardModifiers.RightShift);
            Equal(new HotkeyDecision(true, true, CaptureIntent.Screenshot), state.Process(0x53, true, true));
            Equal(new HotkeyDecision(true, true, CaptureIntent.Recording), state.Process(0x52, true, true));
            Equal(new HotkeyDecision(true, false, CaptureIntent.Screenshot), state.Process(0x53, true, true));
            Equal(new HotkeyDecision(true, false, CaptureIntent.Recording), state.Process(0x52, true, true));
            state.SynchronizeModifiers(KeyboardModifiers.None);
            Equal(new HotkeyDecision(true, false, CaptureIntent.Recording), state.Process(0x52, false, false));
            Equal(new HotkeyDecision(true, false, CaptureIntent.Screenshot), state.Process(0x53, false, false));
            state.SynchronizeModifiers(KeyboardModifiers.LeftWindows | KeyboardModifiers.LeftShift | KeyboardModifiers.RightControl);
            Equal(default, state.Process(0x52, true, true));
            Equal(default, state.Process(0x52, false, true));
        });
        Run("同じ通常キーへの異なる修飾割当もrepeatでは切り替わらない", () =>
        {
            var settings = new AppSettings { ScreenshotShortcut1 = "Ctrl+K", ScreenshotShortcut2 = "", RecordingShortcut1 = "Alt+K", RecordingShortcut2 = "" };
            var state = new HotkeyState(HotkeyBinding.ReadAssignments(settings), KeyboardModifiers.LeftControl);
            Equal(new HotkeyDecision(true, true, CaptureIntent.Screenshot), state.Process(0x4B, true, true));
            state.SynchronizeModifiers(KeyboardModifiers.RightAlt);
            Equal(new HotkeyDecision(true, false, CaptureIntent.Screenshot), state.Process(0x4B, true, true));
            Equal(new HotkeyDecision(true, false, CaptureIntent.Screenshot), state.Process(0x4B, false, true));
            Equal(new HotkeyDecision(true, true, CaptureIntent.Recording), state.Process(0x4B, true, true));
        });
        Run("PrintScreen通常down/repeat/upとkeyupのみ経路", () =>
        {
            var bindings = HotkeyBinding.ReadAssignments(new AppSettings());
            var state = new HotkeyState(bindings);
            Equal(new HotkeyDecision(true, true), state.Process(0x2C, true, true));
            Equal(new HotkeyDecision(true, false), state.Process(0x2C, true, true));
            Equal(new HotkeyDecision(true, false), state.Process(0x2C, false, true));
            var releaseOnly = new HotkeyState(bindings);
            Equal(new HotkeyDecision(true, true), releaseOnly.Process(0x2C, false, true));
            Equal(default, releaseOnly.Process(0x2C, false, false));
            var heldAtStartup = new HotkeyState(bindings, KeyboardModifiers.None, [0x2C, 0x52]);
            Equal(default, heldAtStartup.Process(0x2C, false, true));
            heldAtStartup.SynchronizeModifiers(KeyboardModifiers.LeftWindows | KeyboardModifiers.LeftShift);
            Equal(default, heldAtStartup.Process(0x52, true, true));
            Equal(default, heldAtStartup.Process(0x52, false, true));
            Equal(new HotkeyDecision(true, true, CaptureIntent.Recording), heldAtStartup.Process(0x52, true, true));
        });
        Run("PrintScreenはAlt/Win/Ctrl/Shift付きと単押しを区別", () =>
        {
            foreach (KeyboardModifiers modifier in new[] { KeyboardModifiers.LeftAlt, KeyboardModifiers.RightWindows, KeyboardModifiers.LeftControl, KeyboardModifiers.RightShift })
            {
                var state = new HotkeyState(HotkeyBinding.ReadAssignments(new AppSettings()), modifier);
                Equal(default, state.Process(0x2C, false, true));
                Equal(default, state.Process(0x2C, true, true));
                state.SynchronizeModifiers(KeyboardModifiers.None);
                Equal(default, state.Process(0x2C, true, true));
                Equal(default, state.Process(0x2C, false, true));
                Equal(new HotkeyDecision(true, true), state.Process(0x2C, true, true));
            }
        });
        Run("空欄4枠は無効・旧hook constructorのAPIを保持", () =>
        {
            var settings = new AppSettings { ScreenshotShortcut1 = "", ScreenshotShortcut2 = "", RecordingShortcut1 = "", RecordingShortcut2 = "", LastRegionShortcut = "" };
            var state = new HotkeyState(HotkeyBinding.ReadAssignments(settings), KeyboardModifiers.LeftWindows | KeyboardModifiers.LeftShift);
            foreach (int key in new[] { 0x53, 0x52, 0x2C })
            {
                Check(!state.IsTargetKey(key), "未割当キーを監視しない");
                Equal(default, state.Process(key, true, true));
                Equal(default, state.Process(key, false, true));
            }
            Check(typeof(KeyboardHook).GetConstructor([typeof(Action)]) is not null, "互換constructorが必要");
            Check(typeof(KeyboardHook).GetConstructor([typeof(Action<CaptureIntent>), typeof(AppSettings)]) is not null, "設定可能constructorが必要");
        });
        Run("設定UTF-8往復・更新", () => WithServiceDirectory(directory =>
        {
            string path = Path.Combine(directory, "設定", "settings.json");
            var store = new SettingsStore(path);
            SettingsEqual(new AppSettings(), store.Load(out string? initialWarning));
            Equal<string?>(null, initialWarning);
            var settings = new AppSettings { SaveDirectory = Path.Combine(directory, "撮影画像"), DefaultMode = CaptureMode.Freeform, FreezeFrame = false,
                ScreenshotShortcut1 = "Ctrl+Alt+F8", ScreenshotShortcut2 = "Shift+Insert", RecordingShortcut1 = "Win+R", RecordingShortcut2 = "Alt+PrintScreen" };
            store.Save(settings);
            SettingsEqual(settings, store.Load(out string? warning));
            Equal<string?>(null, warning);
            var changed = settings with { DefaultMode = CaptureMode.FullScreen, FreezeFrame = true };
            store.Save(changed);
            SettingsEqual(changed, store.Load(out warning));
            Equal<string?>(null, warning);
            Equal(1, Directory.GetFiles(Path.GetDirectoryName(path)!).Length);
        }));
        Run("旧設定の一括無効を4枠なしに移行", () => WithServiceDirectory(directory =>
        {
            string path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, "{\"DefaultMode\":\"Window\",\"ShortcutEnabled\":false}");
            var settings = new SettingsStore(path).Load(out string? warning);
            Equal<string?>(null, warning);
            Equal(CaptureMode.Window, settings.DefaultMode);
            Equal(false, settings.HasShortcuts);
            Equal("", settings.ScreenshotShortcut1);
            Equal("", settings.ScreenshotShortcut2);
            Equal("", settings.RecordingShortcut1);
            Equal("", settings.RecordingShortcut2);
            Equal(true, settings.FreezeFrame);
        }));
        Run("旧数値と文字列の送信先を独立フラグへ移行し再保存", () => WithServiceDirectory(directory =>
        {
            string path = Path.Combine(directory, "settings.json");
            var store = new SettingsStore(path);
            string[] names = ["Clipboard", "File", "ExternalApp"];
            OutputDestination[] values = [OutputDestination.Clipboard, OutputDestination.File, OutputDestination.ExternalApp];
            for (int i = 0; i < 3; i++)
            {
                foreach (string value in new[] { i.ToString(), "\"" + names[i] + "\"" })
                {
                    File.WriteAllText(path, "{\"DefaultDestination\":" + value + "}");
                    var migrated = store.Load(out var warning);
                    Equal<string?>(null, warning);
                    Equal(values[i], migrated.DefaultDestination);
                    Equal(true, migrated.HasShortcuts);
                    store.Save(migrated with { DefaultDestination = (OutputDestination)7 });
                    Equal((OutputDestination)7, store.Load(out warning).DefaultDestination);
                    Equal<string?>(null, warning);
                }
            }
        }));
        Run("破損設定・不正enum/pathは既定値で起動し原本保持", () => WithServiceDirectory(directory =>
        {
            string path = Path.Combine(directory, "settings.json");
            var store = new SettingsStore(path);
            foreach (string content in new[] { "{破損", "null", "{\"DefaultMode\":567}", "{\"SaveDirectory\":\"relative\"}", "{\"SaveDirectory\":null}", "{\"SaveDirectory\":\"C:\\\\bad*path\"}",
                "{\"ScreenshotShortcut1\":\"Escape\"}", "{\"RecordingShortcut2\":\"shift+win+s\"}", "{\"RecordingShortcut1\":\"Ctrl+Ctrl+R\"}" })
            {
                byte[] bytes = Encoding.UTF8.GetBytes(content);
                File.WriteAllBytes(path, bytes);
                SettingsEqual(new AppSettings(), store.Load(out string? warning));
                Check(!string.IsNullOrWhiteSpace(warning), "破損設定は警告が必要");
                Check(bytes.SequenceEqual(File.ReadAllBytes(path)), "元の破損設定を変更しない");
            }
        }));
        Run("不正設定保存と保存失敗は既存設定を保持", () => WithServiceDirectory(directory =>
        {
            string path = Path.Combine(directory, "settings.json");
            var store = new SettingsStore(path);
            var settings = new AppSettings { SaveDirectory = directory };
            store.Save(settings);
            byte[] before = File.ReadAllBytes(path);
            bool rejected = false;
            try { store.Save(settings with { DefaultMode = (CaptureMode)9 }); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "不正enumの保存を拒否する");
            rejected = false;
            try { store.Save(settings with { RecordingShortcut2 = "PrintScreen" }); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "重複キーの保存を拒否する");
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                rejected = false;
                try { store.Save(settings with { FreezeFrame = false }); }
                catch (IOException) { rejected = true; }
                Check(rejected, "使用中の設定は置き換えない");
            }
            Check(before.SequenceEqual(File.ReadAllBytes(path)), "失敗前の設定ファイルを保持する");
            Equal(1, Directory.GetFiles(directory).Length);
        }));
        Run("同時刻PNG32並列保存・既存画像非上書き・内容保持", () => WithServiceDirectory(directory =>
        {
            var timestamp = new DateTimeOffset(2026, 10, 10, 12, 34, 56, 789, TimeSpan.FromHours(9));
            using var original = new Bitmap(3, 2);
            original.SetPixel(0, 0, Color.FromArgb(128, 1, 2, 3));
            string first = CaptureStorage.SavePng(original, directory, timestamp);
            Equal("20261010_123456_789.png", Path.GetFileName(first));
            byte[] originalBytes = File.ReadAllBytes(first);
            var results = new ConcurrentBag<(string Path, int Color)>();
            Parallel.For(0, 32, index =>
            {
                using var bitmap = new Bitmap(3, 2);
                Color color = Color.FromArgb(255, index + 20, index + 50, index + 80);
                bitmap.SetPixel(1, 1, color);
                results.Add((CaptureStorage.SavePng(bitmap, directory, timestamp), color.ToArgb()));
            });
            Equal(32, results.Select(result => result.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Equal(33, Directory.GetFiles(directory, "*.png").Length);
            Equal(33, Directory.GetFiles(directory).Length);
            Check(originalBytes.SequenceEqual(File.ReadAllBytes(first)), "既存PNGが変更された");
            foreach (var result in results)
            {
                using var readBack = new Bitmap(result.Path);
                Equal(3, readBack.Width);
                Equal(2, readBack.Height);
                Equal(result.Color, readBack.GetPixel(1, 1).ToArgb());
            }
            using var transparent = new Bitmap(first);
            Equal(original.GetPixel(0, 0).ToArgb(), transparent.GetPixel(0, 0).ToArgb());
        }));
        Run("PNG生成失敗時は自分の一時ファイルだけ除去", () => WithServiceDirectory(directory =>
        {
            string keep = Path.Combine(directory, "keep.txt");
            File.WriteAllText(keep, "keep");
            var disposed = new Bitmap(2, 2);
            disposed.Dispose();
            bool rejected = false;
            try { CaptureStorage.SavePng(disposed, directory, DateTimeOffset.Now); }
            catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.ExternalException) { rejected = true; }
            Check(rejected, "破棄済画像を保存しない");
            Equal("keep", File.ReadAllText(keep));
            Equal(1, Directory.GetFiles(directory).Length);
        }));
    }

    private static void WithServiceDirectory(Action<string> test)
    {
        string root = Path.GetFullPath(Path.GetTempPath());
        string directory = Path.GetFullPath(Path.Combine(root, $"SSCapture-ServiceTests-{Guid.NewGuid():N}"));
        Check(directory.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "テスト先は一時フォルダ内であること");
        Directory.CreateDirectory(directory);
        try { test(directory); }
        finally { Directory.Delete(directory, true); }
    }
}
