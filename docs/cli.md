# SSCapture コマンドラインマニュアル

SSCaptureの実行ファイルをPowerShell 7から呼び出し、選択画面、明示座標、入力プリセットで静止画を撮影できます。撮影には、対象の画面を表示しているWindowsのユーザーセッションで実行してください。

[撮影サンプル集（10例）](#撮影サンプル集)には、プリセット指定、同じ範囲の連続撮影、Windowsタスクスケジューラでの定期撮影を掲載しています。

## まず実行する

単体インストーラーの既定の導入先を使う例です。KotorinToys経由で導入した場合や別フォルダーを選んだ場合は、変数に実際の `SSCapture.exe` の絶対パスを入れてください。

```powershell
$sscapture = Join-Path $env:LOCALAPPDATA 'Programs\SSCapture\SSCapture.exe'

# 選択画面を開き、撮りたい範囲を囲む
& $sscapture '--capture-select'

# 仮想デスクトップ上の (100, 200) から幅640、高さ480pxを撮る
& $sscapture '--capture-region' '100' '200' '640' '480'
```

新規設定の既定出力はクリップボードです。PNGファイルを残すには、設定の「出力プリセット」で「ファイル保存」か「クリップボード＋ファイル」を既定にするか、実行するコマンドでその出力プリセットを指定します。

## 引数

| 引数 | 動作 |
| --- | --- |
| `--capture-select` | 撮影範囲の選択画面を開きます。通常の矩形では左ドラッグで囲み、Escでキャンセルできます。 |
| `--capture-region X Y W H` | 左上の座標と幅・高さを使い、選択画面を出さずに現在表示されている画面を撮影します。 |
| `--capture-preset` | 入力プリセットの方式で撮影します。登録済みの固定位置はその位置で即時撮影し、固定サイズは枠を置く画面を開きます。 |
| `--input-preset ID` | 保存済みの入力プリセットを指定します。省略時は実行時点の既定を使います。 |
| `--output-preset ID` | 保存済みの出力プリセットを指定します。省略時は実行時点の既定を使います。定期撮影では予約用のファイル保存を使います。 |
| `--delay-seconds N` | 対象を確定した時点からN秒後に1枚撮影します。 |
| `--at 日時` | ISO 8601の日時に1枚撮影します。タイムゾーンのオフセットまたは `Z` を付けてください。 |
| `--interval-seconds N --count M` | 対象の確定後に初回を撮り、その時点からN秒間隔で合計M枚の成功まで撮影します。 |

撮影方式は3つのうち1つを指定します。X・Y・W・Hは仮想デスクトップの**物理ピクセル**で、整数を渡します。X・Yには負の値も使えます。たとえば左にあるモニター内で左上が `(-1280, 100)` の範囲を撮るには、`'--capture-region' '-1280' '100' '640' '480'` と指定します。幅・高さは各1～32,768px、合計1億画素までです。

- **範囲選択**では、入力の「固定位置とサイズ」や「全画面」が選ばれていても、矩形の選択画面を開きます。
- **固定範囲**では、コマンドの座標と寸法を使います。入力プリセットのマウスカーソルや余白の設定も適用されます。余白を20pxにした場合、640×480pxから出力される画像は680×520pxです。モニターを移設した場合は座標を取り直してください。
- **入力プリセットに従う**では、試しに `& $sscapture '--capture-preset' '--input-preset' 'initial-input-fixed' '--output-preset' 'initial-output-file'` と指定すると、対応する初期プリセットが残っている環境では固定サイズをファイル保存できます。IDは設定の「コマンド生成」で確認できます。
- 「スクロール撮影」の入力プリセットを指定すると、手動で領域を囲って縦スクロールを収集できます。対象のアプリを操作し、SSCaptureの操作窓で撮影を確定します。[スクロール撮影の使い方](usage.md#スクロール撮影)
- 出力はプリセットに従い、コピー・ファイル保存・外部アプリ・ピン留めを併用できます。白い余白を指定すると、完成した画像の外周に一度適用します。ピン留めを使う場合は最後のピン留めウィンドウを閉じると撮影プロセスが終了します。

## 時限・定期撮影

撮影方式とタイマーを組み合わせます。`--delay-seconds` と `--at` はいずれか一つ、定期は `--interval-seconds` と `--count` を両方指定します。秒数・枚数は1以上の整数を指定します。`--at` は将来の日時を指定してください。

```powershell
& $sscapture --capture-select --delay-seconds 5
& $sscapture --capture-region 100 200 640 480 --at (Get-Date).AddMinutes(2).ToString('o')
& $sscapture --capture-region 100 200 640 480 --interval-seconds 10 --count 3
```

選択方式で予約した対象は、各回に新しく撮影します。ウィンドウを選ぶと移動後も追跡します。別の窓が重なっていても選んだ窓の映像を取得するには、既定では `Win` を押しながら対象を選びます。予約の選択では通常の範囲やウィンドウを指定できます。手動スクロール撮影のプリセット方式は予約できません。ロック、スリープ、先行処理中に過ぎた定期の回は飛ばします。撮影対象が消失したときや出力に失敗したときは、理由を標準エラーに出して終了します。

定期撮影で `--output-preset` を省略すると、「ファイル保存」の初期プリセットの保存先と命名パターンで、PNGだけを保存します。初期プリセットを削除している場合は、既定プリセットの保存先と命名パターンを使います。保存後のフォルダー表示は行いません。指定したプリセットの出力やフォルダー表示を使うには `--output-preset` を付けます。`{ocr}` を使う命名では、各回の先頭の認識行を使います。取得できない場合は「OCRなし」で保存します。

## プリセットを指定したコマンドを生成する

1. SSCaptureの設定で「コマンド生成」を開き、「方式」で「範囲選択」「固定範囲」「入力プリセットに従う」のいずれかを選びます。
2. 「入力プリセット」と「出力プリセット」を選びます。「既定」を選ぶと、コマンド実行時の既定設定が使われます。個別に選ぶと保存済みプリセットのIDがコマンドに入ります。
3. 「固定範囲」ではX・Y・幅・高さを入力します。入力プリセットへ登録した値を使う場合は「範囲を取り込む」を押します。
4. 「タイマー」で「すぐに撮影」「指定秒数後」「指定日時」「定期撮影」のいずれかを選びます。
5. 「生成」→「コピー」を押し、PowerShellに貼り付けて実行します。生成したコマンドに含まれるプリセットを削除した場合は、選び直して再生成してください。

撮影用のコマンドは独立したプロセスで実行されます。起動済みの常駐版は利用を続けられます。プリセットは `SSCapture.exe` と同じフォルダー内の `data/settings.json` を読み込むため、別の導入先へ移す場合は、その環境で生成し直すとパスとIDを確認できます。

## バッチ処理で保存結果を受け取る

ファイル出力がONのプリセットを既定にするか、下の `$arguments` に `'--output-preset', '生成したコマンドに含まれるID'` を追加します。ピン留めや外部アプリの起動を伴わない保存専用プリセットなら、繰り返し実行時に画面操作を挟まずに済みます。以下を `capture-once.ps1` として保存すると、サンプル8～10で使えます。

```powershell
$ErrorActionPreference = 'Stop'
$sscapture = Join-Path $env:LOCALAPPDATA 'Programs\SSCapture\SSCapture.exe'
$arguments = @('--capture-region', '100', '200', '640', '480')
$logDirectory = Join-Path ([IO.Path]::GetTempPath()) ('SSCapture-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $logDirectory | Out-Null
$stdout = Join-Path $logDirectory 'stdout.txt'
$stderr = Join-Path $logDirectory 'stderr.txt'

$process = Start-Process -FilePath $sscapture -ArgumentList $arguments -Wait -PassThru `
    -RedirectStandardOutput $stdout -RedirectStandardError $stderr

if ($process.ExitCode -eq 2) {
    '撮影がキャンセルされました。'
    exit 2
} elseif ($process.ExitCode -ne 0) {
    Get-Content -LiteralPath $stderr -Encoding utf8
    throw "SSCaptureの出力を確認してください。終了コード $($process.ExitCode)"
} else {
    $saved = Select-String -LiteralPath $stdout -Pattern '^保存しました: (.+)$' -Encoding utf8 |
        Select-Object -Last 1
    if (!$saved) { throw '保存先が出力されませんでした。出力プリセットのファイル保存を確認してください。' }
    $savedPath = $saved.Matches[0].Groups[1].Value
    Get-Item -LiteralPath $savedPath
}
```

| 終了コード | 意味 |
| --- | --- |
| `0` | 指定した出力が完了しました。ファイル保存に成功すると、標準出力に `保存しました: <絶対パス>` が含まれます。 |
| `1` | 引数・設定エラー、撮影失敗、または出力処理エラーです。標準エラーを確認してください。一部の出力やPNG保存がすでに完了している場合があります。 |
| `2` | 範囲選択をキャンセルしました。 |

リダイレクトした標準出力と標準エラーはUTF-8です。クリップボード出力だけでも成功時は `0` になるため、PNGが必要な処理では保存先の行とファイルを確かめます。命名パターンに `{ocr}` がある場合、撮影CLIは認識した最初の行を使います。取得できない場合はファイル名に「OCRなし」を使います。選択操作で単一ウィンドウを確定した場合は、出力プリセットの四辺切り抜きを適用した範囲をOCRに使います。外部アプリへ直接渡す作業ファイルにも、撮影時の命名パターンと衝突回避が適用されます。

## 設定ファイルと自動起動

設定コマンドは撮影コマンドと別に実行します。標準出力・標準エラーをリダイレクトして実行すると結果をUTF-8で確認できます。終了コードは成功時が0、失敗時が1です。

| 引数 | 動作 |
| --- | --- |
| `--settings-export <path>` | 保存済み設定を、新規のUTF-8 JSONに書き出します。 |
| `--settings-import <path> --check` | JSONの形式・設定・参照を検証し、置換件数と端末固有の指定を表示します。 |
| `--settings-import <path>` | 設定を置き換えます。常駐時は同じ実行ファイルの常駐アプリが適用します。 |
| `--startup-status` | 実行した導入先の自動起動状態をJSONで表示します。 |
| `--startup-enable` | Windowsサインイン時の無表示起動を登録します。 |
| `--startup-disable` | Windowsサインイン時のSSCaptureの自動起動を解除します。 |

```powershell
$settingsFile = Join-Path (Get-Location) 'sscapture-settings.json'
& $sscapture '--settings-export' $settingsFile | Out-Host
& $sscapture '--settings-import' $settingsFile '--check' | Out-Host
& $sscapture '--settings-import' $settingsFile | Out-Host
& $sscapture '--startup-status' | Out-Host
```

取込元には書き出したJSONと旧版の `data/settings.json` を指定できます。設定を置き換えるときは以前のファイルを `data/settings.json.import.bak` に退避します。撮影や録画などの終了を求められた場合は、操作を終えてから再実行してください。事前確認の結果に未確認の項目があれば、取込後に設定画面の該当箇所で指定し直せます。

自動起動の登録・解除は、正式に導入した実行ファイルに対して行います。KotorinToys経由の起動を設定している場合は、本体で変更します。設定ファイルの読込・書き出しでWindowsの予約は変わりません。

## 撮影サンプル集

サンプル1～7は、[まず実行する](#まず実行する)と同じ `$sscapture` を使います。座標は撮りたい場所の物理ピクセル値へ置き換えてください。IDが必要な例では「コマンド生成」で対象のプリセットを個別に選び、生成結果の `--input-preset` または `--output-preset` の直後にあるIDを使います。

### 1. 囲んだ範囲をクリップボードへコピーする

既定の出力を「クリップボード」にして実行します。左ドラッグで撮った後、貼り付け先で **Ctrl＋V** を押します。

```powershell
& $sscapture '--capture-select'
```

### 2. 決まった位置を640×480pxで撮る

左上を `(100, 200)` に指定する例です。撮影開始時点の表示内容を、既定の出力条件で処理します。

```powershell
& $sscapture '--capture-region' '100' '200' '640' '480'
```

### 3. 左側のモニターを負の座標で撮る

対象モニターが仮想デスクトップ上で左側にあり、`(-1280, 100)` からの640×480pxが収まる場合の例です。上側のモニターではYにも負の値を使えます。

```powershell
& $sscapture '--capture-region' '-1280' '100' '640' '480'
```

### 4. 指定した入力プリセットで枠を置いて撮る

入力プリセットに撮影モード「固定サイズ」と寸法を設定し、そのIDを指定します。表示された枠を置いて左クリックすると、毎回同じ寸法で撮影できます。

```powershell
$inputId = '固定サイズの入力プリセットIDに置き換える'
& $sscapture '--capture-select' '--input-preset' $inputId
```

### 5. 指定した出力プリセットでPNGへ保存する

出力プリセットで「ファイル」をONにし、「保存・OCR」で保存先と命名パターンを設定します。「クリップボードへコピー」もONにしたプリセットを選べば、同じ撮影から貼り付け用と保存用を用意できます。

```powershell
$outputId = 'ファイル出力プリセットIDに置き換える'
& $sscapture '--capture-select' '--output-preset' $outputId
```

### 6. 登録済みの位置を使い、入力と出力を両方指定する

「コマンド生成」で方式を「固定範囲」にし、入力・出力をそれぞれ選んで「範囲を取り込む」→「生成」を押します。生成されたX・Y・幅・高さと2つのIDを使います。登録範囲を変更した場合は、座標を取り込み直してください。

```powershell
$inputId = '登録範囲の入力プリセットIDに置き換える'
$outputId = 'ファイル出力プリセットIDに置き換える'
$arguments = @('--capture-region', '240', '160', '1280', '720',
    '--input-preset', $inputId, '--output-preset', $outputId)
& $sscapture @arguments
```

### 7. 撮った画像を最前面に残す

「ピン留め表示」がONの出力プリセットを用意します。画面上に残した画像へ **Ctrl＋ドラッグ** で線を描けます。ピン留めを閉じるには、画像を選んで **Ctrl＋W** を押します。

```powershell
$outputId = 'ピン留め用の出力プリセットIDに置き換える'
& $sscapture '--capture-select' '--output-preset' $outputId
```

### 8. 保存完了を待ってバッチ処理につなぐ

[バッチ処理の例](#バッチ処理で保存結果を受け取る)を `C:\Scripts\capture-once.ps1` などに保存します。スクリプト内で実行ファイルの場所と座標を指定し、保存専用の出力プリセットIDを `$arguments` に追加します。定期撮影に使う場合は「保存後に保存フォルダを開く」をOFFにします。命名パターンを `{yyyymmdd}_{hhmmss}.{ext}` にすると、時刻ごとの画像を見分けられます。

```powershell
$scriptPath = 'C:\Scripts\capture-once.ps1'
& pwsh -NoProfile -File $scriptPath
if ($LASTEXITCODE -ne 0) { throw '撮影またはPNG保存を完了できませんでした。' }
```

成功時は保存したPNGの情報を表示します。失敗時はスクリプトが表示する標準エラーを確認してください。

### 9. 保存後に30秒待って次を撮る操作を5枚分行う

サンプル8のスクリプトを使います。1枚の保存完了から30秒待って次の撮影を始めます。途中で失敗した場合は、その時点で止まります。

```powershell
$scriptPath = 'C:\Scripts\capture-once.ps1'
$pwsh = Join-Path $PSHOME 'pwsh.exe'
for ($shot = 1; $shot -le 5; $shot++) {
    & $pwsh -NoProfile -File $scriptPath
    if ($LASTEXITCODE -ne 0) { throw "${shot}枚目の撮影に失敗しました。" }
    if ($shot -lt 5) { Start-Sleep -Seconds 30 }
}
```

### 10. タスクスケジューラで毎日15分おきに撮る

先にサンプル8を1回実行し、保存先にPNGができることを確認します。次に、Windowsの「タスク スケジューラ」で「タスクの作成」を開いて設定します。

| タブ | 設定例 |
| --- | --- |
| 全般 | 名前を `SSCapture 定期撮影` にし、画面を撮るユーザーで「ユーザーがログオンしているときのみ実行する」を選ぶ |
| トリガー | 「毎日」で開始時刻を指定。「繰り返し間隔」を15分、「継続時間」を1日にする |
| 操作 | 「プログラムの開始」を選ぶ。プログラムに `pwsh.exe` のフルパスを指定する。引数に `-NoProfile -NonInteractive -File "C:\Scripts\capture-once.ps1"` を入力する |
| 設定 | 既に実行中の場合は「新しいインスタンスを開始しない」を選ぶ |

PowerShell 7の実行ファイルの場所は `Join-Path $PSHOME 'pwsh.exe'` で確認できます。登録したタスクを右クリックして「実行」を選び、新しいPNGと前回の実行結果を確認します。停止するときは同じタスクを「無効」にします。

撮影したい画面を表示できる状態で使ってください。ログオフ、画面ロック、スリープ中は通常と同じ画像を取得できません。画像が増え続けるため、保存先の空き容量と保存期間を決めて使います。[Microsoft Learnの対話ログオン](https://learn.microsoft.com/windows/win32/api/taskschd/ne-taskschd-task_logon_type)・[繰り返しトリガー](https://learn.microsoft.com/windows/win32/taskschd/repeating-a-task)

## 常駐起動と設定

| 指定 | 動作 |
| --- | --- |
| 引数なし、または `--settings` | 設定画面を開きます。常駐中なら既存プロセスに設定画面の表示を依頼します。 |
| `--startup` または `--hidden` | 設定画面を表示せずに起動します。自動起動の登録は設定画面の「基本」で行います。 |

録画の開始・停止は録画操作ウィンドウや録画ショートカットを使用します。[録画の準備](usage.md#録画の準備)・[利用ガイド](usage.md)

ソースからのビルドやCLIの検証方法は開発者向け資料にあります。[開発者ガイド](DEVELOPERS_GUIDE.md#撮影cliの保存を検証する)
