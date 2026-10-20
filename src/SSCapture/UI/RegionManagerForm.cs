using SSCapture.Services;

namespace SSCapture.UI;

internal sealed class RegionManagerForm : Form
{
    private readonly List<SavedRegion> regions;
    private SavedRegion? lastRegion;
    private readonly Func<Size?, Task<Rectangle?>> selectRegion;
    private readonly Func<Size?, CaptureAnchor, Task<Rectangle?>>? selectAnchoredRegion;
    private readonly Func<SavedRegion[], string?> saveRegions;
    private readonly Func<IReadOnlyList<DisplayLayout>> displays;
    private readonly ListBox list;
    private readonly TextBox name;
    private readonly NumericUpDown x, y, width, height;
    private readonly HotkeyEditor shortcut;
    private readonly ComboBox anchor;
    private readonly Label displayInfo, status;
    private readonly Button delete, addLast;
    private SavedRegion? editing;
    private Rectangle loadedBounds;
    private bool selecting, pickedBounds;
    private bool automaticName, updatingFields;
    private RegionPreviewForm? preview;

    public RegionManagerForm(SavedRegion[] regions, SavedRegion? lastRegion,
        Func<Size?, Task<Rectangle?>> selectRegion, Func<SavedRegion[], string?> saveRegions,
        Func<IReadOnlyList<DisplayLayout>>? displays = null,
        Func<Size?, CaptureAnchor, Task<Rectangle?>>? selectAnchoredRegion = null)
    {
        this.regions = regions.Select(Clone).ToList();
        this.lastRegion = lastRegion is null ? null : Clone(lastRegion);
        this.selectRegion = selectRegion;
        this.selectAnchoredRegion = selectAnchoredRegion;
        this.saveRegions = saveRegions;
        this.displays = displays ?? RegionCoordinates.ReadDisplays;
        Text = WindowTitle.Create("入力プリセット");
        Name = "RegionManagerForm";
        MinimizeBox = MaximizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Yu Gothic UI", 12F);
        BackColor = Color.FromArgb(247, 249, 251);
        ForeColor = Color.FromArgb(28, 37, 47);
        ClientSize = new Size(1040, 604);
        MinimumSize = new Size(1020, 630);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = AppIcon.Create();

        var root = new TableLayoutPanel { Name = "RegionManagerLayout", Dock = DockStyle.Fill,
            Padding = new Padding(12), ColumnCount = 1, RowCount = 3 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        Controls.Add(root);
        var body = new TableLayoutPanel { Name = "RegionBody", Dock = DockStyle.Fill, Margin = Padding.Empty,
            ColumnCount = 2, RowCount = 2 };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 72));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
        root.Controls.Add(body, 0, 0);
        var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3,
            Margin = new Padding(0, 0, 12, 0) };
        left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        body.Controls.Add(left, 0, 0);
        body.SetRowSpan(left, 2);
        list = new ListBox { Name = "SavedRegions", Dock = DockStyle.Fill, IntegralHeight = false,
            DisplayMember = nameof(SavedRegion.Name), Margin = Padding.Empty, FormattingEnabled = true, HorizontalScrollbar = true };
        list.Format += (_, e) =>
        {
            if (e.ListItem is SavedRegion region)
                e.Value = $"{region.Name} / {(string.IsNullOrWhiteSpace(region.Shortcut) ? "なし" : region.Shortcut)} / ({region.Bounds.X}, {region.Bounds.Y}) {region.Bounds.Width}×{region.Bounds.Height}";
        };
        left.Controls.Add(list, 0, 0);
        var addNumeric = Button("新規（数値入力）", "NewNumericRegion", ButtonRole.Secondary);
        addNumeric.Click += (_, _) => NewRegion();
        left.Controls.Add(addNumeric, 0, 1);
        addLast = Button("前回の範囲から追加", "AddLastRegion", ButtonRole.Secondary);
        addLast.Enabled = lastRegion is not null;
        addLast.Click += (_, _) => AddLast();
        left.Controls.Add(addLast, 0, 2);

        var editor = new TableLayoutPanel { Name = "RegionEditor", Dock = DockStyle.Fill,
            ColumnCount = 2, RowCount = 7, Margin = Padding.Empty };
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 184));
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (float row in new[] { 44f, 44f, 88f, 40f, 48f, 48f, 0f })
            editor.RowStyles.Add(new RowStyle(row == 0 ? SizeType.Percent : SizeType.Absolute, row == 0 ? 100 : row));
        body.Controls.Add(editor, 1, 0);
        name = new TextBox { Name = "RegionName", Dock = DockStyle.Fill, MaxLength = 120 };
        AddField(editor, "名前", name, 0);
        shortcut = new HotkeyEditor("RegionShortcut", "") { Dock = DockStyle.Fill };
        AddField(editor, "再撮影ショートカット", shortcut, 1);
        var dimensions = new TableLayoutPanel { Name = "RegionDimensions", Dock = DockStyle.Fill,
            ColumnCount = 4, RowCount = 2, Margin = Padding.Empty };
        dimensions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
        dimensions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        dimensions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
        dimensions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        dimensions.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        dimensions.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        x = Number("RegionX", int.MinValue, int.MaxValue);
        y = Number("RegionY", int.MinValue, int.MaxValue);
        width = Number("RegionWidth", 1, 32768);
        height = Number("RegionHeight", 1, 32768);
        Dimension("X", x, 0, 0); Dimension("Y", y, 2, 0);
        Dimension("幅", width, 0, 1); Dimension("高さ", height, 2, 1);
        AddField(editor, "範囲 (px)", dimensions, 2);
        anchor = new ComboBox { Name = "RegionAnchor", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        foreach (CaptureAnchor value in Enum.GetValues<CaptureAnchor>()) anchor.Items.Add(new AnchorItem(value));
        anchor.SelectedIndex = 0;
        AddField(editor, "基点", anchor, 3);
        var pickers = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        pickers.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        pickers.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        var draw = Button("囲み直す", "DrawRegion", ButtonRole.Secondary);
        draw.Click += async (_, _) => await SelectBoundsAsync(null);
        var fixedSize = Button("この寸法で位置を選ぶ", "PickFixedRegion", ButtonRole.Secondary);
        fixedSize.Click += async (_, _) => await SelectBoundsAsync(new Size((int)width.Value, (int)height.Value));
        pickers.Controls.Add(draw, 0, 0); pickers.Controls.Add(fixedSize, 1, 0);
        editor.Controls.Add(pickers, 0, 4); editor.SetColumnSpan(pickers, 2);
        var check = Button("範囲を確認", "PreviewRegion", ButtonRole.Secondary);
        check.Click += (_, _) => PreviewRegion();
        editor.Controls.Add(check, 0, 5); editor.SetColumnSpan(check, 2);
        displayInfo = new Label { Name = "RegionDisplayInfo", Dock = DockStyle.Fill, AutoEllipsis = true,
            Padding = new Padding(0, 4, 0, 0) };
        editor.Controls.Add(displayInfo, 0, 6); editor.SetColumnSpan(displayInfo, 2);
        var actions = new TableLayoutPanel { Name = "RegionActions", Dock = DockStyle.Fill,
            ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        actions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.Controls.Add(actions, 1, 1);
        var saveSelected = Button("この範囲を保存", "SaveRegion", ButtonRole.Primary);
        saveSelected.Click += (_, _) => SaveRegion();
        actions.Controls.Add(saveSelected, 0, 0);
        delete = Button("選択範囲を削除", "DeleteRegion", ButtonRole.Danger);
        delete.Click += (_, _) => DeleteRegion();
        actions.Controls.Add(delete, 1, 0);
        status = new Label { Name = "RegionStatus", Dock = DockStyle.Fill, AutoEllipsis = true,
            Padding = new Padding(0, 6, 0, 0) };
        root.Controls.Add(status, 0, 1);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false, Margin = Padding.Empty };
        var close = Button("閉じる", "CloseRegions", ButtonRole.Neutral);
        close.Dock = DockStyle.None; close.Size = new Size(120, 36); close.Click += (_, _) => Close();
        footer.Controls.Add(close);
        root.Controls.Add(footer, 0, 2);
        CancelButton = close;
        list.SelectedIndexChanged += (_, _) => { if (list.SelectedItem is SavedRegion region) LoadRegion(region); };
        name.TextChanged += (_, _) => { if (!updatingFields) automaticName = false; };
        shortcut.ValueChanged += (_, _) => UpdateAutomaticName();
        foreach (NumericUpDown number in new[] { x, y, width, height }) number.ValueChanged += (_, _) => UpdateAutomaticName();
        FormClosing += (_, e) => { if (selecting) e.Cancel = true; };
        ReloadList(null);
        if (regions.Length == 0) NewRegion();

        void Dimension(string label, Control control, int column, int row)
        {
            dimensions.Controls.Add(Label(label), column, row);
            dimensions.Controls.Add(control, column + 1, row);
        }
    }

    private static SavedRegion Clone(SavedRegion region) => region with { Layout = region.Layout.ToArray() };
    internal void UpdateLastRegion(SavedRegion? region)
    {
        lastRegion = region is null ? null : Clone(region);
        addLast.Enabled = lastRegion is not null;
    }

    private static Button Button(string text, string name, ButtonRole role)
    {
        var button = new Button { Text = text, Name = name, Dock = DockStyle.Fill, Margin = new Padding(3) };
        ButtonTheme.Apply(button, role);
        return button;
    }
    private static Label Label(string text) => new()
    { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 0, 3, 0) };
    private static NumericUpDown Number(string name, decimal minimum, decimal maximum) => new()
    { Name = name, Minimum = minimum, Maximum = maximum, DecimalPlaces = 0, Dock = DockStyle.Fill, ThousandsSeparator = false };
    private static void AddField(TableLayoutPanel table, string label, Control control, int row)
    { table.Controls.Add(Label(label), 0, row); table.Controls.Add(control, 1, row); }

    private void NewRegion()
    {
        list.ClearSelected(); editing = null; pickedBounds = false; delete.Enabled = false;
        updatingFields = true;
        automaticName = true;
        try
        {
            name.Text = ""; shortcut.Value = "";
            var connected = displays();
            Rectangle screen = connected.Count > 0 ? connected[0].Bounds : new Rectangle(0, 0, 640, 480);
            SetBoundsFields(new Rectangle(screen.Location, new Size(Math.Min(640, screen.Width), Math.Min(480, screen.Height))));
            ShowDisplays(connected);
            SetStatus("新規登録（未保存）");
        }
        catch (Exception ex) { SetStatus(ex.Message, true); }
        finally { updatingFields = false; UpdateAutomaticName(); }
    }

    private void AddLast()
    {
        if (lastRegion is null) return;
        try
        {
            var connected = displays();
            Rectangle bounds = RegionCoordinates.Resolve(lastRegion, connected);
            NewRegion(); SetBoundsFields(bounds); pickedBounds = true;
            ShowDisplays(connected); SetStatus("前回の範囲を読み込みました（未保存）。");
        }
        catch (Exception ex) { SetStatus(ex.Message, true); }
    }

    private void LoadRegion(SavedRegion region)
    {
        editing = region; pickedBounds = false; delete.Enabled = true;
        updatingFields = true;
        automaticName = region.Name == RegionAutoName.Create(region.Shortcut, region.Bounds);
        name.Text = region.Name; shortcut.Value = region.Shortcut;
        try
        {
            var connected = displays();
            SetBoundsFields(RegionCoordinates.Resolve(region, connected));
            ShowDisplays(connected); SetStatus("");
        }
        catch (Exception ex)
        {
            SetBoundsFields(region.Bounds);
            displayInfo.Text = "登録時: " + string.Join(" / ", region.Layout.Select(d => $"{d.Name} ({d.Bounds.Width}×{d.Bounds.Height} px)"));
            SetStatus(ex.Message + " 囲み直すか座標を変更して保存すると、現在の画面構成で再登録します。", true);
        }
        finally { updatingFields = false; UpdateAutomaticName(); }
    }

    private void SetBoundsFields(Rectangle bounds)
    {
        bool previousUpdate = updatingFields;
        updatingFields = true;
        x.Value = bounds.X; y.Value = bounds.Y;
        width.Value = Math.Clamp(bounds.Width, (int)width.Minimum, (int)width.Maximum);
        height.Value = Math.Clamp(bounds.Height, (int)height.Minimum, (int)height.Maximum);
        loadedBounds = bounds;
        updatingFields = previousUpdate;
        UpdateAutomaticName();
    }

    private void UpdateAutomaticName()
    {
        if (updatingFields || !automaticName) return;
        updatingFields = true;
        try { name.Text = RegionAutoName.Create(shortcut.Value, ReadBounds()); }
        finally { updatingFields = false; }
    }

    private Rectangle ReadBounds() => new((int)x.Value, (int)y.Value, (int)width.Value, (int)height.Value);
    private SavedRegion ReadRegion()
    {
        Rectangle bounds = ReadBounds();
        SavedRegion next = editing is not null && !pickedBounds && bounds == loadedBounds
            ? editing with { Name = name.Text.Trim(), Shortcut = shortcut.Value }
            : RegionCoordinates.FromBounds(name.Text.Trim(), bounds, displays(), editing?.Id, shortcut.Value);
        next.Validate();
        return next;
    }

    private void SaveRegion()
    {
        if (selecting) return;
        try
        {
            SavedRegion next = ReadRegion();
            var candidate = regions.Select(Clone).ToList();
            int index = candidate.FindIndex(region => region.Id == next.Id);
            if (index < 0) candidate.Add(next); else candidate[index] = next;
            string? error = saveRegions(candidate.Select(Clone).ToArray());
            if (error is not null) { SetStatus(error, true); return; }
            regions.Clear(); regions.AddRange(candidate); ReloadList(next.Id);
            SetStatus("入力プリセットを保存しました。");
        }
        catch (Exception ex) { SetStatus("保存できませんでした: " + ex.Message, true); }
    }

    private void DeleteRegion()
    {
        if (selecting || editing is null) return;
        try
        {
            var candidate = regions.Where(region => region.Id != editing.Id).Select(Clone).ToArray();
            string? error = saveRegions(candidate.Select(Clone).ToArray());
            if (error is not null) { SetStatus(error, true); return; }
            regions.Clear(); regions.AddRange(candidate); ReloadList(null);
            if (candidate.Length == 0) NewRegion();
            SetStatus("選択した入力プリセットを削除しました。");
        }
        catch (Exception ex) { SetStatus("削除できませんでした: " + ex.Message, true); }
    }

    private void ReloadList(string? selectedId)
    {
        list.BeginUpdate();
        try
        {
            list.Items.Clear(); list.Items.AddRange(regions.Cast<object>().ToArray());
            list.SelectedIndex = regions.Count == 0 ? -1 : Math.Max(0, regions.FindIndex(region => region.Id == selectedId));
        }
        finally { list.EndUpdate(); }
    }

    private async Task SelectBoundsAsync(Size? fixedSize)
    {
        if (selecting) return;
        bool restore = Visible;
        selecting = true; Enabled = false;
        try
        {
            if (restore) CaptureUiPreparation.Prepare([this], () => { });
            CaptureAnchor selectedAnchor = ((AnchorItem)anchor.SelectedItem!).Value;
            Rectangle? bounds = await (selectAnchoredRegion is null ? selectRegion(fixedSize) : selectAnchoredRegion(fixedSize, selectedAnchor));
            if (IsDisposed) return;
            if (bounds is null) { SetStatus("範囲選択をキャンセルしました。"); return; }
            if (fixedSize is { } expected && bounds.Value.Size != expected)
                throw new InvalidOperationException("選択した範囲が指定寸法と一致しません。もう一度位置を選んでください。");
            var connected = displays();
            _ = RegionCoordinates.FromBounds("選択範囲", bounds.Value, connected);
            SetBoundsFields(bounds.Value); pickedBounds = true; ShowDisplays(connected);
            SetStatus("選択した範囲は未保存です。");
        }
        catch (Exception ex) { if (!IsDisposed) SetStatus("範囲を選択できませんでした: " + ex.Message, true); }
        finally
        {
            selecting = false;
            if (!IsDisposed) { Enabled = true; if (restore) WindowVisibility.Show(this); }
        }
    }

    private void PreviewRegion()
    {
        if (selecting) return;
        try
        {
            Rectangle bounds = RegionCoordinates.Resolve(ReadRegion(), displays());
            preview?.Close();
            bool restore = Visible;
            preview = new RegionPreviewForm(bounds);
            preview.FormClosed += (_, _) => { preview = null; if (restore && !IsDisposed) WindowVisibility.Show(this); };
            if (restore) Hide();
            WindowVisibility.Show(preview);
        }
        catch (Exception ex) { SetStatus("範囲を表示できませんでした: " + ex.Message, true); }
    }

    private void ShowDisplays(IReadOnlyList<DisplayLayout> connected) => displayInfo.Text =
        "接続中: " + string.Join(" / ", connected.Select(display => $"{display.Name} ({display.Bounds.Width}×{display.Bounds.Height} px)"));
    private void SetStatus(string text, bool error = false)
    { status.Text = text; status.ForeColor = error ? Color.FromArgb(166, 35, 35) : Color.FromArgb(50, 74, 82); }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { RegionPreviewForm? old = preview; preview = null; old?.Dispose(); }
        base.Dispose(disposing);
    }

    private sealed record AnchorItem(CaptureAnchor Value)
    {
        public override string ToString() => Value switch
        {
            CaptureAnchor.TopLeft => "左上", CaptureAnchor.TopRight => "右上",
            CaptureAnchor.BottomRight => "右下", CaptureAnchor.BottomLeft => "左下", _ => "左上"
        };
    }
}
