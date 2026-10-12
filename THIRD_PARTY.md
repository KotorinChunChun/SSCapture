# Third-party components

- OneOCR: 本機にインストールされたMicrosoft Snipping Toolの `oneocr.dll` / `oneocr.onemodel` / `onnxruntime.dll` をローカル実行に利用。キャッシュは実行フォルダ内 `data/oneocr`。これらのファイルはGitとビルド時の配布ファイルには含めません。ネイティブAPIの参考: [OneOCR wrapper](https://github.com/AuroraWright/oneocr)、[Windows11 SnippingTool OCR](https://github.com/b1tg/win11-oneocr)。ラッパーパッケージ自体は依存物に追加していません。

- .NET 10 / Windows Forms: Microsoft / .NET Foundation, MIT license. SDK取得元は `scripts/setup-sdk.ps1` に記録。
- NAudio.Wasapi / NAudio.Core 3.1.0: Mark Heath / NAudio contributors, MIT license. [Source and license](https://github.com/naudio/NAudio/tree/v3.1.0) / [NuGet](https://www.nuget.org/packages/NAudio.Wasapi/3.1.0)。NuGetキャッシュは `.tools/nuget`。
- FFmpeg 9.0.2 essentials Windows x64 build: FFmpeg contributors / Gyan Doshi. このビルドはGPLv3。FFmpegは独立プロセスとして起動します。[FFmpeg download](https://ffmpeg.org/download.html) / [Build provider](https://www.gyan.dev/ffmpeg/builds/) / [Corresponding FFmpeg source](https://github.com/FFmpeg/FFmpeg/commit/946fcce07b)。ビルド設定・外部ライブラリ一覧は配布READMEと `ffmpeg -version` に記載。実行ファイルと配布LICENSE/READMEは `artifacts/app/tools` にコピーします。

FFmpeg ZIP SHA256: `60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba`

FFmpegバイナリを再配布する場合は同梱ライセンスと対応ソースの提供要件を確認してください。このGitリポジトリは依存バイナリを含みません。2026年10月12日のGitHub公開インストーラーでは、対応ソース一式をまだ確保していないためFFmpeg／FFprobeを含めず、利用者による別途配置方式とします。上記の同梱コピーはローカル検証向けの経路です。
