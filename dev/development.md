# 開発・ビルド・更新手順

[README](../README.md) / [使い方](../docs/usage.md) / [検証状況](validation.md)

コマンドはリポジトリのルートで、PowerShell 7の `pwsh` を使って実行します。このフォルダだけがGit管理の対象です。

## 現在の開発記録

- [v0.1.0の要求](v0.1.0-req.md)
- [v0.1.0の実装計画・点検報告](v0.1.0-imp.md)
- [v0.1.0の進捗](v0.1.0-task.md)
- [共通ルール](../AGENTS.md) / [対応しない事項](reject.md)

現在の版は `v0.1.0` です。以後は `vX.Y.Z` の通常の連番を使い、過去の `v0.YYYYMMDD.Z` の文書は履歴として維持します。新しい版の要求・計画を `dev/vX.Y.Z-req.md`・`dev/vX.Y.Z-imp.md` に記録し、必要な場合にだけ `task.md` を作成します。フェーズごとに検証結果を記録し、問題がなければコミットします。却下事項にはユーザーが明示したものだけを記録し、未検証項目は [validation.md](validation.md) に分けます。

## 主要構成と変更時の不変条件

| 場所 | 役割・守ること |
| --- | --- |
| `src/SSCapture/TrayApplicationContext.cs` | 常駐・ショートカット・画面・出力の調停。UI更新はUIスレッドへ戻し、処理中の二重開始を防ぐ |
| `src/SSCapture/Capture` | 画面取得と選択。座標は負座標を含む物理ピクセル。通常撮影は選択UI表示前の画像を保持する |
| `src/SSCapture/Recording` | 録画区間・音声時刻・FFmpegプロセス。一時停止時間を完成動画へ含めず、音声トグルは今回のセッションだけへ適用する |
| `src/SSCapture/Services` | 設定、OCR、命名、保存、独立出力、履歴、通知。成功済み出力と未完了・失敗の出力を区別する |
| `src/SSCapture/UI` | 設定・OCR・範囲・履歴・録画のWinForms画面。データ処理の結果と所有権を明確にし、システムダイアログを追加しない |
| `tests/SSCapture.Tests` | 通常の合成テストと明示指定するスモークを分離。通常実行で実画面・実録音・個人クリップボードを扱わない |

- `CaptureSession.SelectAsync` はSTAのUIスレッドから呼びます。セッションがSnapshotを所有・解放し、呼び出し元が返却された `CaptureOutcome` を解放します。非同期出力は所有する画像のコピーを使い、処理完了前に元データを解放しないようにします。保存OFFの履歴はメモリー内だけに保持します。
- 録画の開始・停止・音声切替は既存のゲートで直列化します。デバイス初期化・取得終了待ちをUIスレッドへ依存させず、停止時に映像・音声の取得を解放します。音声OFF区間の生音をWAVへ残さず、途中で音源を追加しても同じ区間時計で無音を補います。失敗時の区間原本・作業ファイルは回収用に保持します。
- `OutputRouter` はコピー・保存・外部アプリの結果を独立して扱います。OCR命名より先に成功したコピーを、確定・キャンセル時にやり直しません。元の撮影日時と、命名画面で編集されたファイル名日時を混同しないようにします。
- `DeliveryWait` はOCR入力待ちと出力の10秒待機を調停します。入力中には出力の期限を適用せず、入力終了後の期限切れでも出力そのものをキャンセルしません。実処理の例外・キャンセルを待機期限切れとして扱わないようにします。
- `AtomicFile` は設定・前回範囲の同一ディレクトリ内での一時ファイル作成・flush・原子的置換を共通化します。各Storeの検証、JSON形式、旧設定移行は維持し、失敗時に元ファイルを失わないようにします。設定の読込だけで元のJSONを書き換えません。

## 環境準備

対象はWindows 11 x64、.NET 10 Windows Formsです。ローカルSDKの既定版は10.0.401、録画エンジンはFFmpeg 9.0.2、音声ライブラリはNAudio.Wasapi 3.1.0です。配布元とライセンスは [THIRD_PARTY.md](../THIRD_PARTY.md) に記録しています。

```powershell
pwsh -File scripts/setup-sdk.ps1
pwsh -File scripts/setup-media.ps1
```

SDKは公式配布のSHA512、FFmpegは指定アーカイブのSHA256を確認します。依存物は `.tools` 内に置き、システムのPATHは変更しません。SDKの版はプロジェクトルートを基点に照会し、既存のローカルSDKが指定版と異なる場合、`setup-sdk.ps1` はエラーで終了します。自動更新や既存環境の削除は行いません。

## 検証だけを行う

```powershell
pwsh -File scripts/test.ps1
pwsh -File scripts/test-build.ps1
```

`test.ps1` はテストプロジェクトのNuGet復元、Releaseビルド、通常テストを行います。`test-build.ps1` は生成したfixtureによるビルド手順の安全性検証で、実際の常駐プロセスや個人の `data` は操作しません。

必要な検証を追加する場合:

```powershell
pwsh -File scripts/test-media.ps1
pwsh -File scripts/test-ocr.ps1
```

これらは `--no-restore` で実行するため、先に `test.ps1` を成功させます。メディア検証には `setup-media.ps1` で配置したFFmpeg/ffprobeが必要です。生成映像・生成音声だけを使い、実画面・マイクは取得しません。OCR検証は本機のOneOCRで生成した日英文字画像を認識します。対応するSnipping Toolのエンジン/モデルが必要で、実画面をOCRする試験ではありません。

実マウス・キーによる合成画面の操作検証は別指定です。

```powershell
pwsh -File scripts/test.ps1 -Interactive
```

実行前に常駐版をトレイから終了し、実行中はPC操作を控えます。このテストは入力操作を行いますが、画面の画像取得・クリップボード変更は行いません。別途UI画像を確認する場合も、撮影は検証対象ウィンドウ内に限定します。実機確認と合成テストの結果は分けて記録します。

## 発行と常駐版の更新

撮影・録画・OCRやファイル処理が完了してから、利用者がトレイの「終了」で常駐版を終了します。ビルドスクリプトはプロセスを自動停止しません。

```powershell
pwsh -File scripts/build.ps1
```

NuGet復元が済んでおり、再復元を省く場合は次の指定を使います。

```powershell
pwsh -File scripts/build.ps1 -NoRestore
```

`build.ps1` の順序は次のとおりです。

1. 依存ファイル・ライセンスと、対象の常駐アプリ／FFmpeg／ffprobe／dotnet・corerunホストの稼働を検査する。不足・稼働、または必要な稼働情報を確認できない場合は発行前に終了する。
2. テストプロジェクトを復元する（`-NoRestore` では省略）。
3. テストプロジェクトをReleaseビルドし、通常テストを `--no-build --no-restore` で実行する。
4. 依存ファイル・ライセンスと対象プロセスの稼働を再検査する。
5. 検証済みのReleaseを `--no-build` で従来の `artifacts/app` へ発行し、メディア実行ファイルとライセンスをコピーする。

実行中と報告された場合は、その処理の終了を確認してからやり直します。稼働確認は自動停止の代わりではありません。更新後は次のコマンドで起動し、タイトル・製品バージョンと、設定・前回範囲が保持されたことを確認して記録します。

```powershell
pwsh -File scripts/start.ps1 -Settings
```

## 範囲選択画面の実モニタ配置検証

通常テストのビルド後、次のコマンドで実モニタごとの配置を確認できます。単色の検証画面が短時間表示されます。画面撮影・音声取得・クリップボード変更は行いません。

```powershell
dotnet tests/SSCapture.Tests/bin/Release/net10.0-windows/SSCapture.Tests.dll --overlay-placement-smoke
```

FancyZonesの「新規ウィンドウを現在のアクティブモニタへ移動」が有効な環境では、表示後の物理座標・クライアントサイズ・自動移動印を確認します。FancyZonesが停止中の成功は、FancyZonesとの併用確認を意味しません。通常テストでは、非表示の実HWNDとハンドル再作成後のツールウィンドウ属性を確認します。

開始画面限定設定とポインタ制限の実機検証は次のコマンドで行います。単色の選択画面を表示して実ポインタを動かすため、実行中はPC操作を控えてください。画面画像・音声・クリップボードは取得しません。

```powershell
dotnet tests/SSCapture.Tests/bin/Release/net10.0-windows/SSCapture.Tests.dll --selection-monitor-smoke
```

各実モニタで設定ON/OFF、4辺からのポインタ逸脱防止、確定・Esc・非アクティブ化・設定へ移動・画像取得例外の終了経路を確認します。終了後は元のポインタ位置と制限へ戻します。

## ファイルと配布の扱い

| 場所 | 内容 |
| --- | --- |
| `artifacts/app` | ローカル発行先。利用後は個人設定・作業データも含み得る |
| `.tools` | SDK、NuGetキャッシュ、FFmpegなどのローカル依存物 |
| 実行ファイル横の `data/settings.json` | 個人設定 |
| 実行ファイル横の `data/last-region.json` | 前回の撮影範囲 |
| 実行ファイル横の `data/work` | 外部アプリ／動画クリップボード用ファイル、録画の作業・回収用ファイル |
| 実行ファイル横の `data/oneocr` | 本機のOneOCRエンジン/モデルのローカルキャッシュ |

依存バイナリ・生成物・個人設定・撮影データはGitへ追加しません。OneOCRのMicrosoft DLL/モデルはGitにもビルド時の配布ファイルにも含めません。利用済みの `artifacts/app` はそのまま再配布できるフォルダではありません。今回、配布パッケージの生成や個人データのフォルダ移行は追加していません。

自動起動の登録・解除は [利用案内](../docs/usage.md) を参照してください。初期状態では登録せず、アプリフォルダを移した場合は再登録が必要です。
