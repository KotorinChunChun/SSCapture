# Release規則

[README](../README.md) / [ビルド手順](../docs/DEVELOPERS_GUIDE.md) / [検証状況](validation.md)

## 識別情報・配布形式

| 項目 | 値 |
| --- | --- |
| リポジトリ | `KotorinChunChun/SSCapture`、main |
| 現在の版・タグ | `0.1.1` / `v0.1.1` |
| AppId | `KotorinToys.SSCapture` |
| 導入先の既定 | `{localappdata}\Programs\SSCapture` |
| 対象 | Windows 11 x64、ユーザー単位 |
| 形式 | Inno Setupインストーラー、.NET 10 Desktop Runtime同梱 |
| 公開状態 | 未署名のprereleaseを継続 |
| 公開用出力先 | `release/v0.1.1/`、Git除外 |

v0.1.0の公開EXEを実行せず、SHA256照合とヘッダー展開でAppId・導入先を確認した。AppId・導入先・設定JSON形式は変更しない。既存導入先は`UsePreviousAppDir`で継承する。稼働中はAppMutexで更新を止め、強制終了しない。アプリの終了後に更新する。

`data`、利用者の`tools`、撮影ファイルはインストーラーの上書き・アンインストール対象にしない。設定移行のために利用者のJSONを配布物へ混ぜない。署名の追加・配布形式変更・外部公開は別の明示指示で行う。

## 生成と検査

```powershell
pwsh -File scripts/setup-package.ps1
pwsh -File scripts/package.ps1
```

通常テスト後、新規発行先から生成する。FFmpeg/FFprobe/OneOCR/onnxruntime・個人設定・PDBを除外し、版とランタイム同梱を検査する。公開用の3ファイルはEXE、packages.json、SHA256SUMSとする。その他の`release/work`は公開しない。目録の`signature=unsigned-public-build`は署名済みを意味しない。

再生成するとインストーラーのタイムスタンプ等によりSHA256は変わり得る。公開するEXEと同時生成したJSON・SHA256SUMSを組で扱う。GitHubへ掲載済みの版・タグ・成果物は上書きせず、変更時は次の版を作る。

使用ツールは[公式Inno Setup 6.2.2](https://github.com/jrsoftware/issrc/releases/tag/is-6_2_2)と[innoextractのWindowsビルド](https://github.com/UserUnknownFactor/innoextract_win/releases/tag/670)。取得物のSHA256はsetup-package.ps1に固定する。展開のみ行い、システムのインストール・PATH・証明書ストアを変更しない。ライセンスと配布元は[THIRD_PARTY.md](../THIRD_PARTY.md)に記録する。

## 公開前の確認

- 要求・実装報告を完了記録へ移し、現行文書のリンク・版を整合させる。
- Releaseビルド・通常回帰・ビルドスクリプトのfixture検証を成功させ、実機測定との境界をvalidation.mdに残す。
- 配布物の版、同梱ランタイム、除外物、JSON・SHA256SUMSとの一致を確認する。インストールを実行していない場合は報告にその範囲を残す。
- `git diff --check`、クリーンな作業ツリー、`git fetch origin`後の分岐なしを確認する。
- ローカル常駐版の更新を行う場合は正常終了し、個人設定を保持して再起動する。

## 明示指示を受けて公開するとき

以下は準備完了だけでは実行しない。GitHub CLI認証済みのPowerShell 7で、対象ブランチ・未使用タグ・EXEのSHA256を再確認して実行する。

```powershell
git fetch origin --prune
git status --short
git rev-list --left-right --count origin/main...HEAD
Get-FileHash release/v0.1.1/SSCapture-0.1.1-setup.exe -Algorithm SHA256
git push origin main
$releaseCommit = git rev-parse HEAD
gh release create v0.1.1 release/v0.1.1/SSCapture-0.1.1-setup.exe release/v0.1.1/SSCapture-packages.json release/v0.1.1/SSCapture-SHA256SUMS.txt --repo KotorinChunChun/SSCapture --target $releaseCommit --title 'SSCapture v0.1.1' --notes-file docs/releases/v0.1.1.md --prerelease
```

mainだけをプッシュする。不要な過去タグの一括送信・force pushを行わない。Release作成後はタグの対象コミット、3資産のサイズ・SHA256、ノートを確認し、実公開URLを報告する。
