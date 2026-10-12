# SSCapture v0.1.0

## GitHub開発版インストーラー

[Releases](https://github.com/KotorinChunChun/SSCapture/releases)でWindows 11 x64向けのランタイム同梱インストーラーを配布します。未署名の開発版です。設定・保存した撮影結果は削除時も保持します。

公開インストーラーにはFFmpeg／FFprobeとOneOCRの外部ファイルを同梱しません。録画には信頼できる配布元から取得した`ffmpeg.exe`と`ffprobe.exe`を導入先の`tools`へ配置する必要があります。[FFmpeg公式の取得案内](https://ffmpeg.org/download.html)を参照してください。録画依存物がない場合も画面撮影は利用できます。

Windows 11用の常駐スクリーンショット・領域録画ツールです。クリップボード・ファイル保存・外部アプリへの出力を個別にON/OFFできます。既定はクリップボードだけONです。編集は指定した外部アプリで行います。

## 起動

ビルド済みの `artifacts/app/SSCapture.exe` を実行します。リポジトリのルートから設定画面を表示して起動する場合:

```powershell
pwsh -File scripts/start.ps1 -Settings
```

| 操作 | 既定のキー・方法 |
| --- | --- |
| 静止画 | Win＋Shift＋S / PrintScreen |
| 録画 | Win＋Shift＋R → 領域選択 → 開始ボタン |
| 即時撮影 | Win＋Shift＋1～9 / 0（画面1は主画面） |
| 前回範囲の再撮影 | Win＋Ctrl＋Shift＋S |
| 設定 | トレイをダブルクリック |
| 終了 | トレイを右クリック → 終了 |

撮影と録画のキーは設定で変更できます。通常選択では左ドラッグで範囲、左クリックだけでウィンドウ、中央クリックで画面全体を選び、Escapeでキャンセルします。

## 案内

- [使い方](docs/usage.md): 撮影・録画・OCR命名・登録範囲・履歴・通知・設定
- [開発・ビルド・更新手順](dev/development.md): 環境準備、検証後の発行、版管理
- [検証状況と未確認の範囲](dev/validation.md): 今回の確認状況と過去版の実績
- [v0.1.0の要求](dev/v0.1.0-req.md) / [実装計画・報告](dev/v0.1.0-imp.md) / [進捗](dev/v0.1.0-task.md)
- [対応しない事項](dev/reject.md) / [第三者ライセンス・配布元](THIRD_PARTY.md)

Windows 11 x64と.NET 10 Desktop Runtimeが対象です。設定と作業データは実行ファイル横の `data` に保存します。実行フォルダは書き込み可能な場所を使い、利用済みの `artifacts/app` をそのまま配布しないでください。
