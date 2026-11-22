using SSCapture.Capture;
using SSCapture.Services;

namespace SSCapture.UI;

internal static class SettingsHelp
{
    private static readonly IReadOnlyDictionary<string, string> Descriptions = new Dictionary<string, string>
    {
        ["ResetSettingsPage"] = "表示中のページの設定を初期値に戻します。ショートカットは選択中のタブだけが対象です。入力・出力プリセットは確認後に自動保存し、自作登録を保持します。",
        ["StartupSettings"] = "Windowsサインイン時の自動起動です。初期設定はOFFです。KotorinToysと同じ所有予約を参照します。変更は「自動起動を適用」で確定します。設定全体の保存やビルドでは予約を変更しません。",
        ["StartWithWindows"] = "ONにして「自動起動を適用」を押すと、次回のWindowsサインイン時から設定画面を表示せず起動します。正式インストール版だけが登録できます。本体経由の予約はKotorinToysで変更してください。",
        ["ApplyStartup"] = "自動起動のON/OFFだけを適用します。他の設定編集中の値は保存しません。OFFにしても現在のアプリは終了しません。",
        ["ExportSettings"] = "保存済みの設定とプリセットを新規のJSONファイルに書き出します。外部アプリの実行先や保存先も含まれます。",
        ["ImportSettings"] = "書き出したJSONや旧版の設定を確認します。「適用」で現在の設定を置き換えます。",
        ["SaveDirectory"] = "ファイル保存とピン留め窓のCtrl+Sで使う保存先です。フォルダの完全パスを指定してください。存在しない場合は保存時に作成します。",
        ["PinnedOutput"] = "静止画を撮影位置へ枠なし・常に最前面の窓として残します。複数枚を並べられます。Ctrlで描画、Shiftでコピー範囲、通常ドラッグで移動します。録画には適用しません。",
        ["CaptureCommandIntent"] = "コマンドを生成できるのは静止画です。録画はトレイまたは録画ショートカットから開始します。",
        ["CaptureCommandMethod"] = "範囲選択は実行時に範囲を選びます。固定範囲では座標を指定し、入力プリセットに従う場合は登録した撮影方式を使います。",
        ["PresetCaptureShortcuts"] = "設定済みの組み合わせです。一覧で行を選ぶと「選択した割り当ての編集」に表示します。",
        ["PresetCaptureShortcut"] = "選んだ組み合わせで静止画撮影または録画を開始するキーです。Windows キー・Ctrl・Alt・Shiftと最後のキーを選びます。",
        ["ShortcutCaptureIntent"] = "静止画または録画を選びます。録画中に録画のキーを押すと停止します。",
        ["ShortcutInputPreset"] = "「既定を使う」は撮影時点の既定入力を使います。静止画で固定座標のプリセットを指定すると、その範囲をすぐに撮影します。",
        ["ShortcutOutputPreset"] = "このキーで撮影するときの出力です。「既定を使う」は撮影時点の既定出力を参照します。",
        ["AddPresetCaptureShortcut"] = "組み合わせの行を追加します。キーと入出力を選び、編集欄の「保存」で確定します。",
        ["DuplicatePresetCaptureShortcut"] = "選んだ行の撮影種別とプリセットを引き継ぎます。複製先には新しいキーを指定してください。",
        ["SavePresetCaptureShortcuts"] = "PC作業中のショートカット割り当てを確定します。",
        ["DeletePresetCaptureShortcut"] = "選んだ行を削除します。編集欄の「保存」でキーの登録解除が確定します。",
        ["OpenInputPresetShortcuts"] = "この入力を使うプリセット撮影の割り当てを開きます。",
        ["OpenOutputPresetShortcuts"] = "この出力を使うプリセット撮影の割り当てを開きます。",
        ["CaptureCommandInput"] = "撮影条件を使う入力プリセット。既定を選ぶとコマンド実行時の既定入力を使います。",
        ["CaptureCommandOutput"] = "コピー・保存・外部アプリ・ピン留めに使う出力プリセット。既定は実行時の既定出力を使います。",
        ["CaptureCommandX"] = "固定範囲の物理X座標。左側のモニタなどの負座標も指定できます。",
        ["CaptureCommandY"] = "固定範囲の物理Y座標。上側のモニタなどの負座標も指定できます。",
        ["CaptureCommandWidth"] = "固定範囲の幅。1～32768物理px、幅と高さの積は1億画素以内です。",
        ["CaptureCommandHeight"] = "固定範囲の高さ。1～32768物理px、幅と高さの積は1億画素以内です。",
        ["CaptureCommandImport"] = "選択した入力プリセットの位置と寸法を固定範囲へ取り込みます。撮影や設定保存はしません。",
        ["CaptureCommandGenerate"] = "選択した条件のPowerShellコマンドを生成します。実行・撮影・設定保存はしません。",
        ["CaptureCommandCopy"] = "生成済みのPowerShellコマンドをクリップボードへコピーします。撮影はしません。",
        ["CaptureCommandText"] = "PowerShellで実行すると、指定した条件とタイミングで撮影します。",
        ["CaptureScheduleMode"] = "すぐに撮影、指定秒数後、指定日時、定期撮影から選びます。",
        ["CaptureScheduleSeconds"] = "対象を確定してから撮影までの秒数です。1秒以上を指定します。",
        ["CaptureScheduleAt"] = "撮影するローカル日時です。生成したコマンドにはタイムゾーンのオフセットが入ります。",
        ["CaptureScheduleInterval"] = "定期撮影の間隔を秒で指定します。初回は対象を確定した直後です。",
        ["CaptureScheduleCount"] = "定期撮影を終了するまでの成功枚数です。",
        ["DimCaptureBackground"] = "ONのときは、選択中のモニターで選択範囲の外側を暗くします。\nOFFのときは、クリック中・ドラッグ中・ウィンドウ選択中も背景を暗くしません。",
        ["CaptureMode"] = "矩形はドラッグ、ウィンドウは対象をクリック、全画面は全モニタ、自由形状は輪郭を囲みます。固定サイズは撮影時に位置を選ぶ枠、固定位置とサイズは保存した座標と寸法をそのまま使う定位置です。",
        ["ClipboardOutput"] = "ONのときは撮影結果をクリップボードへコピーします。他の出力先と併用できます。録画では動画ファイルをコピーします。",
        ["FileOutput"] = "ONのときは保存先と命名パターンに従って画像・動画を保存します。他の出力先と併用できます。",
        ["ExternalOutput"] = "ONのときは撮影結果を指定した画像・動画編集アプリへ渡します。対応する実行ファイルとパラメータを設定してください。即時撮影には適用しません。",
        ["FreezeFrame"] = "ONのときは撮影開始時の画面を保存・コピーします。OFFのときは範囲確定後、選択画面を閉じてから画面を取得します。選択中の背景はどちらも開始時の静止画です。選択を中断してやり直しても開始画像は保持します。録画映像は固定しません。",
        ["OpenFolderAfterSave"] = "ONのときはファイル保存に成功した後、保存先フォルダを開いて保存ファイルを選択します。ファイル保存がOFFのときと即時撮影では実行しません。",
        ["LimitToStartingMonitor"] = "ONのときは、撮影開始時にポインタがあったモニター内で選択します。\nONのままでも「別モニタへの移動を許可」に割り当てたキー（既定はWin）を押しながらポインタを動かすと、別のモニターへ移動できます。\nOFFのときは、キーを押さずにモニターをまたいで選択できます。",
        ["SelectionMethod"] = "矩形の始点と終点をドラッグで指定するか、左クリック2回で指定するかを選びます。",
        ["ShowMagnifier"] = "ポインタ下の画面を拡大し、中央の物理1pxを示します。矢印キーでポインタを1pxずつ移動できます。",
        ["SelectionGuideDisplay"] = "画面右下の操作ガイドを、撮影を始めたモニターまたはメイン画面に表示します。",
        ["ShareCapturePresentation"] = "モニター全体の画面共有や配信に、選択枠・暗転・表示中の操作ガイド・拡大鏡・ウィンドウプレビューを重ねます。",
        ["ScreenshotPaddingPixels"] = "静止画の外側へ上下左右同じ幅の白い余白を追加します。0～4096物理px、0は余白なしです。コピー・保存・外部アプリ・履歴に共通で適用します。撮影範囲や録画映像は変わりません。余白込みで各辺32768px・合計1億画素以内にしてください。",
        ["ScreenshotColorDepth"] = "静止画を最大256色（8ビット）または16色（4ビット）へ減色します。PNGはパレット形式で保存。コピー・外部アプリ・履歴にも同じ色を使用します。寸法・白余白・完全透明領域を保持し、録画は変更しません。写真やグラデーションは色が変わります。",
        ["CapturePresetEditor"] = "出力先・減色・外部アプリ・保存先・命名・OCR・撮影情報の組合せを登録し、変更確定時に自動保存します。白い余白は入力プリセットで設定します。撮影中の適用や個別変更は今回だけです。",
        ["FileNamePattern"] = "保存ファイルや受け渡しキャッシュの名前を組み立てます。{yyyymmdd}は年月日、{hhmmss}は時分秒、{ocr}は画像から選んだ文字列、{region}は範囲名です。{#}は連番、{##}は2桁の連番です。例: {yyyymmdd}_{hhmmss}_{##}.png。録画は.mp4で保存します。",
        ["OcrSeparator"] = "OCRで選んだ複数の文字列の間に入れる文字です。例: _ を指定すると、文字列をアンダースコアでつなぎます。ファイル名に使用できない文字は指定できません。",
        ["OneOcrDirectory"] = "OneOCRのエンジンとモデルがあるフォルダの完全パスです。空欄のときはSnipping Toolから自動検出します。OCRを使う命名パターンで必要になります。",
        ["FixedWidth"] = "固定サイズモードの横幅です。単位は画像の物理ピクセルで、1～32768pxを指定できます。幅と高さの積は1億画素以内にしてください。",
        ["FixedHeight"] = "固定サイズモードの高さです。単位は画像の物理ピクセルで、1～32768pxを指定できます。幅と高さの積は1億画素以内にしてください。",
        ["FixedCaptureAnchor"] = "固定サイズの枠でポインタに合わせる隅または中心です。画面外にはみ出した枠は確定できません。",
        ["IncludeCursor"] = "ONのときは撮影画像と録画映像にマウスポインタを含めます。静止画では撮影タイミングの設定に応じた取得時点のポインタが対象です。",
        ["KeepHistory"] = "ONのときは撮影した画像をアプリのメモリーに保持し、撮影履歴から再利用できます。OFFのときは保持せず、既存のメモリー内履歴も消去します。アプリ終了時に履歴は失われます。保存ファイルは削除しません。",
        ["OpenRegions"] = "登録した撮影範囲を追加・変更・削除する画面を開きます。入力プリセットは設定に保存され、再起動後も使用できます。",
        ["OpenHistory"] = "メモリーに保持した撮影履歴を開きます。画像のコピーや外部アプリでの編集ができます。履歴はアプリ終了時に失われます。",
        ["SystemAudio"] = "ONのときは録画と一緒にPCで再生している音を録音します。マイク音声と併用できます。録画中の操作画面でも切り替えられます。",
        ["MicrophoneAudio"] = "ONのときは選択したマイクの音を録画と一緒に録音します。PCの再生音と併用できます。録画中の操作画面でも切り替えられます。",
        ["MicrophoneDevice"] = "マイク録音に使用する入力機器です。「既定のマイク」はWindowsの既定機器を使用します。マイク録音がOFFのときは選択欄が無効になります。",
        ["NotificationDisplay"] = "撮影完了通知を表示するモニターです。初期値はメイン画面です。撮影操作の配信表示がONのときは、撮影したモニターへ表示します。指定画面が使えない場合はメイン画面へ表示します。",
        ["NotificationCorner"] = "完了通知を表示する位置です。選択したモニタの作業領域内で、8方位と中央の9か所から選べます。",
        ["NotificationDurationSeconds"] = "完了通知を表示する時間です。単位は秒で、0～3600の整数を指定できます。0のときは通知を表示しません。効果音は別の設定です。",
        ["PlayCaptureSound"] = "ONのときは撮影完了時に短い効果音を鳴らします。通知を非表示にしていても鳴ります。PC音声の録音・共有に含まれる場合があります。",
        ["HoldNotificationWhileApproaching"] = "ONのとき、ポインタが通知へ近づく間や近接中は、表示残時間を設定秒数へ戻して維持します。OFFは通常の時間で消えます。",
        ["NotificationClickAction"] = "通知をクリックしたときの動作です。保存フォルダはファイル保存成功時だけ開けます。外部アプリは通知専用に選べます。",
        ["NotificationApplication"] = "通知の「外部アプリで編集」ボタンが使う登録アプリです。個別指定がない場合は撮影時の出力プリセットの画像/動画アプリを使います。未保存画像は押した時だけPNGを生成し、完成後に起動します。",
        ["ApplyPageSettings"] = "この欄の変更を以後の撮影に適用します。入力に問題がある場合は、画面下に理由を表示します。",
        ["CloseSettings"] = "設定画面を閉じます。未適用の設定や未登録の編集内容は引き継ぎません。アプリは常駐を続けます。",
        ["OpenFolder"] = "選択中の出力プリセットの「保存フォルダー」を開きます。ファイル保存またはピン留め表示を選び、フォルダーの絶対パスを指定してください。"
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
                if (label.Name.StartsWith("PresetCaptureFieldLabel", StringComparison.Ordinal)) continue;
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
        if (name.StartsWith("PresetCaptureFieldLabel", StringComparison.Ordinal))
            return Descriptions[name[^1] switch { '0' => "PresetCaptureShortcut", '1' => "ShortcutInputPreset", _ => "ShortcutOutputPreset" }];
        if (name == "PreviewRegion") return "登録した位置と寸法の枠だけを表示します。位置が未指定の場合は利用できません。先に座標を指定するか範囲を選んでください。撮影や設定保存は行いません。";
        if (name == "ScreenshotPaddingPixels") return "選択範囲の外側へ上下左右同じ幅の白い余白を追加します。元画像の縮小や周囲の画面の撮り足しはしません。0～4096物理px、0は余白なし。静止画だけに適用し録画には適用しません。";
        if (name == "FileNamePattern") return "{yyyymmdd} 年月日 / {hhmmss} 時分秒 / {ext} 静止画png・録画mp4 / {ocr} OCR文字列 / {region} 範囲名 / {#}・{##} 連番。例: {yyyymmdd}_{hhmmss}.{ext}";
        if (Descriptions.TryGetValue(name, out string? description)) return description;
        if (name == "SnapDistancePixels") return $"ウィンドウ枠やデスクトップ（モニター）の端・角にスナップする距離です。既定値は{SelectionSnap.Distance}pxです。";
        if (name == "GridStepPixels") return $"例えば10pxにすると、グリッドスナップで矩形の幅と高さを10px単位で調整します。既定値は{AppSettings.DefaultGridStepPixels}pxです。\n一時使用にも同じ間隔を使います。「グリッドスナップを既定でON」がOFFでも変更できます。";
        if (name == "GridEnabled") return "矩形選択の幅と高さをグリッド間隔に揃えます。\nOFFでも、ショートカットで「一時グリッドスナップ」に割り当てたキーを押している間は使えます。";
        if (name == "ShowSelectionGuide") return "画面右下に操作一覧のウィンドウを表示します。ドラッグ中にポインタが近づくと一時的に隠れます。";
        if (name == "ShowPointerGuide") return "現在の操作、押しているキーの効果、取り消し方法と撮影対象をポインタの近くに表示します。";
        if (name == "InputFixedCoordinates") return "ONは登録した位置へ固定枠を表示します。OFFは撮影モードに従い位置や範囲を選びます。再撮影キーはONなら即時撮影、OFFなら選択開始です。";
        if (name.Contains("InputPreset", StringComparison.Ordinal)) return "入力プリセットを追加・複製し、通常撮影の既定を選べます。録画音声も含めて変更確定時に自動保存します。暗転・モニタ制限は基本設定です。";
        if (name is "AddCapturePreset" or "DeleteCapturePreset" or "DefaultCapturePreset" or "CapturePresetName") return "出力プリセットを追加・削除し、既定を選びます。変更確定時に自動保存します。追加では新規と直前の出力設定を選べます。";
        if (name is "MetadataMode" or "PresetMetadataMode") return "静止画をファイルに保存する場合の撮影情報です。記録しない・PNG内XMP・別JSON・両方から選択します。UI名・アプリのパス・タイトル等を含むため共有時には注意してください。";
        if (name.EndsWith("EditorArguments", StringComparison.Ordinal)) return Services.EditorArguments.Help;
        if (name.Contains("Editor", StringComparison.Ordinal) || name.Contains("SelectedEditor", StringComparison.Ordinal))
            return "画像用・動画用の外部アプリを複数登録し、既定を1つ選びます。追加メニューから入力例を選べます。この画面の「保存」で登録を確定します。参照中のアプリは削除できません。";
        if (name.StartsWith("InstantShortcut", StringComparison.Ordinal))
            return "指定モニタの全体を選択画面なしで即時撮影するキーです。画面1は主画面で、未接続の画面は撮影できません。" + ShortcutHelp;
        if (name == "LastRecipeShortcut") return "成功した直前の静止画と同じ範囲・入出力設定（一時変更を含む）で新しく撮影します。画像履歴とは別で、終了時に記録を破棄します。初期値は未割当です。" + ShortcutHelp;
        if (name.StartsWith("Region", StringComparison.Ordinal) || name is "SavedRegions" or "NewNumericRegion" or "AddLastRegion" or "DrawRegion" or "PickFixedRegion" or "PreviewRegion" or "SaveRegion" or "DeleteRegion" or "CloseRegions")
            return "入力プリセットの名前・物理pxの位置と寸法を編集します。設定画面では変更確定時に自動保存します。固定サイズは撮影時に位置を選び、固定位置とサイズは保存した場所を動かさず使用します。";
        if (name is "ImageEditorPreset" or "VideoEditorPreset") return "外部編集アプリの実行ファイルとパラメータの入力例です。選ぶと入力欄へ反映します。実際のインストール先に合わせて変更し、保存してください。";
        if (name is "ImageEditorPath" or "VideoEditorPath") return "撮影結果を渡す編集アプリの.exeファイルの完全パスです。外部アプリへの出力がONのときに使用します。登録したSnipping ToolはWindowsのPNG関連付けで開きます。";
        if (name is "ImageEditorArguments" or "VideoEditorArguments") return "外部アプリへ渡す起動引数です。{file}は完全パス、{directory}はフォルダ、{filename}はファイル名に置換します。空白を含むパスを渡せるよう、\"{file}\"のように引用符で囲んでください。";
        return null;
    }

    private const string ShortcutHelp = " Ctrl・Alt・Shift・Windows キーと最後のキーを選びます。最後のキーを「なし」にすると割当なしです。重複や利用できない組合せは保存時に通知します。Windowsや他のアプリが同じキーを使っている場合は動作が競合することがあります。";
}
