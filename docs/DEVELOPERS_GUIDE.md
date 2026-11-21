# ビルド手順

[README](../README.md) / [開発・更新手順](../dev/development.md) / [Release規則](../dev/RELEASE_RULE.md)

Windows 11 x64、PowerShell 7、Gitを使います。コマンドはリポジトリのルートで実行します。

## 準備

```powershell
git clone https://github.com/KotorinChunChun/SSCapture.git
cd SSCapture
pwsh -File scripts/setup-sdk.ps1
```

.NET 10 SDKは`.tools`へ配置し、公式SHA512と照合します。既に利用できる.NET 10 SDKがあれば`common.ps1`が使用します。システムのPATHは変更しません。

## 通常検証

```powershell
pwsh -File scripts/test.ps1
pwsh -File scripts/test-build.ps1
```

通常テストは合成画像・注入した処理を使い、実画面撮影・実録音・個人クリップボード操作を行いません。実入力や機器の検証は[開発手順](../dev/development.md)の専用コマンドで別に実施します。

機能別に実行するには `-Suite` を指定します。通知の試験は、保存済み・未保存・保存失敗でのボタンとクリック領域の切り替え、サムネイルのドラッグ操作、一時PNGの生成や取り消しを確認します。

```powershell
pwsh -File scripts/test.ps1 -Suite Notification -OutputPath dev/history/v0.1.10/logs/notification-cli/tests
pwsh -File scripts/test.ps1 -Suite Output
pwsh -File scripts/test.ps1 -Suite Recording
pwsh -File scripts/test.ps1 -Suite Startup
```

### 実デスクトップのCLI検証

ロックを解除したWindowsデスクトップで、試験中に他のマウス・キー操作を行わずに実行します。テストは専用画面を使い、対象が前面にない場合は入力を中止します。

```powershell
$run = 'dev/history/v0.1.10/logs/native-tests'
pwsh -File scripts/test.ps1 -NativeSmoke Selection -OutputPath "$run/tests"
pwsh -File scripts/test.ps1 -NativeSmoke Notification -OutputPath "$run/tests"
pwsh -File scripts/test.ps1 -NativeSmoke Monitors -OutputPath "$run/tests"
pwsh -File scripts/test.ps1 -NativeSmoke Placement -OutputPath "$run/tests"
```

- `Selection` は方向ロックの解除、Spaceの対角切替、メニュー終了と画面再取得失敗からの復帰、WGCでの対象ウィンドウ取得とPNG保存を検査します。
- `Notification` は本文の透過、フォルダ操作、未保存画像のダブルクリックと生成PNG、前回結果の表示とフォーカス維持を検査します。フォルダと編集アプリの呼び出しは試験内で受け取ります。
- `Monitors` と `Placement` は接続中のモニタを使って選択制限と物理座標を検査します。

同じテストDLLで外部アプリへ専用の画像・動画を渡したり、専用ウィンドウを一定時間録画したりできます。フォトとClipchampへのファイル受け渡しはWindowsの「プログラムから開く」の登録を使います。ファイルを受け取った後の表示やサインイン状態は対象アプリの画面で確認します。録画テストは専用ウィンドウの400×240pxを取得し、途中で一時停止・再開します。出力の寸法、色、音声トラック数、録画時間を照合します。最後のWAV引数を付けたときだけ試験音を再生し、PC音と既定のマイクを同時に有効にして録画中のマイク切り替えを検査します。

```powershell
$test = "$run/tests/SSCapture.Tests.dll"
dotnet $test --store-editor-open photos "$run/sample.png"
dotnet $test --store-editor-open clipchamp "$run/sample.mp4"
dotnet $test --recording-desktop-smoke artifacts/app/tools/ffmpeg.exe artifacts/app/tools/ffprobe.exe "$run/recording" 120
dotnet $test --recording-desktop-smoke artifacts/app/tools/ffmpeg.exe artifacts/app/tools/ffprobe.exe "$run/audio-check" 12 "$run/test-tone.wav"
```

ほかに `Command`、`Pinned`、`Preset`、`Recipe`、`Snap`、`Menu` を指定できます。通知からExplorerやOfficeへの実際のドロップと、他アプリが前面にある場合のフォーカスは実機で確認します。

### 撮影CLIの保存を検証する

`pwsh -File scripts/test.ps1 -Suite Command` では合成画像を使い、座標、入力プリセットの余白、出力プリセットの保存フォルダー、完成パスと終了コードを検証できます。

実プロセスで確認するときは、試験用に新規発行した `SSCapture.exe` と依存ファイルを使います。例では版別ログに `app` を発行し、その実行ファイルの隣に試験用の `data/settings.json` を作ります。

```powershell
$run = Join-Path $PWD.Path ('dev/history/v0.1.10/logs/capture-cli-' + [Guid]::NewGuid().ToString('N'))
$app = Join-Path $run 'app'
. ./scripts/common.ps1
Invoke-Dotnet publish src/SSCapture/SSCapture.csproj -c Release -r win-x64 --self-contained true '-p:RestoreConfigFile=NuGet.Config' -o $app
$data = New-Item -ItemType Directory -Path (Join-Path $app 'data')
$saved = Join-Path $run '試験の保存先'
@{
    SchemaVersion = 3
    PresetSchemaVersion = 1
    NotificationDurationSeconds = 0
    KeepHistory = $false
    SavedRegions = @(@{
        Id = 'fixture-input'; Name = '試験入力'; IsDefault = $true; ProfileVersion = 2
        Mode = 'Rectangle'; UseFixedCoordinates = $false; PaddingPixels = 2
        Bounds = @{ X = 0; Y = 0; Width = 64; Height = 40 }
    })
    CapturePresets = @(@{
        Id = 'fixture-output'; Name = '試験出力'; IsDefault = $true
        Destination = 'File'; SaveDirectory = $saved; FileNamePattern = 'fixture.{ext}'
    })
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $data.FullName 'settings.json') -Encoding utf8NoBOM
```

撮影用の単色ウィンドウを前面に置き、64×40px全体がそのウィンドウの内側に入る左上の物理座標を指定します。

```powershell
$x = [int](Read-Host '左上の物理X座標')
$y = [int](Read-Host '左上の物理Y座標')
$stdout = Join-Path $run 'stdout.txt'
$stderr = Join-Path $run 'stderr.txt'
$process = Start-Process -FilePath (Join-Path $app 'SSCapture.exe') -WindowStyle Hidden -Wait -PassThru `
    -ArgumentList @('--capture-region', $x, $y, 64, 40, '--input-preset', 'fixture-input', '--output-preset', 'fixture-output') `
    -RedirectStandardOutput $stdout -RedirectStandardError $stderr
$process.ExitCode
Get-Content -LiteralPath $stdout,$stderr -Encoding utf8
Get-Item -LiteralPath (Join-Path $saved 'fixture.png')
```

成功時は終了コード0と、標準出力に `保存しました: <絶対パス>` が返ります。画像は上下左右に2pxの余白を含む68×44pxです。不明なプリセットIDは終了コード1と標準エラーで分かります。選択方式のキャンセルは終了コード2です。

## ローカルで実行する

```powershell
pwsh -File scripts/setup-media.ps1
pwsh -File scripts/build.ps1
pwsh -File scripts/start.ps1 -Settings
```

発行先は`artifacts/app`です。既存の常駐版をトレイから終了してからビルドします。ビルドは対象プロセスを自動停止せず、依存物・稼働検査、通常テスト、発行の順で進みます。ローカル版にはFFmpeg/FFprobeをコピーし、.NET 10 Desktop Runtimeは別途必要です。SDKにもDesktop Runtimeを含みます。

## 公開用インストーラーを生成する

```powershell
pwsh -File scripts/setup-package.ps1
pwsh -File scripts/package.ps1
```

`setup-package.ps1`は公式Inno Setup 6.2.2と展開ツールをSHA256照合後、`.tools`へ展開します。システムへのインストール・レジストリ登録は行いません。指定版が配置済みなら再取得しません。

`package.ps1`は通常テスト後、毎回新しい`dev/history/vX.Y.Z/logs/package-<識別子>/app`へWindows x64用ランタイム同梱版を発行します。テストも同じ作業先の`tests`へ分離し、起動中の検証画面を上書きしません。利用中の`artifacts/app`や`data`は配布元に使いません。FFmpeg/FFprobe、OneOCR、PDB、個人設定を含めず、`release/v0.1.9`へ次の3ファイルを生成します。

- `SSCapture-0.1.9-setup.exe`
- `SSCapture-packages.json`
- `SSCapture-SHA256SUMS.txt`

版は`tool.json`から取得し、実行ファイル・アセンブリ・ランタイム同梱を検査します。実プッシュ・タグ・Release公開は行いません。公開時は[Release規則](../dev/RELEASE_RULE.md)に従います。

明示された公開用生成のみ `-PublicRelease` を指定します。目録の公開状態と未署名種別が切り替わりますが、外部公開は別工程です。生成setupは非実行展開し、発行元と両配置の全ファイル・目録を照合します。秘密情報検査と公式最新ランタイム版の検査が完了しない場合は生成を止めます。

同梱ライセンスはNAudio、実際に使用した.NET Runtime Pack、Inno Setupから取得します。Inno Setup用の`.iss`は日本語を正しく扱うためUTF-8 BOM付きで保存します。


## READMEの撮影

先に`pwsh -File scripts/test.ps1 -Suite Preset`と同じ`-Suite Menu`を実行します。専用のビルド出力を使い、設定はSSCapture.exeの`--ui-preview`、範囲と撮影メニューはSSCapture.Tests.exeの`--capture-menu-preview <専用出力先>`で開きます。後者は合成画像を使い、実画面取得とクリップボード操作をしません。

設定・入力・出力・範囲・メニュー・結果通知の6場面をREADMEへ掲載しました。範囲確定後は専用先のPNGと寸法を読み戻して確認します。実撮影の受入とは区別してください。
