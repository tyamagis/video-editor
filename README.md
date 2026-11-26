# VideoEditor

Museに指示だけ出して作らせる動画編集ソフト。

## できること

### 編集

- マルチトラック タイムライン（ビデオ／オーディオ／テキスト）
- カット、トリミング、分割、移動、リップル削除、スナップ、クリップのグループ化
- クリップの速度変更と逆再生
- キーフレーム（位置・スケール・回転・不透明度／線形・イージング・ホールド補間）
- Undo / Redo

### 音まわり

- 波形表示（表示・非表示の切り替え可。解析データは保持される）
- BPM 手動設定と拍グリッド表示（フレーム／秒／拍の切り替え、拍へのスナップ）
- スペクトラム表示、リサジュー表示
- 周波数帯域（例：20–80Hz）の音量でエフェクトのパラメータを駆動
- 音量フェード、ダッキング、無音区間の検出、ノイズ除去
- テキスト・歌詞テロップ、歌詞ファイルの取り込み

### 映像エフェクト

- 色調補正、LUT 適用（.cube、プロジェクト相対パスで保存）
- グロー、ブラー、グリッチ、ワープ、シャープン、エンボス
- クロマキー、ブレンドモード、トランジション

プレビューと書き出しは同じ評価経路（`TimelineFrameRenderer`）を使うため、画面で見た結果と書き出した結果が一致する。

### 書き出し

- MP4 / H.264（1080p・4K、30fps・60fps、CRF・プリセット指定可）
- 音声ミックス込み（フェード／ダッキングを反映）
- 単体クリップの書き出しにも対応

### UI

- ダークテーマ（チャコール基調にマゼンタ1色のアクセント）、フラットでミニマルなデザイン
- 素材プール／プレビュー／インスペクタ／タイムラインの4ペイン構成
- プレビュー解像度切替（フル／1/2／1/4）。画質の確認と、タイミング作業の軽さを使い分けられる

## 技術スタック

- C# / .NET 8、WPF
- SkiaSharp（描画）
- NAudio（音声読み取り）
- MathNet.Numerics（FFT・周波数帯エネルギー計算）
- FFmpeg / ffprobe（OS にインストール済みのものを利用。同梱しない）

## 構成

```
VideoEditor.sln
src/
  Editor.Core/    編集ドメインモデル（プロジェクト、トラック、クリップ、キーフレーム、BPM・拍グリッド、評価エンジン、JSON保存）
  Editor.Media/   FFmpeg 連携（プローブ、サムネイル／フレーム取得、MP4書き出し）
  Editor.Audio/   波形読み取り、FFT・周波数帯エネルギー、無音検出
  Editor.Render/  SkiaSharp 描画（フレーム描画、プレビュー、タイムライン書き出し）
  Editor.App/     WPF アプリ（4ペイン UI、ダークテーマ）
tests/
  Editor.Tests/   単体テスト（991件）
```

依存関係：App → Core / Media / Audio / Render。Render → Core / Media / Audio。Media・Audio → Core。
Core は UI・FFmpeg・NAudio・SkiaSharp に依存しない純粋なドメイン層で、編集ロジックと評価エンジンはここに集約されている。

## セットアップ（Windows）

必要なもの：

- Visual Studio 2022（「.NET デスクトップ開発」ワークロード）
- .NET 8 SDK
- ffmpeg / ffprobe（PATH が通っていること）

```powershell
ffmpeg -version
ffprobe -version
```

手順：

1. `VideoEditor.sln` を Visual Studio 2022 で開く。
2. ソリューションを右クリック → 「NuGet パッケージの復元」（初回のみ）。
   使うパッケージ：SkiaSharp 2.88.9、SkiaSharp.Views.WPF 2.88.9、NAudio 2.2.1、FFMpegCore 5.1.0、MathNet.Numerics 5.0.0。
3. 構成を `Debug` / `Any CPU` にしてビルド（Ctrl+Shift+B）。
4. `Editor.App` をスタートアップ プロジェクトにして実行（F5）。

ffmpeg の実行ファイルの場所を変えたい場合は `Editor.Media/FfmpegSettings.cs` の `FfmpegExePath` / `FfprobeExePath` を変える。

プロジェクトファイルは `.veproj`（JSON、バージョン番号付き）。

## テスト

```powershell
dotnet test VideoEditor.sln
```

ビルドと単体テストは検証済み。WPF の実機表示・操作感は Windows 側で確認する。

## このプロジェクトについて

個人利用のみを想定。他環境での動作は保証しない。
