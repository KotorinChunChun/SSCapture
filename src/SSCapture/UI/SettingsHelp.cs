namespace SSCapture.UI;

internal static class SettingsHelp
{
    private static readonly IReadOnlyDictionary<string, string> Descriptions = new Dictionary<string, string>
    {
        ["StartupSettings"] = "Windowsサインイン時の自動起動です。初期設定はOFFです。KotorinToysと同じ所有予約を参照します。変更はこの欄の「適用」で確定します。設定全体の保存やビルドでは予約を変更しません。",
        ["StartWithWindows"] = "ONにして「適用」を押すと、次回のWindowsサインイン時から設定画面を表示せず起動します。正式インストール版だけが登録できます。本体経由の予約はKotorinToysで変更してください。",
        ["ApplyStartup"] = "自動起動のON/OFFだけを適用します。他の設定編集中の値は保存しません。OFFにしても現在のアプリは終了しません。",
        ["SaveDirectory"] = "ファイル保存を有効にしたときの保存先です。フォルダの完全パスを指定してください。保存先が存在しない場合は保存時に作成します。",
        ["DimCaptureBackground"] = "選択中の背景だけを暗くします。初期設定はOFFです。画面固定・撮影時点・選択枠とは独立した設定です。",
        ["CaptureMode"] = "矩形はドラッグ、ウィンドウは対象をクリック、全画面は全モニタ、自由形状は輪郭を囲みます。固定サイズは撮影時に位置を選ぶ枠、固定位置とサイズは保存した座標と寸法をそのまま使う定位置です。",
        ["ClipboardOutput"] = "ONのときは撮影結果をクリップボードへコピーします。他の出力先と併用できます。録画では動画ファイルをコピーします。",
        ["FileOutput"] = "ONのときは保存先と命名パターンに従って画像・動画を保存します。他の出力先と併用できます。",
        ["ExternalOutput"] = "ONのときは撮影結果を指定した画像・動画編集アプリへ渡します。対応する実行ファイルとパラメータを設定してください。即時撮影には適用しません。",
        ["FreezeFrame"] = "ONのときは撮影開始時の画面を保存・コピーします。OFFのときは範囲確定後、選択画面を閉じてから画面を取得します。選択中の背景はどちらも開始時の静止画です。選択を中断してやり直しても開始画像は保持します。録画映像は固定しません。",
        ["OpenFolderAfterSave"] = "ONのときはファイル保存に成功した後、保存先フォルダを開いて保存ファイルを選択します。ファイル保存がOFFのときと即時撮影では実行しません。",
        ["LimitToStartingMonitor"] = "ONのときは開始時にポインタがあるモニタだけを暗転し、選択範囲とポインタをそのモニタ内に制限します。他のモニタは映像を更新し続けます。OFFのときは全モニタで選択できます。選択画面を使わない全画面・即時撮影には適用しません。",
        ["ScreenshotPaddingPixels"] = "静止画の外側へ上下左右同じ幅の白い余白を追加します。0～4096物理px、0は余白なしです。コピー・保存・外部アプリ・履歴に共通で適用します。撮影範囲や録画映像は変わりません。余白込みで各辺32768px・合計1億画素以内にしてください。",
        ["ScreenshotColorDepth"] = "静止画を最大256色（8ビット）または16色（4ビット）へ減色します。PNGはパレット形式で保存。コピー・外部アプリ・履歴にも同じ色を使用します。寸法・白余白・完全透明領域を保持し、録画は変更しません。写真やグラデーションは色が変わります。",
        ["CapturePresetEditor"] = "出力先・減色・外部アプリ・保存先・命名・OCR・撮影情報の組合せを登録し、変更確定時に自動保存します。白い余白は入力プリセットで設定します。撮影中の適用や個別変更は今回だけです。",
        ["FileNamePattern"] = "保存ファイル名の組立て方です。{yyyymmdd}は年月日、{hhmmss}は時分秒、{ocr}は画像から選んだ文字列、{region}は範囲名です。{#}は連番、{##}は2桁の連番です。例: {yyyymmdd}_{hhmmss}_{##}.png。録画は.mp4で保存します。",
        ["OcrSeparator"] = "OCRで選んだ複数の文字列の間に入れる文字です。例: _ を指定すると、文字列をアンダースコアでつなぎます。ファイル名に使用できない文字は指定できません。",
        ["OneOcrDirectory"] = "OneOCRのエンジンとモデルがあるフォルダの完全パスです。空欄のときはSnipping Toolから自動検出します。OCRを使う命名パターンで必要になります。",
        ["FixedWidth"] = "固定サイズモードの横幅です。単位は画像の物理ピクセルで、1～32768pxを指定できます。幅と高さの積は1億画素以内にしてください。",
        ["FixedHeight"] = "固定サイズモードの高さです。単位は画像の物理ピクセルで、1～32768pxを指定できます。幅と高さの積は1億画素以内にしてください。",
        ["FixedCaptureAnchor"] = "固定サイズの四隅のうち、ポインタに合わせる位置です。右下ならポインタから左上へ指定サイズの枠が広がります。画面外にはみ出した枠は確定できません。",
        ["IncludeCursor"] = "ONのときは撮影画像と録画映像にマウスポインタを含めます。静止画では撮影タイミングの設定に応じた取得時点のポインタが対象です。",
        ["KeepHistory"] = "ONのときは撮影した画像をアプリのメモリーに保持し、撮影履歴から再利用できます。OFFのときは保持せず、既存のメモリー内履歴も消去します。アプリ終了時に履歴は失われます。保存ファイルは削除しません。",
        ["OpenRegions"] = "登録した撮影範囲を追加・変更・削除する画面を開きます。入力プリセットは設定に保存され、再起動後も使用できます。",
        ["OpenHistory"] = "メモリーに保持した撮影履歴を開きます。画像のコピーや外部アプリでの編集ができます。履歴はアプリ終了時に失われます。",
        ["SystemAudio"] = "ONのときは録画と一緒にPCで再生している音を録音します。マイク音声と併用できます。録画中の操作画面でも切り替えられます。",
        ["MicrophoneAudio"] = "ONのときは選択したマイクの音を録画と一緒に録音します。PCの再生音と併用できます。録画中の操作画面でも切り替えられます。",
        ["MicrophoneDevice"] = "マイク録音に使用する入力機器です。「既定のマイク」はWindowsの既定機器を使用します。マイク録音がOFFのときは選択欄が無効になります。",
        ["NotificationDisplay"] = "撮影完了通知を表示するモニタです。「撮影した画面」は撮影範囲に対応するモニタです。指定画面が使えない場合は接続中の画面へ切り替えます。",
        ["NotificationCorner"] = "完了通知を表示する画面の隅です。選択したモニタの作業領域に合わせて左下または右下に表示します。",
        ["NotificationDurationSeconds"] = "完了通知を表示する時間です。単位は秒で、0～3600の整数を指定できます。0のときは通知を表示しません。効果音は別の設定です。",
        ["PlayCaptureSound"] = "ONのときは撮影完了時に短い効果音を鳴らします。通知を非表示にしていても鳴ります。PC音声の録音・共有に含まれる場合があります。",
        ["HoldNotificationWhileApproaching"] = "ONのとき、ポインタが通知へ近づく間や近接中は、表示残時間を設定秒数へ戻して維持します。OFFは通常の時間で消えます。",
        ["NotificationClickAction"] = "通知をクリックしたときの動作です。保存フォルダはファイル保存成功時だけ開けます。外部アプリは通知専用に選べます。",
        ["NotificationApplication"] = "通知から画像・動画を開く登録アプリです。撮影時の出力先アプリとは独立しています。",
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
        if (name == "PreviewRegion") return "登録した位置と寸法の枠だけを表示します。位置が未指定の場合は利用できません。先に座標を指定するか範囲を選んでください。撮影や設定保存は行いません。";
        if (name == "ScreenshotPaddingPixels") return "選択範囲の外側へ上下左右同じ幅の白い余白を追加します。元画像の縮小や周囲の画面の撮り足しはしません。0～4096物理px、0は余白なし。静止画だけに適用し録画には適用しません。";
        if (name == "FileNamePattern") return "{yyyymmdd} 年月日 / {hhmmss} 時分秒 / {ext} 静止画png・録画mp4 / {ocr} OCR文字列 / {region} 範囲名 / {#}・{##} 連番。例: {yyyymmdd}_{hhmmss}.{ext}";
        if (Descriptions.TryGetValue(name, out string? description)) return description;
        if (name == "SnapDistancePixels") return "Alt中、ポインタがウィンドウ枠・モニタ端からこの物理px以内なら吸着します。刻み幅とは別の数値です。1～32768px、既定20px。";
        if (name == "GridStepPixels") return "範囲の幅・高さを調整する物理pxの刻みです。常時ONまたはAlt中に適用します。枠への吸着距離とは別です。1～32768px、既定20px。";
        if (name == "GridEnabled") return "指定の刻み幅を常時使用します。OFFでもAlt中は使用します。固定サイズの寸法や画像の解像度は変えません。";
        if (name == "ShowSelectionGuide") return "撮影中の操作ガイドを表示します。ドラッグ中にポインタが近づくと一時的に隠れます。";
        if (name == "DefaultInputSummary") return "既定の入力プリセットの概要です。クリックすると、そのプリセットを選択した編集ページへ移動します。";
        if (name == "DefaultOutputSummary") return "既定の出力プリセットの概要です。クリックすると、そのプリセットを選択した編集ページへ移動します。";
        if (name == "InputFixedCoordinates") return "ONは登録した位置へ固定枠を表示します。OFFは撮影モードに従い位置や範囲を選びます。再撮影キーはONなら即時撮影、OFFなら選択開始です。";
        if (name.Contains("InputPreset", StringComparison.Ordinal)) return "入力プリセットを追加・複製し、通常撮影の既定を選べます。録画音声も含めて変更確定時に自動保存します。暗転・モニタ制限は基本設定です。";
        if (name is "AddCapturePreset" or "DeleteCapturePreset" or "DefaultCapturePreset" or "CapturePresetName") return "出力プリセットを追加・削除し、既定を選びます。変更確定時に自動保存します。追加は新規・直前の出力設定・選択を複製から選べます。";
        if (name is "MetadataMode" or "PresetMetadataMode") return "静止画をファイルに保存する場合の撮影情報です。記録しない・PNG内XMP・別JSON・両方から選択します。UI名・アプリのパス・タイトル等を含むため共有時には注意してください。";
        if (name.EndsWith("EditorArguments", StringComparison.Ordinal)) return Services.EditorArguments.Help;
        if (name.Contains("Editor", StringComparison.Ordinal) || name.Contains("SelectedEditor", StringComparison.Ordinal))
            return "画像用・動画用の外部アプリを複数登録し、既定を1つ選びます。新規メニューから入力例を選べます。「保存」で一覧登録、設定の「保存」で確定します。参照中のアプリは削除できません。";
        if (name.StartsWith("InstantShortcut", StringComparison.Ordinal))
            return "指定モニタの全体を選択画面なしで即時撮影するキーです。画面1は主画面で、未接続の画面は撮影できません。" + ShortcutHelp;
        if (name.StartsWith("ScreenshotShortcut", StringComparison.Ordinal)) return "静止画の範囲選択を開始するキーです。撮影1と撮影2は同じ操作を開始します。" + ShortcutHelp;
        if (name.StartsWith("RecordingShortcut", StringComparison.Ordinal)) return "録画範囲の選択を開始するキーです。録画中に押すと録画を停止します。" + ShortcutHelp;
        if (name == "LastRegionShortcut") return "直前に確定した範囲を選択画面なしで再撮影するキーです。前回範囲がない場合は撮影できません。" + ShortcutHelp;
        if (name.StartsWith("Region", StringComparison.Ordinal) || name is "SavedRegions" or "NewNumericRegion" or "AddLastRegion" or "DrawRegion" or "PickFixedRegion" or "PreviewRegion" or "SaveRegion" or "DeleteRegion" or "CloseRegions")
            return "入力プリセットの名前・物理pxの位置と寸法・再撮影キーを編集します。設定画面では変更確定時に自動保存します。固定サイズは撮影時に位置を選び、固定位置とサイズは保存した場所を動かさず使用します。";
        if (name is "ImageEditorPreset" or "VideoEditorPreset") return "外部編集アプリの実行ファイルとパラメータの入力例です。選ぶと入力欄へ反映します。実際のインストール先に合わせて変更し、保存してください。";
        if (name is "ImageEditorPath" or "VideoEditorPath") return "撮影結果を渡す編集アプリの.exeファイルの完全パスです。外部アプリへの出力がONのときに使用します。空欄の場合は外部アプリを起動できません。";
        if (name is "ImageEditorArguments" or "VideoEditorArguments") return "外部アプリへ渡す起動引数です。{file}は完全パス、{directory}はフォルダ、{filename}はファイル名に置換します。空白を含むパスを渡せるよう、\"{file}\"のように引用符で囲んでください。";
        return null;
    }

    private const string ShortcutHelp = " Ctrl・Alt・Shift・Winと最後のキーを選びます。最後のキーを「なし」にすると割当なしです。重複や利用できない組合せは保存時に通知します。Windowsや他のアプリが同じキーを使っている場合は動作が競合することがあります。";
}
