# Claude Code向けの入口

このプロジェクトの共通ルールはAGENTS.mdにまとめている。Claude Codeは次の行でAGENTS.mdを読み込む。ルールはAGENTS.mdだけを更新し、このファイルへ重複して書かない。

@AGENTS.md

## 開発文書の命名と役割

| 文書 | 置き場所 | 主に書く人 |
| --- | --- | --- |
| 要求定義書 | `dev/history/vX.Y.Z/vX.Y.Z-req.md` | 開発者。AIは確認で決まったことの追記、進捗のチェック、誤字の修正だけを行う |
| 実装計画・実装報告書 | `dev/history/vX.Y.Z/vX.Y.Z-imp.md` | AI。設計・検証の結果・技術的な知見を追記していく |
| 進捗管理チェック表 | `dev/history/vX.Y.Z/vX.Y.Z-task.md` | AI。要求書だけでは進捗を管理できないときだけ作る |
| 却下事項 | `dev/reject.md` | ユーザーが「実装しない」と明言した事項だけをAIが記録する |

`dev/v0.20261010.*` は旧形式の版の記録で、移動・改名しない。

## 検証の手順

テスト・回帰比較・スモークの扱いは `sscapture-verify` スキル（`.claude/skills/sscapture-verify/SKILL.md`）に従う。
