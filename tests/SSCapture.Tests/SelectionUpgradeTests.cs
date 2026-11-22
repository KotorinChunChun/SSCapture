using SSCapture.Capture;
using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture.Tests;

internal static partial class Program
{
    private static void SelectionUpgradeTests()
    {
        Run("撮影前とドラッグ中のキー設定・クリック設定を独立して保存できる", () => WithServiceDirectory(directory =>
        {
            var map = new CaptureKeyMap(Ctrl: CaptureKeyAction.Snap, Alt: CaptureKeyAction.Grid, CtrlAlt: CaptureKeyAction.LockHeight, SpaceEnabled: false);
            var beforeMap = CaptureKeyMap.BeforeDefaults with { CtrlAlt = CaptureKeyAction.CrossMonitor };
            var mouse = new CaptureMouseMap(BeforeLeftAction: CaptureClickAction.None, BeforeMiddleAction: CaptureClickAction.Menu, DragRightAction: CaptureClickAction.None);
            using var editor = new CaptureKeyEditor(map, mouse, beforeSelection: false);
            Equal(10, CaptureKeyMap.Combinations.Distinct().Count());
            Equal(map, editor.Value);
            var primary = (ComboBox)editor.Controls.Find("CaptureKeys5", true).Single();
            var mirror = (ComboBox)editor.Controls.Find("CaptureKeys5Mirror", true).Single();
            primary.SelectedIndex = (int)CaptureKeyAction.CrossMonitor;
            Equal(primary.SelectedIndex, mirror.SelectedIndex);
            Check(!mirror.Enabled, "重複セルが編集可能");
            primary.SelectedIndex = (int)CaptureKeyAction.Snap;
            Equal(primary.SelectedIndex, mirror.SelectedIndex);
            var store = new SettingsStore(Path.Combine(directory, "settings.json"));
            store.Save(new AppSettings { CaptureKeys = editor.Value, CaptureBeforeKeys = beforeMap, CaptureMouse = mouse });
            var loaded = store.Load(out string? warning);
            Check(warning is null, warning ?? "");
            Equal(map with { CtrlAlt = CaptureKeyAction.Snap }, loaded.CaptureKeys);
            Equal(beforeMap, loaded.CaptureBeforeKeys);
            Equal(mouse, loaded.CaptureMouse);
            Equal(CaptureKeyAction.None, map.Resolve(CaptureModifiers.Ctrl | CaptureModifiers.Alt | CaptureModifiers.Shift));
        }));
        Run("旧HighlightWindowを読み込み、撮影前とクリックに初期値を補いドラッグ編集で正規化する", () => WithServiceDirectory(directory =>
        {
            string path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, """
                { "SchemaVersion": 3, "CaptureKeys": { "Ctrl": "Snap", "Win": "HighlightWindow",
                  "CtrlShift": "HighlightWindow", "CtrlAlt": "LockHeight", "AltWin": "SwitchAnchor", "SpaceEnabled": false },
                  "CaptureMouse": { "BeforeMiddle": true, "BeforeRight": false } }
                """);
            var store = new SettingsStore(path);
            var loaded = store.Load(out string? warning);
            Check(warning is null, warning ?? "");
            Equal(CaptureKeyAction.HighlightWindow, loaded.CaptureKeys.Win);
            Equal(CaptureKeyAction.HighlightWindow, loaded.CaptureKeys.CtrlShift);
            Equal(CaptureKeyAction.CrossMonitor, new AppSettings().CaptureKeys.Shift);
            foreach (var keys in CaptureKeyMap.Combinations)
                Equal(keys switch { CaptureModifiers.Win => CaptureKeyAction.HighlightWindowPush, CaptureModifiers.Alt => CaptureKeyAction.Snap,
                    CaptureModifiers.Ctrl => CaptureKeyAction.SelectControlPush, CaptureModifiers.Shift => CaptureKeyAction.SelectDesktop,
                    _ => CaptureKeyAction.None }, loaded.CaptureBeforeKeys.Resolve(keys));
            Check(!loaded.CaptureBeforeKeys.SpaceEnabled, "撮影範囲設定前にSpaceが有効");
            Equal(new CaptureMouseMap(BeforeRightAction: CaptureClickAction.None, BeforeMiddleAction: CaptureClickAction.Desktop), loaded.CaptureMouse);

            using var editor = new CaptureKeyEditor(loaded.CaptureKeys, loaded.CaptureMouse, beforeSelection: false);
            var expected = loaded.CaptureKeys with { Win = CaptureKeyAction.None, CtrlShift = CaptureKeyAction.None };
            store.Save(loaded with { CaptureKeys = editor.Value });
            Equal(expected, store.Load(out _).CaptureKeys);
        }));
        Run("既定の撮影キーを外部ファイルから読み、保存済み割り当てを保持する", () => WithServiceDirectory(directory =>
        {
            var bundled = CaptureKeyDefaults.Current.Drag;
            Equal(CaptureKeyAction.FromCenter, bundled.Ctrl);
            Equal(CaptureKeyAction.CrossMonitor, bundled.Shift);
            Equal(CaptureKeyAction.LockAspectRatio, bundled.Win);
            Equal(CaptureKeyAction.SelectDesktop, CaptureKeyDefaults.Current.Before.Shift);
            Equal(CaptureClickAction.ToggleMagnifier, new AppSettings().CaptureMouse.BeforeMiddleAction);
            Equal(CaptureClickAction.ToggleMagnifier, new AppSettings().CaptureMouse.DragMiddleAction);
            Equal(CaptureKeyAction.FromCenterWithAspectRatio, bundled.CtrlShift);
            Equal(CaptureKeyAction.LockWidth, bundled.CtrlAlt);
            Equal(CaptureKeyAction.LockHeight, bundled.ShiftAlt);
            Equal(CaptureKeyAction.Grid, bundled.Resolve(CaptureModifiers.Win | CaptureModifiers.Alt));
            Equal(CaptureKeyAction.None, CaptureKeyDefaults.Current.Before.Resolve(CaptureModifiers.Win | CaptureModifiers.Alt));
            string data = Directory.CreateDirectory(Path.Combine(directory, "data")).FullName;
            File.WriteAllText(Path.Combine(data, CaptureKeyDefaults.FileName), """{"Drag":{"Win":"Snap"},"Before":{"Win":"HighlightWindow"}}""");
            Equal(CaptureKeyAction.Snap, CaptureKeyDefaults.Load(directory).Drag.Win);
            File.WriteAllText(Path.Combine(data, CaptureKeyDefaults.FileName), """{"Drag":{"Win":"Invalid"},"Before":{}}""");
            Equal(bundled, CaptureKeyDefaults.Load(directory).Drag);
            var saved = new SettingsStore(Path.Combine(directory, "settings.json"));
            saved.Save(new AppSettings { CaptureKeys = bundled with { Win = CaptureKeyAction.None } });
            Equal(CaptureKeyAction.None, saved.Load(out _).CaptureKeys.Win);
        }));
        Run("Winは直前の縦横比、Ctrlは起点を中心、複合と離した後も正しい", () =>
        {
            Rectangle desktop = new(-300, -200, 600, 400);
            var gesture = new SelectionGesture(CaptureMode.Rectangle, [desktop], []);
            gesture.Begin(new(-100, -80), new(8, 8));
            gesture.Move(new(0, -30));
            gesture.Move(new(20, 40), action: CaptureKeyAction.LockAspectRatio);
            Equal(new Rectangle(-100, -80, 240, 120), gesture.Selection);
            gesture.Move(new(20, 40), action: CaptureKeyAction.FromCenterWithAspectRatio);
            Equal(new Rectangle(-300, -180, 400, 200), gesture.Selection);
            gesture.Move(new(20, 40), action: CaptureKeyAction.FromCenter);
            Equal(new Rectangle(-220, -200, 240, 240), gesture.Selection);
            Equal(new Rectangle(-100, -80, 120, 120), gesture.End(new(20, 40), OutputDestination.File)!.Bounds);
            gesture.Begin(new(0, 0), new(8, 8), action: CaptureKeyAction.FromCenterWithAspectRatio);
            gesture.Move(new(-40, -25), action: CaptureKeyAction.FromCenterWithAspectRatio);
            Equal(new Rectangle(-40, -40, 80, 80), gesture.Selection);
        });
        Run("撮影前のWin単押しでウィンドウ撮影に切り替え、再度押すと元のモードに戻る", () =>
        {
            Rectangle monitor = new(0, 0, 600, 400);
            Rectangle front = new(100, 100, 140, 160), back = new(10, 10, 300, 300);
            var snapshot = new DesktopSnapshot(new Bitmap(600, 400), monitor, [monitor]);
            var session = new SessionController(new(CaptureMode.Rectangle, CaptureIntent.Screenshot, OutputDestination.Clipboard,
                LimitToStartingMonitor: false, OutputSettings: new() { WindowActivation = CaptureActivation.Toggle,
                    CaptureBeforeKeys = new(Win: CaptureKeyAction.HighlightWindow) }), snapshot, [front, back], findWindow: _ => null);
            try
            {
                session.ApplyPointerInput(new(110, 110), CaptureModifiers.Win);
                Equal(CaptureMode.Rectangle, session.Mode);
                session.ApplyPointerInput(new(110, 110), CaptureModifiers.None, windowsTap: true);
                Equal(CaptureMode.Window, session.Mode);
                Check(session.SelectionMenu.Command("IsolatedWindowCapture").Checked, "Winで開始したウィンドウ撮影がWGCにならない");
                Check(session.HighlightWindow, "ウィンドウ撮影で対象が強調されない");
                Equal(Rectangle.Empty, session.HighlightedWindow); // Without a WGC frame the desktop crop stays dimmed.
                session.ApplyPointerInput(new(20, 20), CaptureModifiers.Ctrl);
                Equal<string?>(null, session.ModifierHint);
                Equal(Rectangle.Empty, session.HighlightedWindow);
                session.ApplyPointerInput(new(20, 20), CaptureModifiers.None, windowsTap: true);
                Equal(CaptureMode.Rectangle, session.Mode);
                Equal(Rectangle.Empty, session.HighlightedWindow);
                Check(session.ModifierHint is null, "修飾キーを離しても効果が表示される");
                session.ChangeMode(CaptureMode.Window);
                Check(session.SelectionMenu.Command("VisibleWindowCapture").Checked, "Winでの撮影方式が通常のモード切替に持ち越された");
                Equal(back, session.HighlightedWindow);
            }
            finally { session.Cancel(); }
        });
        Run("選択前のCtrlとWinは既定で保持中のみ有効でグリッドは20pxのOFF", () =>
        {
            var settings = new AppSettings();
            Equal(CaptureActivation.Push, settings.ControlActivation);
            Equal(CaptureActivation.Push, settings.WindowActivation);
            Equal(20, settings.GridStepPixels);
            Check(!settings.Grid20, "初期状態でグリッドがON");
            Rectangle monitor = new(0, 0, 400, 300);
            var session = new SessionController(new(CaptureMode.FixedRegion, CaptureIntent.Screenshot, OutputDestination.None,
                FixedSize: new(100, 80)), new DesktopSnapshot(new Bitmap(400, 300), monitor, [monitor]), [], findWindow: _ => null);
            try
            {
                Point point = new(80, 60);
                session.ApplyPointerInput(point, CaptureModifiers.Ctrl);
                Check(session.SelectingControl && session.UsesControlPreview, "Ctrl押下でコントロール選択に入らない");
                session.ApplyPointerInput(point, CaptureModifiers.None);
                Check(!session.SelectingControl, "Ctrlを離してもモードが戻らない");
                session.ApplyPointerInput(point, CaptureModifiers.Win);
                Equal(CaptureMode.Window, session.Mode);
                Check(session.SelectionMenu.Command("IsolatedWindowCapture").Checked, "Win押下でWGCを選択しない");
                session.ApplyPointerInput(point, CaptureModifiers.None, windowsTap: true);
                Equal(CaptureMode.FixedRegion, session.Mode);
                Equal(new Rectangle(80, 60, 100, 80), session.Selection);
            }
            finally { session.Cancel(); }
        });
        Run("Ctrlトグルは単押しの解放で切り替わり、組み合わせでは開始しない", () =>
        {
            Rectangle monitor = new(0, 0, 400, 300);
            var session = new SessionController(new(CaptureMode.Rectangle, CaptureIntent.Screenshot, OutputDestination.None,
                OutputSettings: new() { CaptureBeforeKeys = new(Ctrl: CaptureKeyAction.SelectControlToggle) }),
                new DesktopSnapshot(new Bitmap(400, 300), monitor, [monitor]), []);
            Point point = new(80, 60);
            try
            {
                session.ApplyPointerInput(point, CaptureModifiers.Ctrl);
                Check(!session.SelectingControl, "押した瞬間にトグルした");
                session.ApplyPointerInput(point, CaptureModifiers.Ctrl | CaptureModifiers.Shift);
                session.ApplyPointerInput(point, CaptureModifiers.None);
                Check(!session.SelectingControl, "Ctrl＋Shiftからトグルした");
                session.ApplyPointerInput(point, CaptureModifiers.Ctrl);
                session.ApplyPointerInput(point, CaptureModifiers.None);
                Check(session.SelectingControl, "Ctrl解放でトグルしない");
                session.ApplyPointerInput(point, CaptureModifiers.Ctrl);
                session.ApplyPointerInput(point, CaptureModifiers.None);
                Check(!session.SelectingControl, "次のCtrl解放で戻らない");
            }
            finally { session.Cancel(); }
        });
        Run("旧共通の動作をセルに移行し、明示されたプッシュと中クリック設定を保持する", () => WithServiceDirectory(directory =>
        {
            string path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, """
                {
                  "SchemaVersion": 3,
                  "ControlActivation": "Toggle",
                  "WindowActivation": "Push",
                  "CaptureBeforeKeys": { "Ctrl": "SelectControl", "CtrlAlt": "SelectControlPush", "Win": "HighlightWindow" },
                  "CaptureMouse": { "BeforeMiddleAction": "None", "DragMiddleAction": "Cancel" }
                }
                """);
            var store = new SettingsStore(path);
            var settings = store.Load(out var warning);
            Equal<string?>(null, warning);
            Equal(CaptureKeyAction.SelectControlToggle, settings.CaptureBeforeKeys.Ctrl);
            Equal(CaptureKeyAction.SelectControlPush, settings.CaptureBeforeKeys.CtrlAlt);
            Equal(CaptureKeyAction.HighlightWindowPush, settings.CaptureBeforeKeys.Win);
            Equal(CaptureClickAction.None, settings.CaptureMouse.BeforeMiddleAction);
            Equal(CaptureClickAction.Cancel, settings.CaptureMouse.DragMiddleAction);
            store.Save(settings);
            Equal(settings.CaptureBeforeKeys, store.Load(out _).CaptureBeforeKeys);
        }));
        Run("1点目では同じ機能のプッシュとトグルを併用し、2キーは両方を離してから切り替える", () =>
        {
            Rectangle monitor = new(0, 0, 400, 300);
            var session = new SessionController(new(CaptureMode.Rectangle, CaptureIntent.Screenshot, OutputDestination.None,
                OutputSettings: new()
                {
                    CaptureBeforeKeys = new(Ctrl: CaptureKeyAction.SelectControlPush, Win: CaptureKeyAction.HighlightWindowPush,
                        CtrlAlt: CaptureKeyAction.SelectControlToggle, ShiftWin: CaptureKeyAction.HighlightWindowToggle)
                }), new DesktopSnapshot(new Bitmap(400, 300), monitor, [monitor]), [], findWindow: _ => null);
            try
            {
                Point point = new(70, 65);
                void Input(CaptureModifiers keys) => session.ApplyPointerInput(point, keys);
                Input(CaptureModifiers.Ctrl);
                Check(session.SelectingControl, "Ctrlを保持中に選択できない");
                Input(CaptureModifiers.Ctrl | CaptureModifiers.Alt);
                Input(CaptureModifiers.Alt);
                Check(!session.SelectingControl, "組み合わせの途中でトグルした");
                Input(CaptureModifiers.None);
                Check(session.SelectingControl, "2キーを離しても切り替わらない");
                Input(CaptureModifiers.Ctrl | CaptureModifiers.Alt);
                Input(CaptureModifiers.Ctrl);
                Input(CaptureModifiers.None);
                Check(!session.SelectingControl, "再度のトグルで選択が終了しない");

                Input(CaptureModifiers.Win);
                Equal(CaptureMode.Window, session.Mode);
                Input(CaptureModifiers.None);
                Equal(CaptureMode.Rectangle, session.Mode);
                Input(CaptureModifiers.Shift | CaptureModifiers.Win);
                Input(CaptureModifiers.Shift);
                Equal(CaptureMode.Rectangle, session.Mode);
                Input(CaptureModifiers.None);
                Equal(CaptureMode.Window, session.Mode);
                Check(session.SelectionMenu.Command("IsolatedWindowCapture").Checked, "トグルでWGCが選ばれない");
            }
            finally { session.Cancel(); }
        });
        Run("矩形ドラッグ中のWin単押しは旧割り当てでも撮影モードを変えない", () =>
        {
            Rectangle monitor = new(0, 0, 600, 400);
            var snapshot = new DesktopSnapshot(new Bitmap(600, 400), monitor, [monitor]);
            var settings = new AppSettings { CaptureKeys = new CaptureKeyMap(Win: CaptureKeyAction.HighlightWindow) };
            var session = new SessionController(new(CaptureMode.Rectangle, CaptureIntent.Screenshot, OutputDestination.Clipboard,
                OutputSettings: settings), snapshot, []);
            try
            {
                var gesture = (SelectionGesture)typeof(SessionController).GetField("gesture",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(session)!;
                gesture.Begin(new(100, 100), new(8, 8));
                session.ApplyPointerInput(new(160, 150), CaptureModifiers.Win);
                Equal<string?>(null, session.ModifierHint);
                session.ApplyPointerInput(new(180, 170), CaptureModifiers.None, windowsTap: true);
                Equal(CaptureMode.Rectangle, session.Mode);
                Check(gesture.IsPressed && gesture.HasDragged && !session.HighlightWindow, "Win単押しでドラッグ状態が変わった");
                Equal(new Rectangle(100, 100, 80, 70), gesture.Selection);
            }
            finally { session.Cancel(); }
        });
        Run("左単押しの対象名を撮影結果に添え、矩形ドラッグには添えない", () =>
        {
            Rectangle monitor = new(0, 0, 600, 400);
            Rectangle window = new(40, 40, 240, 180);
            var gesture = new SelectionGesture(CaptureMode.Rectangle, [monitor], [window]);
            Point pointer = new(110, 110);
            gesture.Begin(pointer, new(8, 8));
            var selected = gesture.End(pointer, OutputDestination.Clipboard)!;
            var snapshot = new DesktopSnapshot(new Bitmap(600, 400), monitor, [monitor]);
            var session = new SessionController(new(CaptureMode.Rectangle, CaptureIntent.Screenshot, OutputDestination.Clipboard),
                snapshot, [window], readTitle: point => point == pointer ? "検証用ウィンドウ" : "");
            session.CompleteSelection(selected);
            using var outcome = session.Completion.GetAwaiter().GetResult();
            Equal("検証用ウィンドウ", outcome.Selection!.WindowTitle);
            gesture.Begin(pointer, new(8, 8));
            Check(gesture.End(new(150, 150), OutputDestination.Clipboard)!.WindowHitPoint is null, "任意の矩形にウィンドウ名を付けた");
        });
        Run("矩形Spaceは四隅を時計回りに巡回し、中心の選択中は寸法を保って移動する", () =>
        {
            var defaults = new AppSettings().CaptureKeys;
            Check(defaults.SpaceEnabled && defaults.SpaceCycle == CaptureAnchorCycle.Corners, "初期値が四方移動ではない");
            var gesture = new SelectionGesture(CaptureMode.Rectangle, [new(-800, -600, 1600, 1200)], []);
            gesture.Begin(new(-220, -150), new(8, 8));
            gesture.Move(new(-100, -70));
            var original = gesture.Selection;
            foreach (Point expected in new Point[] { new(-220, -70), new(-220, -150), new(-100, -150), new(-100, -70) })
            {
                Check(gesture.Space(cycle: CaptureAnchorCycle.Corners), "四方移動ができない");
                Equal(expected, gesture.RawCurrent);
                gesture.Move(expected);
                Equal(original, gesture.Selection);
            }
            gesture.Space(cycle: CaptureAnchorCycle.CornersAndCenter);
            gesture.Space(cycle: CaptureAnchorCycle.CornersAndCenter);
            Equal(new Point(-160, -110), gesture.RawCurrent);
            gesture.Move(new(-150, -90));
            Equal(new Rectangle(-210, -130, 120, 80), gesture.Selection);
            gesture.Space(cycle: CaptureAnchorCycle.CornersAndCenter);
            Equal(new Point(-210, -130), gesture.RawCurrent);
            gesture.Move(new(-230, -155));
            Equal(new Rectangle(-230, -155, 140, 105), gesture.End(new(-230, -155), OutputDestination.None)!.Bounds);
        });
        Run("矩形のホイールは始点側と終点側を広げ、移動と確定に寸法を反映する", () =>
        {
            var gesture = new SelectionGesture(CaptureMode.Rectangle, [new(-100, -100, 400, 300)], []);
            Check(!gesture.ResizeRectangle(120), "未押下で範囲を変更した");
            gesture.Begin(new(-60, 10), new(8, 8));
            Check(!gesture.ResizeRectangle(120), "ドラッグ前のホイールで範囲を変更した");
            gesture.Move(new(10, 70));
            Check(gesture.ResizeRectangle(60), "高精細ホイールを受け付けない");
            Equal(new Rectangle(-60, 10, 70, 60), gesture.Selection);
            gesture.ResizeRectangle(60);
            Equal(new Rectangle(-61, 9, 72, 62), gesture.Selection);
            Equal(new Point(11, 71), gesture.MagnifierPoint);
            gesture.Move(new(40, 70));
            Equal(new Rectangle(-61, 9, 102, 62), gesture.Selection);
            gesture.ResizeRectangle(-240);
            Equal(new Rectangle(-59, 11, 98, 58), gesture.Selection);
            var adjusted = gesture.Selection;
            gesture.Space(cycle: CaptureAnchorCycle.Corners);
            Equal(adjusted, gesture.End(gesture.RawCurrent, OutputDestination.None)!.Bounds);
        });
        Run("矩形ホイールは画面端で対辺を同じ幅に保ち、もう一方の軸は調整できる", () =>
        {
            var gesture = new SelectionGesture(CaptureMode.Rectangle, [new(0, 0, 300, 300)], []);
            gesture.Begin(new(100, 100), new(4, 4));
            gesture.Move(new(299, 180));
            gesture.ResizeRectangle(120);
            gesture.ResizeRectangle(120);
            gesture.ResizeRectangle(120);
            Equal(new Rectangle(99, 97, 201, 86), gesture.Selection);
            gesture.ResizeRectangle(-120);
            Equal(new Rectangle(100, 98, 199, 84), gesture.Selection);
        });
        Run("四方向の矩形ホイールは負座標でも各辺を動かし最小2pxで止まる", () =>
        {
            var gesture = new SelectionGesture(CaptureMode.Rectangle, [new(-500, -500, 1000, 1000)], []);
            Point start = new(-100, -80);
            foreach (int x in new[] { -1, 1 })
            foreach (int y in new[] { -1, 1 })
            {
                Point end = start + new Size(200 * x, 100 * y);
                MoveRectangle(start, end);
                Rectangle original = CaptureGeometry.FromDrag(start, end);
                gesture.ResizeRectangle(120);
                Equal(Rectangle.Inflate(original, 1, 1), gesture.Selection);
                gesture.ResizeRectangle(-120);
                Equal(original, gesture.Selection);
            }

            MoveRectangle(start, start + new Size(6, 4));
            gesture.ResizeRectangle(-1200);
            Equal(new Rectangle(-98, -79, 2, 2), gesture.Selection);
            gesture.ResizeRectangle(120);
            Equal(new Rectangle(-99, -80, 4, 4), gesture.Selection);

            void MoveRectangle(Point from, Point to)
            {
                gesture.Begin(from, new(4, 4));
                gesture.Move(to);
            }
        });
        Run("矩形ホイールは方向固定と縦横比固定を保つ", () =>
        {
            var gesture = new SelectionGesture(CaptureMode.Rectangle, [new(0, 0, 600, 500)], []);
            gesture.Begin(new(100, 100), new(4, 4));
            gesture.Move(new(300, 200));
            gesture.Move(new(340, 230), lockWidth: true);
            gesture.ResizeRectangle(120);
            Equal(new Rectangle(100, 99, 200, 132), gesture.Selection);

            gesture.Begin(new(100, 100), new(4, 4));
            gesture.Move(new(300, 200));
            gesture.Move(new(300, 200), action: CaptureKeyAction.LockAspectRatio);
            gesture.ResizeRectangle(120);
            var adjusted = new Rectangle(98, 99, 204, 102);
            Equal(adjusted, gesture.Selection);
            gesture.Space(cycle: CaptureAnchorCycle.CornersAndCenter);
            gesture.Space(cycle: CaptureAnchorCycle.CornersAndCenter);
            Equal(new Point(200, 150), gesture.RawCurrent);
            Equal(adjusted, gesture.Selection);
            gesture.ResizeRectangle(-120);
            Equal(new Rectangle(100, 100, 200, 100), gesture.Selection);
        });
        Run("矩形Spaceは選択を保ちポインタを対角へ移し逆側を操作する", () =>
        {
            var gesture = new SelectionGesture(CaptureMode.Rectangle, [new(0, 0, 1000, 1000)], []);
            gesture.Begin(new(100, 100), new(8, 8)); gesture.Move(new(200, 180));
            gesture.Move(new(400, 350), lockHeight: true, lockWidth: true);
            Check(gesture.Space(target => { Equal(new Point(100, 100), target); return target; }), "Spaceを処理しない");
            Check(gesture.IsPressed && gesture.HasDragged, "ドラッグを終了した");
            Equal(new Rectangle(100, 100, 100, 80), gesture.Selection);
            gesture.Move(new(90, 85));
            Equal(new Rectangle(90, 85, 110, 95), gesture.Selection);
            Equal(gesture.Selection, gesture.End(new(90, 85), OutputDestination.File)!.Bounds);
        });
        Run("Space直後に離すと元の矩形を確定する", () =>
        {
            var gesture = new SelectionGesture(CaptureMode.Rectangle, [new(0, 0, 1000, 1000)], [new(0, 0, 900, 900)]);
            Check(!gesture.Space(), "未ドラッグのSpaceを処理した");
            gesture.Begin(new(10, 10), new(8, 8)); gesture.Move(new(100, 100)); gesture.Space();
            Equal(new Rectangle(10, 10, 90, 90), gesture.End(new(10, 10), OutputDestination.Clipboard)!.Bounds);
            gesture.Begin(new(300, 300), new(8, 8)); gesture.Move(new(200, 200)); gesture.Space();
            gesture.Move(new(100, 120));
            Equal(new Rectangle(100, 120, 100, 80), gesture.Selection);
        });
        Run("固定Spaceは枠を維持してポインタを時計回りの基点へ移動", () =>
        {
            var gesture = new SelectionGesture(CaptureMode.FixedRegion, [new(-1000, -1000, 2000, 2000)], [], new(161, 91));
            Point pointer = new(100, 200); gesture.Move(pointer);
            foreach (var (anchor, x, y) in new[] { (CaptureAnchor.TopRight, 261, 200), (CaptureAnchor.BottomRight, 261, 291),
                (CaptureAnchor.BottomLeft, 100, 291), (CaptureAnchor.Center, 180, 245), (CaptureAnchor.TopLeft, 100, 200) })
            {
                Check(gesture.Space(), "Space未処理"); Equal(anchor, gesture.FixedAnchor);
                Equal(new Point(x, y), gesture.RawCurrent);
                Equal(new Rectangle(100, 200, 161, 91), gesture.Selection);
                Equal(gesture.Selection, gesture.FixedSelection(OutputDestination.File)!.Bounds);
            }
        });
        Run("固定Spaceのキーリピートを抑えガイドの基点表示を更新", () =>
        {
            var snapshot = new DesktopSnapshot(new Bitmap(800, 600), new(0, 0, 800, 600), [new(0, 0, 800, 600)]);
            var session = new SessionController(new(CaptureMode.FixedRegion, CaptureIntent.Screenshot, OutputDestination.File, FixedSize: new(100, 80)), snapshot, []);
            try
            {
                session.MoveTo(new(400, 300)); session.HandleCommandKey(Keys.Space);
                Equal(CaptureAnchor.TopRight, session.FixedAnchor);
                session.HandleCommandKey(Keys.Space, true); Equal(CaptureAnchor.TopRight, session.FixedAnchor);
                session.HandleCommandKey(Keys.Space | Keys.Shift | Keys.Control); Equal(CaptureAnchor.BottomRight, session.FixedAnchor);
                using var guide = new SelectionGuideForm(new(0, 0, 800, 600), CaptureIntent.Screenshot, () => { }, new(100, 80));
                guide.UpdateAnchor(session.FixedAnchor);
                Check(guide.Controls["SelectionHelp"]!.Text.Contains("基点: 右下"), "ガイドが古い基点");
            }
            finally { session.Cancel(); }
        });
        Run("固定Spaceはスナップ済みの基準枠を保ち基点移動後の実移動で配置を再開する", () =>
        {
            var gesture = new SelectionGesture(CaptureMode.FixedRegion, [new(0, 0, 1000, 1000)],
                [new(100, 100, 220, 160)], new(81, 61), CaptureAnchor.BottomLeft);
            Point pointer = new(312, 251);
            gesture.Move(pointer, snap: true);
            Equal(new Point(320, 260), gesture.Current);
            Check(gesture.Space(), "固定枠のSpaceが無効");
            Equal(CaptureAnchor.Center, gesture.FixedAnchor);
            Equal(new Point(360, 229), gesture.RawCurrent);
            Equal(new Rectangle(320, 199, 81, 61), gesture.FixedSelection(OutputDestination.Clipboard)!.Bounds);
            gesture.Move(gesture.RawCurrent, snap: false);
            Equal(new Rectangle(320, 199, 81, 61), gesture.Selection);
            gesture.Move(new Point(370, 233), snap: false);
            Equal(new Rectangle(330, 203, 81, 61), gesture.Selection);
        });
        Run("矩形縦横比は正負4方向とも正確な整数比", () =>
        {
            foreach (var ratio in new[] { CaptureAspectRatio.Wide, CaptureAspectRatio.Standard, CaptureAspectRatio.Square })
                foreach (int sx in new[] { -1, 1 }) foreach (int sy in new[] { -1, 1 })
                {
                    var gesture = new SelectionGesture(CaptureMode.Rectangle, [new(-1000, -1000, 2000, 2000)], []) { AspectRatio = ratio };
                    gesture.Begin(Point.Empty, new(8, 8)); gesture.Move(new(sx * 201, sy * 81));
                    var size = gesture.Selection.Size;
                    Check(size.Width > 0 && size.Height > 0, "選択できない");
                    Equal(ratio == CaptureAspectRatio.Wide ? 9 : ratio == CaptureAspectRatio.Standard ? 3 : 1,
                        size.Height * (ratio == CaptureAspectRatio.Wide ? 16 : ratio == CaptureAspectRatio.Standard ? 4 : 1) / size.Width);
                    Equal(0, size.Width * (ratio == CaptureAspectRatio.Wide ? 9 : ratio == CaptureAspectRatio.Standard ? 3 : 1)
                        - size.Height * (ratio == CaptureAspectRatio.Wide ? 16 : ratio == CaptureAspectRatio.Standard ? 4 : 1));
                }
        });
        Run("縦横比とグリッド・ロック・Space・画面境界を併用", () =>
        {
            var gesture = new SelectionGesture(CaptureMode.Rectangle, [new(0, 0, 1000, 800)], []) { AspectRatio = CaptureAspectRatio.Wide, Grid20 = true };
            gesture.Begin(new(100, 100), new(8, 8)); gesture.Move(new(200, 170));
            int width = gesture.Selection.Width;
            gesture.Move(new(700, 600), lockWidth: true); Equal(width, gesture.Selection.Width);
            gesture.Move(new(999, 799));
            Check(new Rectangle(0, 0, 1000, 800).Contains(gesture.Selection), "画面外へ伸びた");
            Equal(gesture.Selection.Width * 9, gesture.Selection.Height * 16);
            gesture.Space(); gesture.Move(new(500, 500));
            Equal(gesture.Selection.Width * 9, gesture.Selection.Height * 16);
        });
        Run("比率メニューの選択は同セッション内だけで次回自由へ戻る", () =>
        {
            using var menu = new CaptureContextMenu(OutputDestination.File, _ => { }, () => { }, () => { }, changeRatio: _ => { });
            var item = (ToolStripMenuItem)menu.Command("AspectRatioMenu")!;
            Equal(4, item.DropDownItems.Count);
            menu.SetRatio(CaptureAspectRatio.Wide);
            Check(((ToolStripMenuItem)item.DropDownItems[1]).Checked && !((ToolStripMenuItem)item.DropDownItems[0]).Checked, "比率チェック不一致");
            using var next = new CaptureContextMenu(OutputDestination.File, _ => { }, () => { }, () => { }, changeRatio: _ => { });
            Check(((ToolStripMenuItem)((ToolStripMenuItem)next.Command("AspectRatioMenu")!).DropDownItems[0]).Checked, "次回へ漏れた");
        });
    }
}
