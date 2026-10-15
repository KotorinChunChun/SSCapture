# SSCapture セキュリティ診断・改善実装レポート

診断日: 2026-10-15。対象: `4bdc693` / feature/full-refactoring、製品v0.1.0。開始時clean。撮影/録画の保存、外部エディター/OCR/FFmpeg起動、管理IPC、NuGet依存を静的診断した。

## 指摘と改善

|ID|重大度・成立条件|証拠|改善・実装状況|
|---|---|---|---|
|SSC-01|中。同一利用者からの巨大pipe要求/巨大requestIdを含む未読応答で、メモリ消費/管理受付停止。|`src/SSCapture/ManagementEndpoint.cs:45` の読取りに長上限なし、`:86` の書込みにdeadlineなし。|共通IPC契約として上限と書込み期限を追加。高/致命的ではないため報告まで。|
|SSC-02|低/運用。録画対象の個人情報、外部FFmpeg/OneOCRのネイティブ安全性はソース/NuGet監査だけでは保証できない。|`src/SSCapture/Recording/MediaProcess.cs:22`、`Services/OneOcr.cs:94`。|外部依存版の台帳化と更新方針、保存先アクセス管理を検討。具体的CVEの成立は未確認。|

`Services/OutputRouter.cs:222` は外部エディターを絶対パスのEXEに限定し、`:231` でArgumentList/UseShellExecute=falseを使用。保存は既存ファイルを上書きしない移動で確定（同ファイル:198）。`Services/ExplorerSelection.cs:20` はWindows配下のExplorerを選択しシェルなしで起動。管理pipeはCurrentUserOnlyと2秒読取り期限がある。高/致命的の確定事項は見つからなかった。

## 依存・秘密・検証

`dotnet list src/SSCapture/SSCapture.csproj package --vulnerable --include-transitive --format json --no-restore` を2026-10-15に実行し、終了コード0、NuGet.orgからの報告対象0。製品依存はNAudio.Wasapi 3.1.0。既存restore資産での照合でありWindows/.NETや外部codecは別途評価が必要。

追跡テキスト166ファイルのPEM秘密鍵/GitHub/AWS/OpenAI代表パターン値非表示検査は一致0。無視対象/過去履歴/一般パスワードは未検査。

- [x] 静的診断・依存照合・改善案・文書差分検査。
- 製品コード変更なし。文書のみのためビルド・GUIを形式的に実行しない。
- 実撮影・録音・個人設定・ユーザーファイル・導入・外部公開・親gitlink更新なし。
