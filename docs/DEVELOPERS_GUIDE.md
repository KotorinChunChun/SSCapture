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

`package.ps1`は通常テスト後、毎回新しい`dev/history/vX.Y.Z/logs/package-<識別子>/app`へWindows x64用ランタイム同梱版を発行します。テストも同じ作業先の`tests`へ分離し、起動中の検証画面を上書きしません。利用中の`artifacts/app`や`data`は配布元に使いません。FFmpeg/FFprobe、OneOCR、PDB、個人設定を含めず、`release/v0.1.6`へ次の3ファイルを生成します。

- `SSCapture-0.1.6-setup.exe`
- `SSCapture-packages.json`
- `SSCapture-SHA256SUMS.txt`

版は`tool.json`から取得し、実行ファイル・アセンブリ・ランタイム同梱を検査します。実プッシュ・タグ・Release公開は行いません。公開時は[Release規則](../dev/RELEASE_RULE.md)に従います。

明示された公開用生成のみ `-PublicRelease` を指定します。目録の公開状態と未署名種別が切り替わりますが、外部公開は別工程です。生成setupは非実行展開し、発行元と両配置の全ファイル・目録を照合します。秘密情報検査と公式最新ランタイム版の検査が完了しない場合は生成を止めます。

同梱ライセンスはNAudio、実際に使用した.NET Runtime Pack、Inno Setupから取得します。Inno Setup用の`.iss`は日本語を正しく扱うためUTF-8 BOM付きで保存します。
