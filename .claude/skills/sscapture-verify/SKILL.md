---
name: sscapture-verify
description: SSCaptureの変更をビルド・テストで検証し、結果を記録する手順。コードやテストを変更したあと、リファクタリングの前後比較、失敗したテストの原因の切り分け（環境依存か実装か）、検証結果を実装書へ書くときに使う。ドキュメントだけの変更や、質問への回答には使わない。
---

# SSCaptureの検証手順

検証の規則そのものは[AGENTS.md](../../../AGENTS.md)の「検証」にある。ここでは実行の手順と結果の読み方をまとめる。コマンドはリポジトリのルートで `pwsh` から実行する。

## 1. SDKを用意する

`dotnet` がPATHにない、またはSDKがないPCでも、プロジェクト内の `.tools` に指定版を入れて使う。システムのPATHは変更しない。

```powershell
pwsh -File scripts/setup-sdk.ps1
```

`scripts/common.ps1` が `.tools/dotnet/dotnet.exe` を優先して使う。

## 2. 全体テストを実行する

```powershell
pwsh -File scripts/test.ps1 *> <ログの保存先>/test.log
```

- NuGet復元、Releaseビルド、通常テストの順に進む。全体で5〜7分かかるので、バックグラウンドで実行して完了を待つ。
- ログは `dev/history/vX.Y.Z/logs/<実行識別子>/`（Git除外）か、作業用の一時フォルダに残す。
- 結果は最終行の `RESULT: <成功> passed, <失敗> failed` と、`FAIL ` で始まる行で読む。ビルドの警告・エラーは `warning CS` / `error CS` を検索する。
- `-Suite`（Notification、Command、Pinned、Preset、Recipe、Snap、Menu、Output、Recording、Startup）は、修正中に早く確かめるために使う。完了の判定には使わない。

## 3. 変更前と比べる（リファクタリング・不具合修正）

変更前の結果を基準として先に保存しておき、変更後と比べる。

```powershell
pwsh -File .claude/skills/sscapture-verify/compare-test-logs.ps1 -Before <変更前のログ> -After <変更後のログ>
```

テストごとの成否が一致すれば「一致」と表示して終了コード0を返す。差があれば、増えた・消えた・成否が変わったテストを表示して終了コード1を返す。リファクタリングでは一致が条件になる。不具合の修正では、意図したテストだけが変わったことを確かめる。

## 4. 失敗の原因を切り分ける

失敗が残ったら、すぐに「環境依存」と判断しない。変更前のコミットでも同じ条件で再現するか確かめる。

```powershell
$base = '<一時フォルダ>/wt-before'
git worktree add --detach $base <確かめたいコミット>
New-Item -ItemType Junction -Path "$base/.tools" -Target (Resolve-Path .tools)
pwsh -File "$base/scripts/test.ps1" *> <一時フォルダ>/before.log
# 片付け: 先にジャンクションだけを外す（中身のSDKを消さないため）
cmd /c rmdir "$base\.tools"
git worktree remove --force $base
```

- 直前のコミットで結果が変わるなら、実装かテストの問題として扱う。
- 2つのテストの成否が入れ替わるなら、要求どうしが矛盾している可能性が高い。片方を黙って直さず、どちらを優先するかをユーザーに確認する。
- 配置・寸法を検査するUIテストは、表示倍率やフォントの影響を受けることがある。それでも上の手順で、変更前のコミットでも再現することを確かめてから環境依存と判断する。

## 5. 実画面・実入力のスモーク

`pwsh -File scripts/test.ps1 -Interactive`、`-NativeSmoke <種類>`、テストDLLの `--*-smoke` は、実際のマウス・キー・画面を使う。PCの操作を止める必要があるので、ユーザーの指示があるときだけ実行する。実行していない場合は、報告に「未実施」と書く。合成画像のテストの成功を、実機で確認したことにしない。

## 6. 結果を記録する

`dev/history/vX.Y.Z/vX.Y.Z-imp.md` の該当する節に、次を書く。

- ビルドの警告・エラーの件数
- 全体テストの成功・失敗の件数と、失敗したテスト名
- 変更前と比べたかどうか、比べた結果
- 実施していない検証（実画面・実マルチモニター・メディア・OCRなど）

過去の記録に誤りがあった場合は、元の文を消さず、日付を付けた追記で訂正する。
