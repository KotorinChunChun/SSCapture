using SSCapture.Capture;
using SSCapture.Services;
using System.Diagnostics;

namespace SSCapture.Tests;

internal static partial class Program
{
    private static void FrozenCaptureTests()
    {
        Run("Win選択は画面固定と遮蔽にかかわらずWGCを使い、開始カーソル付きのプレビューを再利用する", () =>
        {
            Rectangle desktop = new(-50, -30, 120, 100), bounds = new(-30, -10, 50, 50);
            var window = new CaptureWindow(bounds, "固定対象", 1, 0, 0);
            Rectangle[] monitors = [new(-50, -30, 50, 100), new(0, -30, 70, 100)];
            var covered = new CaptureWindow(new Rectangle(0, 10, 15, 20), "遮蔽物", 2, 0, 0);
            var targets = new FrozenCaptureTargets([new(covered, covered.Bounds, true, true), new(window, bounds, true, false)]);
            using var shape = new Bitmap(4, 3);
            using (var graphics = Graphics.FromImage(shape)) graphics.Clear(Color.FromArgb(128, Color.Lime));
            foreach (var (freeze, includeCursor, cursorPoint) in new[] {
                (true, true, Point.Empty), (false, false, Point.Empty), (true, true, new Point(bounds.Right, 0)) })
            {
                using var cursor = CursorSnapshot.FromImage(shape, cursorPoint, new(1, 1));
                var frame = new DesktopSnapshot(SolidCaptureImage(desktop.Size, Color.Firebrick), desktop, monitors, targets, cursor.Copy());
                int calls = 0;
                var session = new SessionController(new(CaptureMode.Rectangle, CaptureIntent.Screenshot, OutputDestination.Clipboard,
                    FreezeFrame: freeze, IncludeCursor: includeCursor, LimitToStartingMonitor: false), frame, [bounds], initialPointer: cursorPoint,
                    currentFrameFactory: () => throw new InvalidOperationException("デスクトップを取り直しました。"),
                    findWindow: point => bounds.Contains(point) ? window : null, takeWindowFrame: (target, nativeCursor, _) =>
                    {
                        Check(!nativeCursor, "WGC側のカーソルが有効です。");
                        Equal(window, target);
                        Interlocked.Increment(ref calls);
                        return Task.FromResult(WindowFrame(target, Color.RoyalBlue));
                    });
                try
                {
                    Point point = new(-16, 30);
                    session.ApplyPointerInput(point, CaptureModifiers.Win);
                    var preview = WaitForWindowPreview(session);
                    Equal(bounds, preview.Bounds);
                    Equal(Color.RoyalBlue.ToArgb(), Pixel(preview.Image, bounds, covered.Bounds.Location));
                    using var expected = SolidCaptureImage(bounds.Size, Color.RoyalBlue);
                    if (includeCursor && bounds.Contains(cursorPoint)) cursor.Draw(expected, bounds.Location);
                    Equal(expected.GetPixel(29, 9).ToArgb(), Pixel(preview.Image, bounds, new(-1, -1)));
                    Equal(expected.GetPixel(bounds.Width - 1, 10).ToArgb(), preview.Image.GetPixel(bounds.Width - 1, 10).ToArgb());
                    session.CompleteSelection(new(bounds, null, OutputDestination.Clipboard, WindowHitPoint: point));
                    using var result = WaitForFrozenSelection(session);
                    Equal(bounds, result.Selection!.Bounds);
                    Check(ReferenceEquals(preview.Image, result.Image), "確定時にプレビューを再利用しませんでした。");
                    Equal(1, calls);
                    Check(result.Selection.ReplaySource is CaptureReplaySource.Windows { Isolated: true }, "再撮影の対象が失われました。");
                }
                finally { session.Cancel(); }
            }
        });

        Run("開始時画像では遮蔽物の背後やモニター間の隙間を読めない", () =>
        {
            Rectangle desktop = new(-50, 0, 100, 80), bounds = new(-30, 10, 60, 60), coverBounds = new(20, 30, 6, 6);
            CaptureWindow window = new(bounds, "対象", 1, 0, 0, ZOrder: 1);
            CaptureWindow cover = new(coverBounds, "入力透過の遮蔽物", 2, 0, 0);
            var targets = new FrozenCaptureTargets([new(cover, coverBounds, false, true), new(window, bounds, true, false)]);
            Check(!targets.CanRead(window, bounds, [desktop]), "入力透過ウィンドウの下を写せると判定しました。");
            Check(!targets.CanRead(window, bounds, [new(-50, 0, 50, 80), new(1, 0, 49, 80)]), "モニター間の1pxの隙間を見落としました。");
        });

        Run("修飾キーなしの左単押し4候補は開始時画像を使い、矩形ドラッグは最新画面を使う", () =>
        {
            Rectangle monitor = new(-200, -50, 200, 160), windowBounds = new(-180, -40, 150, 120);
            Rectangle controlBounds = new(-151, -22, 31, 18);
            var window = new CaptureWindow(windowBounds, "開始時の窓", 1, 1, 1, ClassName: "Chrome_WidgetWin_1");
            var frozen = new FrozenCaptureTargets([new(window, windowBounds, true, false)]);
            var layout = new ControlLayout([Entry(controlBounds, "17,42", "/0")]);
            frozen.SetBrowserControls(window, windowBounds, layout, layout);
            Point start = controlBounds.Location + new Size(4, 5), end = start + new Size(40, 35);
            foreach (var action in Enum.GetValues<CaptureSingleClickAction>())
            {
                var settings = new AppSettings { CaptureMouse = new(UnmodifiedLeftClick: action) };
                var session = new SessionController(new(CaptureMode.Rectangle, CaptureIntent.Screenshot, OutputDestination.Clipboard,
                    FreezeFrame: false, OutputSettings: settings), Frame(monitor, [monitor], Color.Firebrick, frozen), [windowBounds],
                    currentFrameFactory: () => Frame(monitor, [monitor], Color.RoyalBlue), initialPointer: start,
                    findWindow: point => windowBounds.Contains(point) ? window : null);
                using var overlay = new CaptureOverlay(session, monitor);
                try
                {
                    _ = overlay.Handle;
                    session.MouseDown(overlay, MouseButtons.Left, start, Keys.None);
                    session.MouseUp(MouseButtons.Left, start, Keys.None);
                    if (action == CaptureSingleClickAction.None)
                    {
                        Check(!session.Completion.IsCompleted && !session.HasPointerPress, "「なし」で撮影を終了しました。");
                        session.MouseDown(overlay, MouseButtons.Left, start, Keys.None);
                        session.MoveTo(end);
                        session.MouseUp(MouseButtons.Left, end, Keys.None);
                        using var dragged = WaitForFrozenSelection(session);
                        Equal(Rectangle.FromLTRB(start.X, start.Y, end.X, end.Y), dragged.Selection!.Bounds);
                        Equal(Color.RoyalBlue.ToArgb(), dragged.Image!.GetPixel(0, 0).ToArgb());
                        continue;
                    }
                    using var outcome = WaitForFrozenSelection(session);
                    Equal(action switch
                    {
                        CaptureSingleClickAction.Control => controlBounds,
                        CaptureSingleClickAction.Desktop => monitor,
                        _ => windowBounds
                    }, outcome.Selection!.Bounds);
                    Equal(Color.Firebrick.ToArgb(), outcome.Image!.GetPixel(0, 0).ToArgb());
                }
                finally { session.Cancel(); }
            }
        });

        Run("画面固定中も所有窓と複数対象はWGCで合成し、選択外の隙間は透過する", () =>
        {
            Rectangle desktop = new(0, 0, 160, 100), bounds = new(10, 15, 60, 50);
            CaptureWindow root = new(bounds, "親", 1, 1, 1, ZOrder: 2);
            CaptureWindow owned = new(new(45, 25, 30, 25), "所有窓", 2, 1, 1, root.Handle);
            CaptureWindow other = new(new(100, 12, 40, 35), "別対象", 3, 2, 2, ZOrder: 1);
            CaptureWindow[] windows = [owned, other, root];
            var targets = new FrozenCaptureTargets(windows.Select(window => new FrozenWindow(window, window.Bounds, true, false)).ToArray());
            var frame = Frame(desktop, [desktop], Color.Firebrick, targets);
            var session = new SessionController(new(CaptureMode.Rectangle, CaptureIntent.Screenshot, OutputDestination.Clipboard),
                frame, windows.Select(window => window.Bounds).ToArray(), initialPointer: root.Bounds.Location,
                findWindow: point => windows.FirstOrDefault(window => window.Bounds.Contains(point)),
                findWindowGroup: window => CaptureWindowGroups.ForegroundGroup(window, windows),
                takeWindowFrame: (window, _, _) => Task.FromResult(WindowFrame(window,
                    window == owned ? Color.Gold : window == other ? Color.CornflowerBlue : Color.RoyalBlue)));
            try
            {
                session.ApplyPointerInput(bounds.Location, CaptureModifiers.Win);
                var preview = WaitForWindowPreview(session);
                Equal(Color.Gold.ToArgb(), Pixel(preview.Image, preview.Bounds, owned.Bounds.Location));
                session.CompleteSelection(new(Rectangle.Union(root.Bounds, other.Bounds), null, OutputDestination.Clipboard,
                    Composite: new(WindowRoots: [root, other])));
                using var result = WaitForFrozenSelection(session);
                Rectangle output = result.Selection!.Bounds;
                Equal(Color.RoyalBlue.ToArgb(), Pixel(result.Image!, output, root.Bounds.Location));
                Equal(Color.Gold.ToArgb(), Pixel(result.Image!, output, owned.Bounds.Location));
                Equal(Color.CornflowerBlue.ToArgb(), Pixel(result.Image!, output, other.Bounds.Location));
                Equal(0, result.Image!.GetPixel(80 - output.X, 26 - output.Y).A);
            }
            finally { session.Cancel(); }
        });

        Run("開始時のブラウザーCtrlは安定した要素だけを開始位置の画像から選ぶ", () =>
        {
            Rectangle desktop = new(0, 0, 140, 100), bounds = new(12, 4, 105, 76);
            Rectangle control = new(22, 13, 20, 18);
            CaptureWindow browser = new(bounds, "ブラウザー", 1, 1, 1, ClassName: "Chrome_WidgetWin_1");
            FrozenWindow window = new(browser, bounds, true, false);
            var targets = new FrozenCaptureTargets([window]);
            var layout = new ControlLayout([Entry(control, "17,42", "/0/0"), Entry(new(83, 13, 20, 18), "17,43", "/0/1")]);
            targets.SetBrowserControls(browser, bounds, layout, layout with { Entries = [.. layout.Entries] });
            var session = new SessionController(new(CaptureMode.Rectangle, CaptureIntent.Screenshot, OutputDestination.Clipboard),
                Frame(desktop, [desktop], Color.Firebrick, targets), [bounds], initialPointer: control.Location,
                findWindow: point => bounds.Contains(point) ? browser : null);
            try
            {
                session.ApplyPointerInput(control.Location, CaptureModifiers.Ctrl);
                Equal(control, session.ControlBounds);
                Equal(Color.Firebrick.ToArgb(), Pixel(session.PreviewFrame!.Image, session.PreviewFrame.Bounds, control.Location));
                session.ApplyPointerInput(new(65, 16), CaptureModifiers.Ctrl);
                Equal(Rectangle.Empty, session.ControlBounds);
                session.ApplyPointerInput(control.Location, CaptureModifiers.None);
                Check(!session.SelectingControl && session.PreviewFrame is null, "Ctrlを離してもコントロールの画像が残りました。");
                session.ApplyPointerInput(control.Location, CaptureModifiers.Ctrl);
                typeof(SessionController).GetMethod("ConfirmControl", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(session, [control.Location]);
                using var result = WaitForFrozenSelection(session);
                Equal(control, result.Selection!.Bounds);
                Equal(Color.Firebrick.ToArgb(), result.Image!.GetPixel(0, 0).ToArgb());
            }
            finally { session.Cancel(); }

            var changed = new FrozenCaptureTargets([window]);
            changed.SetBrowserControls(browser, bounds, layout, new([Entry(control, "17,99", "/0/0"), layout.Entries[1]]));
            Equal(layout.Entries[1], changed.ControlLayout(browser, [desktop])!.Entries.Single());
            changed.SetBrowserControls(browser, bounds, layout, new([Entry(new(42, 13, 20, 18), "17,42", "/0/0"), layout.Entries[1]]));
            Equal(layout.Entries[1], changed.ControlLayout(browser, [desktop])!.Entries.Single());
            changed = new FrozenCaptureTargets([window]);
            changed.SetBrowserControls(browser, bounds, layout, layout with { Incomplete = true });
            Check(changed.ControlLayout(browser, [desktop]) is null, "途中で切れたツリーを採用しました。");

            var overlap = new ControlLayout([Entry(control, "17,42", "/0/0"), Entry(new(23, 14, 15, 13), "17,43", "/0/1")]);
            changed.SetBrowserControls(browser, bounds, overlap, overlap);
            Equal(0, changed.ControlLayout(browser, [desktop])!.Entries.Length);

            var document = layout.Entries[0] with { InDocument = true };
            var toolbar = Entry(new(18, 11, 8, 12), "17,81", "/1");
            var chromeOverlap = new ControlLayout([document, toolbar]);
            changed.SetBrowserControls(browser, bounds, chromeOverlap, chromeOverlap);
            Equal(document, changed.ControlLayout(browser, [desktop])!.Entries.Single());
            var popupOverlap = new ControlLayout([document, toolbar with { Popup = true }]);
            changed.SetBrowserControls(browser, bounds, popupOverlap, popupOverlap);
            Equal(0, changed.ControlLayout(browser, [desktop])!.Entries.Length);

            CaptureWindow blocker = new(new(40, 22, 10, 10), "前面", 9, 9, 9);
            var obscuredBrowser = browser with { ZOrder = 1 };
            var obscured = new FrozenCaptureTargets([new(blocker, blocker.Bounds, true, false), window with { Target = obscuredBrowser }]);
            obscured.SetBrowserControls(obscuredBrowser, bounds, layout, layout);
            Equal(layout.Entries[1], obscured.ControlLayout(obscuredBrowser, [desktop])!.Entries.Single());
        });

        Run("固定中の短いブラウザー文字はツールバー候補では捨てず、ポップアップや前面の別窓では遮蔽扱いにする", () =>
        {
            Rectangle desktop = new(0, 0, 200, 120), bounds = new(12, 10, 150, 100);
            Rectangle text = new(30, 34, 16, 16);
            var browser = new CaptureWindow(bounds, "ブラウザー", 1, 1, 1, ZOrder: 1, ClassName: "Chrome_WidgetWin_1");
            var window = new FrozenWindow(browser, bounds, true, false);
            var label = Entry(text, "17,42", "/0") with { InDocument = true, Interactive = false, StaticText = true };
            var toolbar = Entry(new(22, 26, 32, 32), "17,43", "/1");
            var targets = new FrozenCaptureTargets([window]);
            Point pointer = new(35, 39);
            var layout = new ControlLayout([toolbar, label]);
            targets.SetBrowserControls(browser, bounds, layout, layout);
            Equal(label, targets.HitControl(browser, pointer, [desktop]));

            // A fully overlapping non-text control retains the existing conservative rule.
            var button = label with { StaticText = false, Interactive = true };
            var buttonLayout = new ControlLayout([toolbar, button]);
            targets.SetBrowserControls(browser, bounds, buttonLayout, buttonLayout);
            Check(targets.HitControl(browser, pointer, [desktop]) is null, "配置が曖昧なボタンを採用しました。");

            var popup = new ControlLayout([toolbar with { Popup = true }, label]);
            targets.SetBrowserControls(browser, bounds, layout, popup);
            Check(targets.HitControl(browser, pointer, [desktop]) is null, "始点付近のポップアップの遮蔽を見落としました。");

            var cover = new CaptureWindow(new(22, 26, 32, 32), "別窓", 2, 2, 2);
            var obscured = new FrozenCaptureTargets([new(cover, cover.Bounds, true, false), window]);
            obscured.SetBrowserControls(browser, bounds, layout, layout);
            Check(obscured.HitControl(browser, pointer, [desktop]) is null, "前面の別ウィンドウの遮蔽を見落としました。");
        });

        Run("固定対象と無関係な窓の増減・順序はWinとネイティブコントロールを無効にしない", () =>
        {
            Rectangle desktop = new(0, 0, 180, 100), area = new(12, 14, 65, 65), control = new(20, 25, 24, 16);
            var native = new CaptureWindow(area, "一般のアプリ", 1, 1, 1, ZOrder: 2);
            var unrelated = new CaptureWindow(new(100, 10, 45, 50), "別窓", 2, 2, 2);
            var before = new FrozenWindow(native, area, true, false, [new(3, control)]);
            var after = before with { Target = native with { ZOrder = 0, Title = "更新" }, Controls = [new(3, control), new(4, new(50, 50, 16, 16))] };
            var targets = FrozenCaptureTargets.Compare([new(unrelated, unrelated.Bounds, true, false), before], [after]);
            Equal(PixelBounds.From(control), targets.HitControl(native, control.Location, [desktop])!.Hit.Bounds);
            Check(targets.HitControl(native, new(55, 55), [desktop]) is null, "取得後に現れたネイティブコントロールが選べました。");
            using var frame = Frame(desktop, [desktop], Color.Firebrick, targets);
            var frozen = targets.ReadWindows(frame, [native]);
            using (frozen.Image)
            {
                Equal(area, frozen.Bounds);
                Equal(Color.Firebrick.ToArgb(), frozen.Image.GetPixel(8, 8).ToArgb());
            }
            var blocked = FrozenCaptureTargets.Compare([before], [new(unrelated with { ZOrder = 0 }, control, true, false),
                after with { Target = after.Target with { ZOrder = 1 } }]);
            Check(blocked.HitControl(native, control.Location, [desktop]) is null && !blocked.CanRead(native, area, [desktop]),
                "取得時の対象範囲に重なった別窓を見落としました。");
        });

        Run("開始時の別々のブラウザーを選べ、開始後に現れた対象から同じ固定画像の矩形へ戻れる", () =>
        {
            Rectangle desktop = new(0, 0, 220, 100), a = new(0, 0, 70, 90), b = new(100, 0, 70, 90);
            Rectangle first = new(10, 13, 24, 18), second = new(110, 13, 24, 18), newControl = new(140, 45, 24, 18);
            var left = new CaptureWindow(a, "最初のブラウザー", 7, 7, 7, ClassName: "Chrome_WidgetWin_1");
            var right = new CaptureWindow(b, "別のブラウザー", 8, 8, 8, ZOrder: 1, ClassName: "MozillaWindowClass");
            var targets = new FrozenCaptureTargets([new(left, a, true, false), new(right, b, true, false)]);
            var firstLayout = new ControlLayout([Entry(first, "17,1", "/0")]);
            var secondLayout = new ControlLayout([Entry(second, "18,1", "/0")]);
            targets.SetBrowserControls(left, a, firstLayout, firstLayout);
            targets.SetBrowserControls(right, b, secondLayout, new([secondLayout.Entries[0], Entry(newControl, "18,2", "/1")]));
            var session = new SessionController(new(CaptureMode.Rectangle, CaptureIntent.Screenshot, OutputDestination.Clipboard,
                LimitToStartingMonitor: false), Frame(desktop, [desktop], Color.CornflowerBlue, targets), [a, b],
                initialPointer: new(198, 45), findWindow: point => a.Contains(point) ? left : b.Contains(point) ? right : null,
                currentFrameFactory: () => throw new InvalidOperationException("固定画像を撮り直しました。"));
            try
            {
                session.ApplyPointerInput(first.Location, CaptureModifiers.Ctrl);
                Equal(first, session.ControlBounds);
                session.ApplyPointerInput(second.Location, CaptureModifiers.Ctrl);
                Equal(second, session.ControlBounds);
                session.ApplyPointerInput(newControl.Location, CaptureModifiers.Ctrl);
                Check(session.ControlBounds.IsEmpty && session.SelectionError is null, "未確認の要素に枠またはエラーを出しました。");
                typeof(SessionController).GetMethod("ConfirmControl", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(session, [newControl.Location]);
                Check(!session.Completion.IsCompleted, "確認できない要素で撮影が終わりました。");
                session.ApplyPointerInput(newControl.Location, CaptureModifiers.None);
                Check(session.SelectionError is null, "Ctrlを離した後もエラーが残りました。");
                session.CompleteSelection(new(newControl, null, OutputDestination.Clipboard));
                using var outcome = WaitForFrozenSelection(session);
                Equal(newControl, outcome.Selection!.Bounds);
                Equal(Color.CornflowerBlue.ToArgb(), outcome.Image!.GetPixel(0, 0).ToArgb());
            }
            finally { session.Cancel(); }
        });

        Run("ブラウザーの照合完了前に押したCtrlクリックは、確認済みの固定画像で確定する", () =>
        {
            Rectangle desktop = new(0, 0, 100, 80), area = new(4, 4, 88, 72), control = new(20, 24, 30, 20);
            var browser = new CaptureWindow(area, "ブラウザー", 7, 7, 7, ClassName: "Chrome_WidgetWin_1");
            var targets = new FrozenCaptureTargets([new(browser, area, true, false)]);
            var snapshot = Frame(desktop, [desktop], Color.CornflowerBlue, targets);
            var ready = new TaskCompletionSource();
            typeof(DesktopSnapshot).GetProperty(nameof(DesktopSnapshot.FrozenControlsReady), System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(snapshot, ready.Task);
            var session = new SessionController(new(CaptureMode.Rectangle, CaptureIntent.Screenshot, OutputDestination.Clipboard),
                snapshot, [area], initialPointer: control.Location, findWindow: point => area.Contains(point) ? browser : null);
            try
            {
                session.ApplyPointerInput(control.Location, CaptureModifiers.Ctrl);
                Check(session.ControlBounds.IsEmpty && session.SelectionError is null, "照合完了前に枠かエラーを表示しました。");
                typeof(SessionController).GetMethod("ConfirmControl", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(session, [control.Location]);
                Check(!session.Completion.IsCompleted, "照合完了前に確定しました。");
                var layout = new ControlLayout([Entry(control, "17,42", "/0")]);
                targets.SetBrowserControls(browser, area, layout, layout);
                ready.SetResult();
                using var outcome = WaitForFrozenSelection(session);
                Equal(control, outcome.Selection!.Bounds);
                Equal(Color.CornflowerBlue.ToArgb(), outcome.Image!.GetPixel(0, 0).ToArgb());
            }
            finally { ready.TrySetResult(); session.Cancel(); }
        });

        Run("固定ONの取り直しだけが画像を交換し、固定OFFは確定時に最新画像を使う", () =>
        {
            Rectangle desktop = new(0, 0, 80, 80), area = new(10, 10, 40, 40);
            foreach (bool freeze in new[] { true, false })
            {
                int calls = 0;
                var session = new SessionController(new(CaptureMode.Rectangle, CaptureIntent.Screenshot, OutputDestination.Clipboard),
                    Frame(desktop, [desktop], Color.Firebrick), [], initialPointer: area.Location,
                    recapture: (_, _) => Frame(desktop, [desktop], ++calls == 1 ? Color.Gold : Color.CornflowerBlue));
                try
                {
                    session.ChangeCaptureSourceAsync(freeze, false).GetAwaiter().GetResult();
                    Equal(Color.Gold.ToArgb(), session.Snapshot.Image.GetPixel(10, 10).ToArgb());
                    session.CompleteSelection(new(area, null, OutputDestination.Clipboard));
                    using var result = WaitForFrozenSelection(session);
                    Equal((freeze ? Color.Gold : Color.CornflowerBlue).ToArgb(), result.Image!.GetPixel(0, 0).ToArgb());
                    Equal(freeze ? 1 : 2, calls);
                }
                finally { session.Cancel(); }
            }
        });

        Run("固定OFFのプレビュー処理が遅れて終了しても取り直した固定画像を維持する", () =>
        {
            Rectangle desktop = new(0, 0, 80, 80), area = new(10, 10, 40, 40);
            var window = new CaptureWindow(area, "対象", 1, 0, 0);
            var targets = new FrozenCaptureTargets([new(window, area, true, false)]);
            var session = new SessionController(new(CaptureMode.Rectangle, CaptureIntent.Screenshot, OutputDestination.Clipboard, FreezeFrame: false),
                Frame(desktop, [desktop], Color.Firebrick), [area], initialPointer: area.Location,
                recapture: (_, _) => Frame(desktop, [desktop], Color.Gold, targets),
                findWindow: point => area.Contains(point) ? window : null,
                takeWindowFrame: (target, _, _) => Task.FromResult(WindowFrame(target, Color.RoyalBlue)));
            try
            {
                session.ApplyPointerInput(area.Location, CaptureModifiers.Win);
                var live = (WindowCapturePreview)typeof(SessionController).GetField("windowPreview",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(session)!;
                Task<WindowCaptureFrame> pending = live.Frame;
                session.ChangeCaptureSourceAsync(true, false).GetAwaiter().GetResult();
                try { pending.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult(); }
                catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException) { }
                Equal(Color.Gold.ToArgb(), session.Snapshot.Image.GetPixel(10, 10).ToArgb());
                Equal(Color.RoyalBlue.ToArgb(), WaitForWindowPreview(session).Image.GetPixel(0, 0).ToArgb());
                session.CompleteSelection(new(area, null, OutputDestination.Clipboard, WindowHitPoint: area.Location));
                using var result = WaitForFrozenSelection(session);
                Equal(Color.RoyalBlue.ToArgb(), result.Image!.GetPixel(0, 0).ToArgb());
            }
            finally { session.Cancel(); }
        });
    }

    private static CaptureOutcome WaitForFrozenSelection(SessionController session) =>
        session.Completion.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();

    private static WindowCaptureFrame WaitForWindowPreview(SessionController session)
    {
        Check(SpinWait.SpinUntil(() => session.PreviewFrame is not null, TimeSpan.FromSeconds(3)), "WGCのプレビューがありません。");
        return session.PreviewFrame!;
    }

    private static WindowCaptureFrame WindowFrame(CaptureWindow window, Color color) =>
        new(SolidCaptureImage(window.Bounds.Size, color), window.Bounds, DateTimeOffset.Now);

    private static Bitmap SolidCaptureImage(Size size, Color color)
    {
        var image = new Bitmap(size.Width, size.Height);
        using var graphics = Graphics.FromImage(image);
        graphics.Clear(color);
        return image;
    }

    private static int FrozenCaptureBenchmark()
    {
        foreach (Size size in new Size[] { new(1920, 1080), new(3840, 2160), new(7680, 2160) })
        {
            Rectangle desktop = new(Point.Empty, size);
            Rectangle left = new(0, 0, size.Width / 2, size.Height), right = new(size.Width / 2, 0, size.Width / 2, size.Height);
            CaptureWindow first = new(left, "左", 1, 1, 1, ZOrder: 1), second = new(right, "右", 2, 2, 2);
            var targets = new FrozenCaptureTargets([new(second, right, true, false), new(first, left, true, false)]);
            using var snapshot = Frame(desktop, [left, right], Color.Firebrick, targets);
            CaptureWindow[] windows = [second, first];
            List<double> elapsed = [];
            using var process = Process.GetCurrentProcess();
            long peakDelta = 0;
            for (int i = 0; i < 8; i++)
            {
                process.Refresh();
                long before = process.PrivateMemorySize64;
                long started = Stopwatch.GetTimestamp();
                using var preview = targets.ReadWindows(snapshot, windows).Image;
                elapsed.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                process.Refresh();
                peakDelta = Math.Max(peakDelta, process.PrivateMemorySize64 - before);
            }
            double plane = (long)size.Width * size.Height * 4 / (1024.0 * 1024);
            Console.WriteLine($"{size.Width}x{size.Height} 2 windows: first={elapsed[0]:F1} ms, warm median={elapsed.Skip(1).Order().ElementAt(3):F1} ms, " +
                $"plane={plane:F1} MiB, private memory peak delta={peakDelta / (1024.0 * 1024):F1} MiB");
        }
        return 0;
    }

    private static DesktopSnapshot Frame(Rectangle desktop, Rectangle[] monitors, Color color, FrozenCaptureTargets? targets = null)
    {
        var bitmap = new Bitmap(desktop.Width, desktop.Height);
        using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(color);
        return new(bitmap, desktop, monitors, targets);
    }

    private static int Pixel(Bitmap image, Rectangle physical, Point point) =>
        image.GetPixel(point.X - physical.X, point.Y - physical.Y).ToArgb();

    private static ControlLayoutEntry Entry(Rectangle bounds, string id, string path)
    {
        var pixels = PixelBounds.From(bounds);
        return new(new(pixels, new("test-" + id, 50000)), pixels, true, 2, id, path);
    }
}
