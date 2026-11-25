// フレーム描画（設計プラン 5章）。
// 時刻 t のフレームを SkiaSharp で描く関数を1つだけ作る。
// プレビューと書き出しは同じ描画コードを使うため、表示の一致が保たれる。
// 映像フレームは Editor.Core の口（IVideoFrameSource / IStreamingFrameSource）経由で取得する。テキストはエフェクト無しなら直接描き、
// エフェクト付きならフルフレームの透明オフスクリーンに描いてから
// 映像クリップと同じエフェクト適用経路（DrawBitmapWithTransform）に通す。

using System.IO;
using System.Runtime.InteropServices;
using Editor.Core;
using SkiaSharp;

namespace Editor.Render;

/// <summary>時刻 t のフレームを描く口。</summary>
public interface IFrameRenderer
{
    Task<SKBitmap> RenderFrameAsync(
        EditorProject project, double timeSeconds, int width, int height,
        CancellationToken ct = default);
}

/// <summary>タイムラインの評価結果を1フレームに描く。bandProvider があれば周波数帯駆動バインドも評価する（プレビュー・書き出し共通）。visualizationProvider があれば可視化クリップも描く。</summary>
public sealed class TimelineFrameRenderer : IFrameRenderer
{
    private readonly IVideoFrameSource _frameSource;
    private readonly IStreamingFrameSource? _streamingSource;
    private readonly IAudioBandEnergyProvider? _bandProvider;
    private readonly IAudioVisualizationProvider? _visualizationProvider;

    /// <summary>エコー（残像）用の過去フレーム履歴。ClipCompositor と共有する。</summary>
    private readonly EchoHistory _echoHistory;

    /// <summary>クリップ画像の変形・合成（エフェクト適用）を担う。</summary>
    private readonly ClipCompositor _compositor;

    public TimelineFrameRenderer(
        IVideoFrameSource frameSource, IStreamingFrameSource? streamingSource = null,
        IAudioBandEnergyProvider? bandProvider = null,
        IAudioVisualizationProvider? visualizationProvider = null)
    {
        _frameSource = frameSource;
        _streamingSource = streamingSource;
        _bandProvider = bandProvider;
        _visualizationProvider = visualizationProvider;
        _echoHistory = new EchoHistory();
        _compositor = new ClipCompositor(_echoHistory);
    }

    public async Task<SKBitmap> RenderFrameAsync(
        EditorProject project, double timeSeconds, int width, int height,
        CancellationToken ct = default)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Black);

        var states = TimelineEvaluator.EvaluateProject(project, timeSeconds, _bandProvider);
        foreach (var state in states)
        {
            ct.ThrowIfCancellationRequested();
            var clip = state.Clip;

            switch (clip.Kind)
            {
                case ClipKind.Video:
                    await DrawVideoClipAsync(canvas, clip, state, timeSeconds, width, height, ct).ConfigureAwait(false);
                    break;
                case ClipKind.Image:
                    DrawImageClip(canvas, clip, state, width, height);
                    break;
                case ClipKind.Text:
                    TextClipRenderer.Draw(canvas, clip, state, width, height, _compositor);
                    break;
                case ClipKind.Visualization:
                    VisualizationRenderer.DrawClip(canvas, clip, state, project, timeSeconds, width, height, _visualizationProvider);
                    break;
                case ClipKind.Audio:
                    // 音声クリップは映像を持たない。
                    break;
            }
        }

        // このフレームで描かれなかったクリップのエコー履歴は捨てる
        // （クリップが途切れた後の再登場で古い残像が出ないようにする）。
        if (_echoHistory.Count > 0)
            _echoHistory.PruneInactive(states.Select(s => s.Clip.Id));

        return bitmap;
    }

    private async Task DrawVideoClipAsync(
        SKCanvas canvas, Clip clip, EvaluatedClipState state, double timeSeconds,
        int width, int height, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(clip.SourcePath))
            return;

        // 素材内の時刻は Clip.GetSourceTimeSeconds に統一する（速度・逆再生の両方を
        // 含む唯一の写像。プレビューと書き出しは同じここを通る）。
        // 従来の順再生はトランジション区間でクリップ範囲外の時刻をフレーム取得側に
        // 任せていたが、写像側で使用範囲に丸める方が安全（範囲外の時刻は端のフレームに
        // なる。ディゾルブ区間では端フレームが薄く重なるだけで、見た目の差はほぼ無い）。
        var sourceTime = clip.GetSourceTimeSeconds(timeSeconds);

        // まず常駐ストリームから取る。失敗したら単発抽出にフォールバックする。
        if (_streamingSource is not null)
        {
            try
            {
                var frame = await _streamingSource.GetFrameAsync(clip.SourcePath, sourceTime, width, ct)
                    .ConfigureAwait(false);
                if (frame is not null)
                {
                    using var streamBitmap = CreateBitmapFromBgra(frame);
                    _compositor.DrawBitmapWithTransform(canvas, streamBitmap, state, width, height);
                }
                return; // フレーム無し（終端より後）は黒のまま。
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // フォールバックへ。
            }
        }

        var png = await _frameSource.GetFramePngAsync(clip.SourcePath, sourceTime, width, ct)
            .ConfigureAwait(false);
        if (png is null)
            return;

        using var frameBitmap = SKBitmap.Decode(png);
        if (frameBitmap is null)
            return;

        _compositor.DrawBitmapWithTransform(canvas, frameBitmap, state, width, height);
    }

    /// <summary>BGRA の生画素を SKBitmap にコピーする。</summary>
    private static SKBitmap CreateBitmapFromBgra(VideoFrameData frame)
    {
        var bitmap = new SKBitmap(new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        Marshal.Copy(frame.Bgra, 0, bitmap.GetPixels(), frame.Bgra.Length);
        return bitmap;
    }

    private void DrawImageClip(
        SKCanvas canvas, Clip clip, EvaluatedClipState state, int width, int height)
    {
        if (string.IsNullOrEmpty(clip.SourcePath) || !File.Exists(clip.SourcePath))
            return;

        using var image = SKBitmap.Decode(clip.SourcePath);
        if (image is null)
            return;

        _compositor.DrawBitmapWithTransform(canvas, image, state, width, height);
    }
}
