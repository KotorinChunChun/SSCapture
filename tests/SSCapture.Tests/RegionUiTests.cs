using System.Reflection;
using SSCapture.Services;
using SSCapture.UI;

namespace SSCapture.Tests;

internal static partial class Program
{
    static partial void RegionUiTests()
    {
        DisplayLayout[] displays = [new("left-monitor", "DISPLAY2", new Rectangle(-1920, -200, 1920, 1080), 96, 96),
            new("right-monitor", "DISPLAY1", new Rectangle(0, 0, 1920, 1080), 96, 96)];
        SavedRegion initial = RegionCoordinates.FromBounds("左画面", new Rectangle(-1600, -100, 640, 480), displays,
            shortcut: "Win+Ctrl+Q");

        Run("入力プリセット設定は出力と隣接し一覧登録後に設定保存へ渡る", () =>
        {
            AppSettings? saved = null;
            using var settings = new SettingsForm(new AppSettings { SavedRegions = [initial] }, next => { saved = next; return null; }, () => { }, () => [], regionDisplays: () => displays);
            var categories = (ListBox)settings.Controls.Find("SettingsCategories", true).Single();
            Equal("入力プリセット", ((TabPage)categories.Items[1]!).Text); Equal("録画・音声", ((TabPage)categories.Items[2]!).Text); Equal("出力プリセット", ((TabPage)categories.Items[3]!).Text);
            settings.SelectCategory("入力プリセット");
            var editor = (RegionManagerForm)settings.Controls.Find("RegionManagerForm", true).Single();
            var presetList = (ListBox)editor.Controls.Find("SavedRegions", true).Single();
            presetList.SelectedItem = presetList.Items.Cast<SavedRegion>().Single(p => p.Id == initial.Id);
            ((TextBox)editor.Controls.Find("RegionName", true).Single()).Text = "変更済み";
            Call(editor, "SaveRegion"); Check(saved is null, "一覧登録だけで設定保存した");
            typeof(SettingsForm).GetMethod("SaveSettings", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(settings, null);
            Equal("変更済み", saved!.SavedRegions.Single(p => p.Id == initial.Id).Name);
        });

        Run("登録範囲画面の一覧・数値欄・キーが最小サイズで収まる", () =>
        {
            using var form = Manager([initial]);
            form.Size = form.MinimumSize;
            _ = form.Handle;
            foreach (Control control in Descendants(form)) _ = control.Handle;
            LayoutTree(form);
            Check(form.ControlBox && !form.MinimizeBox && !form.MaximizeBox, "タイトルバーは閉じるのみ");
            var list = form.Controls.Find("SavedRegions", true).Single();
            var editor = form.Controls.Find("RegionEditor", true).Single();
            Check(list.Width >= 240 && list.Height >= 300, $"一覧の寸法不足 {list.Size}");
            Check(editor.Width >= 650 && editor.Height >= 350, $"編集領域の寸法不足 {editor.Size}");
            foreach (string field in new[] { "RegionName", "RegionX", "RegionY", "RegionWidth", "RegionHeight", "RegionShortcut",
                "RegionAnchor", "NewNumericRegion", "AddLastRegion", "DrawRegion", "PickFixedRegion", "PreviewRegion", "SaveRegion", "CloseRegions", "DeleteRegion" })
            {
                Control control = form.Controls.Find(field, true).Single();
                Check(control.Parent!.ClientRectangle.Contains(control.Bounds), field + " が親領域外");
                Check(control.Width >= 120 && control.Height >= 28, field + " の寸法不足");
            }
            Equal(-1600m, Number(form, "RegionX").Value);
            Equal(-100m, Number(form, "RegionY").Value);
            Check(Number(form, "RegionWidth").Minimum == 1 && Number(form, "RegionHeight").Minimum == 1, "0pxを入力可能");
            Rectangle At(string name) => RegionBoundsOnForm(form.Controls.Find(name, true).Single());
            Rectangle title = At("RegionName"), keys = At("RegionShortcut"), dimensions = At("RegionDimensions");
            Check(keys.Top >= title.Bottom && keys.Bottom <= dimensions.Top, "キーが名前と座標の間にない");
            Rectangle save = At("SaveRegion"), delete = At("DeleteRegion"), add = At("NewNumericRegion"), previous = At("AddLastRegion");
            Equal(add.Top, save.Top); Equal(save.Top, delete.Top);
            Equal(previous.Bottom, save.Bottom); Equal(save.Bottom, delete.Bottom);
            Check(save.Height >= 80 && Math.Abs(save.Width - delete.Width) <= 1, "保存/削除が2段の高さと半幅になっていない");
            Equal(At("DrawRegion").Left, At("PreviewRegion").Left);
            Equal(At("PickFixedRegion").Right, At("PreviewRegion").Right);
            var layout = form.Controls.Find("RegionManagerLayout", true).Single();
            layout.Dock = DockStyle.None; layout.Size = new Size(240, 180); LayoutTree(form);
            Check(list.Width < 240 || editor.Width < 650, "Dock切断を寸法テストで検出できない");
        });

        Run("修飾キーは文字幅に合わせて切れずキー欄と縦中央が揃う", () =>
        {
            foreach (float size in new[] { 12F, 16F, 20F })
            {
                using var editor = new HotkeyEditor("MeasuredShortcut", "Win+Ctrl+Alt+Shift+G")
                { Font = new Font("Yu Gothic UI", size), Size = new Size(780, 64) };
                _ = editor.Handle;
                foreach (Control child in Descendants(editor)) _ = child.Handle;
                LayoutTree(editor);
                var key = editor.Controls.Find("MeasuredShortcutKey", true).Single();
                Check(key.Width >= 180 && key.Parent!.ClientRectangle.Contains(key.Bounds), "通常キー欄が狭いか親領域外");
                using Graphics graphics = editor.CreateGraphics();
                Size glyph = CheckBoxRenderer.GetGlyphSize(graphics, System.Windows.Forms.VisualStyles.CheckBoxState.UncheckedNormal);
                foreach (CheckBox modifier in Descendants(editor).OfType<CheckBox>())
                {
                    Size text = TextRenderer.MeasureText(modifier.Text, modifier.Font, Size.Empty, TextFormatFlags.NoPadding);
                    Check(modifier.Width >= text.Width + glyph.Width + 6, $"{modifier.Text}の文字幅不足: {modifier.Width}/{text.Width + glyph.Width + 6}");
                    Check(modifier.Width >= modifier.GetPreferredSize(Size.Empty).Width, modifier.Text + "の推奨幅不足");
                    Check(modifier.Parent!.ClientRectangle.Contains(modifier.Bounds), modifier.Text + "が親領域外");
                    Check(Math.Abs((modifier.Top * 2 + modifier.Height) - (key.Top * 2 + key.Height)) <= 2,
                        $"{modifier.Text}とキー欄の縦中央が違う: 修飾={modifier.Bounds} key={key.Bounds} parent={key.Parent!.ClientRectangle}");
                }
                key.Font = new Font("Yu Gothic UI", size + 2);
                Check(key.Parent!.ClientRectangle.Contains(key.Bounds), $"ネイティブ高さ変更後のキーが親領域外: {key.Bounds} parent={key.Parent.ClientRectangle}");
                foreach (CheckBox modifier in Descendants(editor).OfType<CheckBox>())
                    Check(Math.Abs((modifier.Top * 2 + modifier.Height) - (key.Top * 2 + key.Height)) <= 2,
                        $"ネイティブ高さ変更後に中央が違う: {modifier.Text}={modifier.Bounds} key={key.Bounds}");
            }
        });

        Run("ショートカット変更通知は操作とValue設定で値が変わった時だけ一度", () =>
        {
            using var editor = new HotkeyEditor("ChangedShortcut", "Ctrl+G");
            var changes = new List<string>();
            editor.ValueChanged += (_, _) => changes.Add(editor.Value);
            editor.Value = "Win+Ctrl+G"; Equal(1, changes.Count); Equal("Win+Ctrl+G", changes[0]);
            editor.Value = "Control + Win + g"; Equal(1, changes.Count);
            ((CheckBox)editor.Controls.Find("Shift", true).Single()).Checked = true;
            Equal(2, changes.Count); Equal("Win+Ctrl+Shift+G", changes[1]);
            ((ComboBox)editor.Controls.Find("ChangedShortcutKey", true).Single()).SelectedIndex = 0;
            Equal(3, changes.Count); Equal("", changes[2]);
            editor.Value = ""; Equal(3, changes.Count);
            try { editor.Value = "Escape"; } catch (ArgumentException) { }
            Equal(3, changes.Count); Equal("", editor.Value);
        });

        Run("新規範囲名はキー略号と座標寸法へ追随し手入力後は保持", () =>
        {
            int saves = 0;
            using var form = Manager([], save: _ => { saves++; return null; });
            var text = form.Controls.Find("RegionName", true).Single();
            var key = (HotkeyEditor)form.Controls.Find("RegionShortcut", true).Single();
            Number(form, "RegionX").Value = 100; Number(form, "RegionY").Value = 200;
            Number(form, "RegionWidth").Value = 640; Number(form, "RegionHeight").Value = 480;
            key.Value = "Ctrl+Win+G";
            Equal("WCG_X100_Y200_W640_H480", text.Text);
            Number(form, "RegionWidth").Value = 800;
            Equal("WCG_X100_Y200_W800_H480", text.Text);
            text.Text = "本文";
            key.Value = "Ctrl+Shift+F8"; Number(form, "RegionX").Value = 140;
            Equal("本文", text.Text); Equal(0, saves);
            Call(form, "AddLast");
            Equal("X-1600_Y-100_W640_H480", text.Text);
            key.Value = "Win+Ctrl+Alt+Shift+2";
            Equal("WCAS2_X-1600_Y-100_W640_H480", text.Text);
            Equal(0, saves);
        });

        Run("既存の任意名は保持し生成形式と一致する名前だけ追随", () =>
        {
            using var manual = Manager([initial]);
            Number(manual, "RegionWidth").Value = 700;
            Equal("左画面", manual.Controls.Find("RegionName", true).Single().Text);
            var generated = initial with { Name = RegionAutoName.Create(initial.Shortcut, initial.Bounds) };
            using var automatic = Manager([generated]);
            Number(automatic, "RegionWidth").Value = 700;
            Equal("WCQ_X-1600_Y-100_W700_H480", automatic.Controls.Find("RegionName", true).Single().Text);
            automatic.Controls.Find("RegionName", true).Single().Text = "手動名";
            Number(automatic, "RegionHeight").Value = 500;
            Equal("手動名", automatic.Controls.Find("RegionName", true).Single().Text);
        });

        Run("固定位置選択と囲み直しは選択した4隅の基点を渡す", () =>
        {
            var requested = new List<(Size? Size, CaptureAnchor Anchor)>();
            using var form = new RegionManagerForm([initial], initial,
                _ => throw new InvalidOperationException("旧コールバックに流れた"), _ => null, () => displays,
                (size, corner) => { requested.Add((size, corner)); return Task.FromResult<Rectangle?>(null); });
            var anchor = (ComboBox)form.Controls.Find("RegionAnchor", true).Single();
            Equal("左上", anchor.Items[0]!.ToString()); Equal("右上", anchor.Items[1]!.ToString());
            Equal("右下", anchor.Items[2]!.ToString()); Equal("左下", anchor.Items[3]!.ToString());
            foreach (CaptureAnchor corner in Enum.GetValues<CaptureAnchor>())
            {
                anchor.SelectedIndex = (int)corner;
                InvokeSelect(form, new Size(320, 240));
                Equal(((Size?)new Size(320, 240), corner), requested[^1]);
                InvokeSelect(form, null); Equal(((Size?)null, corner), requested[^1]);
            }
            Equal(-1600m, Number(form, "RegionX").Value);
        });

        Run("登録範囲は明示保存のみ反映し元データを変更しない", () =>
        {
            int calls = 0;
            SavedRegion[]? saved = null;
            using (var form = Manager([initial], save: value => { calls++; saved = value; return null; }))
            {
                form.Controls.Find("RegionName", true).Single().Text = "編集済み";
                Number(form, "RegionWidth").Value = 720;
                ((HotkeyEditor)form.Controls.Find("RegionShortcut", true).Single()).Value = "Ctrl+Alt+F9";
                Equal(0, calls);
                Call(form, "SaveRegion");
                Equal(1, calls); Equal(1, saved!.Length);
                Equal("編集済み", saved[0].Name); Equal(720, saved[0].Bounds.Width);
                Equal("Ctrl+Alt+F9", saved[0].Shortcut); Equal(initial.Id, saved[0].Id);
                saved[0].Layout[0] = displays[0] with { Name = "callback-side mutation" };
                form.Controls.Find("RegionName", true).Single().Text = "未保存";
            }
            Equal(1, calls); Equal("左画面", initial.Name); Equal(640, initial.Bounds.Width);
            Equal("DISPLAY2", initial.Layout[0].Name);
        });

        Run("新規数値登録と前回追加は保存まで永続化せず不正範囲は拒否", () =>
        {
            int calls = 0;
            SavedRegion[]? saved = null;
            using var form = Manager([], save: value => { calls++; saved = value; return null; });
            Call(form, "AddLast");
            Equal(0, calls); Equal(-1600m, Number(form, "RegionX").Value);
            form.Controls.Find("RegionName", true).Single().Text = "";
            Call(form, "SaveRegion"); Equal(0, calls);
            form.Controls.Find("RegionName", true).Single().Text = "複製";
            Number(form, "RegionX").Value = 5000;
            Call(form, "SaveRegion"); Equal(0, calls);
            Number(form, "RegionX").Value = -1600;
            Call(form, "SaveRegion"); Equal(1, calls); Equal(1, saved!.Length);
            Check(saved[0].Id != initial.Id, "前回範囲と別の登録IDにする");
            Call(form, "NewRegion");
            Equal(1, calls);
        });

        Run("固定サイズ選択は正確な寸法を渡しキャンセルで座標を維持", () =>
        {
            int calls = 0;
            Size? requested = null;
            Rectangle? result = null;
            using var form = Manager([initial], select: size => { requested = size; return Task.FromResult(result); },
                save: _ => { calls++; return null; });
            InvokeSelect(form, new Size(320, 240));
            Equal(new Size(320, 240), requested!.Value); Equal(-1600m, Number(form, "RegionX").Value);
            Equal(640m, Number(form, "RegionWidth").Value); Equal(0, calls);
            result = new Rectangle(100, 100, 320, 240);
            InvokeSelect(form, new Size(320, 240));
            Equal(100m, Number(form, "RegionX").Value); Equal(320m, Number(form, "RegionWidth").Value);
            Equal(0, calls);
            Call(form, "SaveRegion"); Equal(1, calls);
        });

        Run("前回範囲の更新は編集中の入力を保持して次の追加だけに反映", () =>
        {
            using var form = Manager([initial]);
            form.Controls.Find("RegionName", true).Single().Text = "編集中";
            Number(form, "RegionWidth").Value = 720;
            var latest = RegionCoordinates.FromBounds("最新", new Rectangle(10, 20, 300, 200), displays);
            form.UpdateLastRegion(latest);
            Equal("編集中", form.Controls.Find("RegionName", true).Single().Text);
            Equal(720m, Number(form, "RegionWidth").Value);
            Call(form, "AddLast");
            Equal(10m, Number(form, "RegionX").Value); Equal(20m, Number(form, "RegionY").Value);
            Equal(300m, Number(form, "RegionWidth").Value);
            form.UpdateLastRegion(null);
            Check(!form.Controls.Find("AddLastRegion", true).Single().Enabled, "前回未登録で追加可能");
        });

        Run("未接続範囲の名称変更では別ディスプレイに再結合しない", () =>
        {
            DisplayLayout[] replacement = [displays[0] with { Identity = "different-monitor" }, displays[1]];
            SavedRegion[]? saved = null;
            using var form = new RegionManagerForm([initial], initial, _ => Task.FromResult<Rectangle?>(null),
                value => { saved = value; return null; }, () => replacement);
            Check(form.Controls.Find("RegionStatus", true).Single().Text.Contains("接続"), "未接続状態が表示されない");
            form.Controls.Find("RegionName", true).Single().Text = "未接続の名称変更";
            Call(form, "SaveRegion");
            Equal("left-monitor", saved![0].AnchorIdentity);
            Number(form, "RegionX").Value = -1500;
            Call(form, "SaveRegion");
            Equal("different-monitor", saved![0].AnchorIdentity);
        });

        Run("保存失敗時は登録一覧と削除対象を保持", () =>
        {
            int calls = 0;
            using var form = Manager([initial], save: _ => { calls++; return "保存先に書き込めません"; });
            form.Controls.Find("RegionName", true).Single().Text = "エラー時";
            Call(form, "SaveRegion"); Equal(1, calls);
            var list = (ListBox)form.Controls.Find("SavedRegions", true).Single();
            Equal("左画面", ((SavedRegion)list.Items[0]).Name);
            Call(form, "DeleteRegion"); Equal(2, calls); Equal(1, list.Items.Count);
            Check(form.Controls.Find("RegionStatus", true).Single().Text.Contains("書き込めません"), "エラー表示がない");
        });

        Run("範囲確認は指定座標の枠だけを表示するウィンドウ", () =>
        {
            using var preview = new RegionPreviewForm(new Rectangle(-1200, -100, 640, 480));
            Equal(new Rectangle(-1200, -100, 640, 480), preview.Bounds);
            Check(!preview.ShowInTaskbar && preview.TopMost, "枠の表示方式が不正");
            Check(preview.Region!.IsVisible(0, 0) && !preview.Region.IsVisible(320, 240), "枠の内部を覆う");
        });

        Run("範囲確認は通常Escapeとフォーカス外のEscape通知の双方で終了", () =>
        {
            using var preview = new RegionPreviewForm(new Rectangle(0, 0, 640, 480));
            object[] keyArguments = [Message.Create(0, 0x100, (nint)Keys.Escape, 0), Keys.Escape];
            bool handled = (bool)typeof(RegionPreviewForm).GetMethod("ProcessCmdKey", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(preview, keyArguments)!;
            Check(handled && preview.IsDisposed, "通常Escapeで閉じない");
            using var outsideFocus = new RegionPreviewForm(new Rectangle(0, 0, 640, 480));
            object[] messageArguments = [Message.Create(0, 0x0312, 0x5353, 0)];
            typeof(RegionPreviewForm).GetMethod("WndProc", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(outsideFocus, messageArguments);
            Check(outsideFocus.IsDisposed, "フォーカス外Escapeで閉じない");
        });

        RegionManagerForm Manager(SavedRegion[] regions, Func<Size?, Task<Rectangle?>>? select = null,
            Func<SavedRegion[], string?>? save = null) => new(regions, initial,
                select ?? (_ => Task.FromResult<Rectangle?>(null)), save ?? (_ => null), () => displays);
    }

    private static NumericUpDown Number(Form form, string name) => (NumericUpDown)form.Controls.Find(name, true).Single();
    private static void Call(RegionManagerForm form, string name) => typeof(RegionManagerForm)
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null);
    private static void InvokeSelect(RegionManagerForm form, Size? size) => ((Task)typeof(RegionManagerForm)
        .GetMethod("SelectBoundsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, [size])!).GetAwaiter().GetResult();
    private static Rectangle RegionBoundsOnForm(Control control)
    {
        Point location = control.Location;
        for (Control? parent = control.Parent; parent is not null and not Form; parent = parent.Parent)
            location.Offset(parent.Location);
        return new Rectangle(location, control.Size);
    }
}
