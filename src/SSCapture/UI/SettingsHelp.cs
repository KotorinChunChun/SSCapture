namespace SSCapture.UI;

internal static class SettingsHelp
{
    private static readonly IReadOnlyDictionary<string, string> Descriptions = new Dictionary<string, string>
    {
        ["SaveDirectory"] = "ファイル保存を有効にしたときの保存先です。フォルダの完全パスを指定してください。保存先が存在しない場合は保存時に作成します。",
        ["CaptureMode"] = "撮影開始時の選択方法です。矩形はドラッグ、ウィンドウは対象をクリック、全画面は全モニタを撮影します。自由形状は輪郭を囲み、固定サイズは指定した幅・高さで選びます。撮影中の右クリックで一時変更できます。",
        ["ClipboardOutput"] = "ONのときは撮影結果をクリップボードへコピーします。他の出力先と併用できます。録画では動画ファイルをコピーします。",
        ["FileOutput"] = "ONのときは保存先と命名パターンに従って画像・動画を保存します。他の出力先と併用できます。",
        ["ExternalOutput"] = "ONのときは撮影結果を指定した画像・動画編集アプリへ渡します。対応する実行ファイルとパラメータを設定してください。即時撮影には適用しません。",
        ["FreezeFrame"] = "ONのときは撮影開始時の画面を保存・コピーします。OFFのときは範囲確定後、選択画面を閉じてから画面を取得します。選択中の背景はどちらも開始時の静止画です。選択を中断してやり直しても開始画像は保持します。録画映像は固定しません。",
        ["OpenFolderAfterSave"] = "ONのときはファイル保存に成功した後、保存先フォルダを開いて保存ファイルを選択します。ファイル保存がOFFのときと即時撮影では実行しません。",
        ["LimitToStartingMonitor"] = "ONのときは開始時にポインタがあるモニタだけを暗転し、選択範囲とポインタをそのモニタ内に制限します。他のモニタは映像を更新し続けます。OFFのときは全モニタで選択できます。選択画面を使わない全画面・即時撮影には適用しません。",
        ["ScreenshotPaddingPixels"] = "静止画の外側へ上下左右同じ幅の白い余白を追加します。0～4096物理px、0は余白なしです。コピー・保存・外部アプリ・履歴に共通で適用します。撮影範囲や録画映像は変わりません。余白込みで各辺32768px・合計1億画素以内にしてください。",
        ["CapturePresetEditor"] = "出力先・余白・保存フォルダー・命名パターンの組合せを登録します。撮影中の右クリックから呼び出しても既定設定は変わりません。登録・更新後は設定画面で保存してください。",
        ["FileNamePattern"] = "保存ファイル名の組立て方です。{yyyymmdd}は年月日、{hhmmss}は時分秒、{ocr}は画像から選んだ文字列、{region}は範囲名です。{#}は連番、{##}は2桁の連番です。例: {yyyymmdd}_{hhmmss}_{##}.png。録画は.mp4で保存します。",
        ["OcrSeparator"] = "OCRで選んだ複数の文字列の間に入れる文字です。例: _ を指定すると、文字列をアンダースコアでつなぎます。ファイル名に使用できない文字は指定できません。",
        ["OneOcrDirectory"] = "OneOCRのエンジンとモデルがあるフォルダの完全パスです。空欄のときはSnipping Toolから自動検出します。OCRを使う命名パターンで必要になります。",
        ["FixedWidth"] = "固定サイズモードの横幅です。単位は画像の物理ピクセルで、1～32768pxを指定できます。幅と高さの積は1億画素以内にしてください。",
        ["FixedHeight"] = "固定サイズモードの高さです。単位は画像の物理ピクセルで、1～32768pxを指定できます。幅と高さの積は1億画素以内にしてください。",
        ["FixedCaptureAnchor"] = "固定サイズの四隅のうち、ポインタに合わせる位置です。右下ならポインタから左上へ指定サイズの枠が広がります。画面外にはみ出した枠は確定できません。",
        ["IncludeCursor"] = "ONのときは撮影画像と録画映像にマウスポインタを含めます。静止画では撮影タイミングの設定に応じた取得時点のポインタが対象です。",
        ["KeepHistory"] = "ONのときは撮影した画像をアプリのメモリーに保持し、撮影履歴から再利用できます。OFFのときは保持せず、既存のメモリー内履歴も消去します。アプリ終了時に履歴は失われます。保存ファイルは削除しません。",
        ["OpenRegions"] = "登録した撮影範囲を追加・変更・削除する画面を開きます。登録範囲は設定に保存され、再起動後も使用できます。",
        ["OpenHistory"] = "メモリーに保持した撮影履歴を開きます。画像のコピーや外部アプリでの編集ができます。履歴はアプリ終了時に失われます。",
        ["SystemAudio"] = "ONのときは録画と一緒にPCで再生している音を録音します。マイク音声と併用できます。録画中の操作画面でも切り替えられます。",
        ["MicrophoneAudio"] = "ONのときは選択したマイクの音を録画と一緒に録音します。PCの再生音と併用できます。録画中の操作画面でも切り替えられます。",
        ["MicrophoneDevice"] = "マイク録音に使用する入力機器です。「既定のマイク」はWindowsの既定機器を使用します。マイク録音がOFFのときは選択欄が無効になります。",
        ["NotificationDisplay"] = "撮影完了通知を表示するモニタです。「撮影した画面」は撮影範囲に対応するモニタです。指定画面が使えない場合は接続中の画面へ切り替えます。",
        ["NotificationCorner"] = "完了通知を表示する画面の隅です。選択したモニタの作業領域に合わせて左下または右下に表示します。",
        ["NotificationDurationSeconds"] = "完了通知を表示する時間です。単位は秒で、0～3600の整数を指定できます。0のときは通知を表示しません。効果音は別の設定です。",
        ["PlayCaptureSound"] = "ONのときは撮影完了時に短い効果音を鳴らします。通知を非表示にしていても鳴ります。PC音声の録音・共有に含まれる場合があります。",
        ["SaveSettings"] = "入力内容を検証して保存し、以後の撮影に適用します。キー割当の重複や入力に問題がある場合は、画面下に理由を表示します。",
        ["CloseSettings"] = "設定画面を閉じます。保存を押していない変更は保存されません。アプリは常駐を続けます。",
        ["OpenFolder"] = "現在保存されている設定の保存先フォルダを開きます。入力欄の変更を反映するには先に保存してください。"
    };

    internal static void Apply(Control root, ToolTip tips)
    {
        void Assign(Control control, string? inherited = null)
        {
            string? text = Description(control.Name) ?? inherited;
            if (text is not null) tips.SetToolTip(control, text);
            foreach (Control child in control.Controls) Assign(child, text);
            if (control is not TableLayoutPanel table) return;
            foreach (Control child in table.Controls)
            {
                var position = table.GetPositionFromControl(child);
                if (position.Column != 1 || table.GetControlFromPosition(0, position.Row) is not Label label) continue;
                string description = string.Join("\n", All(child).Select(c => tips.GetToolTip(c)).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct());
                if (description.Length > 0) tips.SetToolTip(label, description);
            }
        }
        Assign(root);
    }

    private static IEnumerable<Control> All(Control control)
    {
        yield return control;
        foreach (Control child in control.Controls)
            foreach (Control descendant in All(child)) yield return descendant;
    }

    private static string? Description(string name)
    {
        if (Descriptions.TryGetValue(name, out string? description)) return description;
        if (name.StartsWith("InstantShortcut", StringComparison.Ordinal))
            return "指定モニタの全体を選択画面なしで即時撮影するキーです。画面1は主画面で、未接続の画面は撮影できません。" + ShortcutHelp;
        if (name.StartsWith("ScreenshotShortcut", StringComparison.Ordinal)) return "静止画の範囲選択を開始するキーです。撮影1と撮影2は同じ操作を開始します。" + ShortcutHelp;
        if (name.StartsWith("RecordingShortcut", StringComparison.Ordinal)) return "録画範囲の選択を開始するキーです。録画中に押すと録画を停止します。" + ShortcutHelp;
        if (name == "LastRegionShortcut") return "直前に確定した範囲を選択画面なしで再撮影するキーです。前回範囲がない場合は撮影できません。" + ShortcutHelp;
        if (name is "ImageEditorPreset" or "VideoEditorPreset") return "外部編集アプリの実行ファイルとパラメータの入力例です。選ぶと入力欄へ反映します。実際のインストール先に合わせて変更し、保存してください。";
        if (name is "ImageEditorPath" or "VideoEditorPath") return "撮影結果を渡す編集アプリの.exeファイルの完全パスです。外部アプリへの出力がONのときに使用します。空欄の場合は外部アプリを起動できません。";
        if (name is "ImageEditorArguments" or "VideoEditorArguments") return "外部アプリへ渡す起動引数です。{file}は完全パス、{directory}はフォルダ、{filename}はファイル名に置換します。空白を含むパスを渡せるよう、\"{file}\"のように引用符で囲んでください。";
        return null;
    }

    private const string ShortcutHelp = " Ctrl・Alt・Shift・Winと最後のキーを選びます。最後のキーを「なし」にすると割当なしです。重複や利用できない組合せは保存時に通知します。Windowsや他のアプリが同じキーを使っている場合は動作が競合することがあります。";
}
