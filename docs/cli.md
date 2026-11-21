# SSCapture コマンドラインマニュアル

SSCapture v0.1.10の実行ファイルをPowerShell 7から呼び出し、静止画を撮影できます。実行時に画面上で範囲を選ぶ方法と、座標と寸法を指定してそのまま撮る方法があります。撮影には、対象の画面を表示しているWindowsのユーザーセッションで実行してください。

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
| `--input-preset ID` | 保存済みの入力プリセットを指定します。省略時は実行時点の既定を使います。 |
| `--output-preset ID` | 保存済みの出力プリセットを指定します。省略時は実行時点の既定を使います。 |

撮影方式はどちらか1つを指定します。X・Y・W・Hは仮想デスクトップの**物理ピクセル**で、整数を渡します。X・Yには負の値も使えます。たとえば左にあるモニター内で左上が `(-1280, 100)` の範囲を撮るには、`'--capture-region' '-1280' '100' '640' '480'` と指定します。幅・高さは各1～32,768px、合計1億画素までです。

- **範囲選択**では、入力の「固定位置とサイズ」や「全画面」が選ばれていても、矩形の選択画面を開きます。
- **固定範囲**では、コマンドの座標と寸法を使います。入力プリセットのカーソルや余白の設定も適用されます。余白を20pxにした場合、640×480pxから出力される画像は680×520pxです。モニターを移設した場合は座標を取り直してください。
- 出力はプリセットに従い、コピー・ファイル保存・外部アプリ・ピン留めを併用できます。ピン留めを使う場合は最後のピン留めウィンドウを閉じると撮影プロセスが終了します。

## プリセットを指定したコマンドを生成する

1. SSCaptureの設定で「撮影コマンド」を開き、「方式」で「範囲選択」か「固定範囲」を選びます。
2. 「入力プリセット」と「出力プリセット」を選びます。「既定」を選ぶと、コマンド実行時の既定設定が使われます。個別に選ぶと保存済みプリセットのIDがコマンドに入ります。
3. 「固定範囲」ではX・Y・幅・高さを入力します。入力プリセットへ登録した値を使う場合は「範囲を取り込む」を押します。
4. 「生成」→「コピー」を押し、PowerShellに貼り付けて実行します。生成したコマンドに含まれるプリセットを削除した場合は、選び直して再生成してください。

撮影用のコマンドは独立したプロセスで実行されます。起動済みの常駐版は利用を続けられます。プリセットは `SSCapture.exe` と同じフォルダー内の `data/settings.json` を読み込むため、別の導入先へ移す場合は、その環境で生成し直すとパスとIDを確認できます。

## バッチ処理で保存結果を受け取る

ファイル出力がONのプリセットを既定にするか、下の `$arguments` に `'--output-preset', '生成したコマンドに含まれるID'` を追加します。ピン留めや外部アプリの起動を伴わない保存専用プリセットなら、繰り返し実行時に画面操作を挟まずに済みます。

```powershell
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

リダイレクトした標準出力と標準エラーはUTF-8です。クリップボード出力だけでも成功時は `0` になるため、PNGが必要な処理では保存先の行とファイルを確かめます。命名パターンに `{ocr}` がある場合、撮影CLIは認識した最初の行を使います。取得できない場合はファイル名に「OCRなし」を使います。

## 常駐起動と設定

| 指定 | 動作 |
| --- | --- |
| 引数なし、または `--settings` | 設定画面を開きます。常駐中なら既存プロセスに設定画面の表示を依頼します。 |
| `--startup` または `--hidden` | 設定画面を表示せずに起動します。自動起動の登録は設定画面の「基本」で行います。 |

録画の開始・停止は録画操作ウィンドウや録画ショートカットを使用します。[録画の準備](usage.md#録画の準備)・[利用ガイド](usage.md)

ソースからのビルドやCLIの検証方法は開発者向け資料にあります。[開発者ガイド](DEVELOPERS_GUIDE.md#撮影cliの保存を検証する)
