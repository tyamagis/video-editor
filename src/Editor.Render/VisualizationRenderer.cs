// 可視化クリップの描画（設計プラン 4章）。
// FrameRenderer.cs から切り出した、状態を持たない描画だけのクラス。
// スペクトル・波形オシロスコープ・リサジューを描く。プレビューと書き出しで
// 同じコードを使うため、TimelineFrameRenderer から委譲される。

using Editor.Core;
using SkiaSharp;

namespace Editor.Render;

/// <summary>
/// 可視化クリップ（スペクトル／波形／リサジュー）の描画。状態を持たない。
/// クリップ描画（DrawClip）のほか、プレビューのオーバーレイ表示（PreviewControl）も
/// 同じ共通処理（DrawSpectrumBars / DrawLissajousFrame / BuildLissajousPath）を使う。
/// 描画領域・色・既定値は呼び出し側が渡すため、両方の見た目は従来通りになる。
/// </summary>
public static class VisualizationRenderer
{
    /// <summary>
    /// 可視化クリップを描く（設計プラン 4章）。音源クリップの事前解析（FFT フレーム列・
    /// 間引きステレオ波形）からその時刻のデータを読み、SkiaSharp で描く。
    /// プレビューと書き出しで同じ描画コードを使うため、両方に反映される。
    /// 描画はフレーム全体を基準とし、位置・拡大率・回転のキーフレームは他のクリップと
    /// 同じ変換（中心＋位置、回転、拡大率）で適用する。ブレンドモードも同じく適用する。
    /// 音源がその時刻に流れていない・解析前は描かない。
    /// </summary>
    public static void DrawClip(
        SKCanvas canvas, Clip clip, EvaluatedClipState state, EditorProject project,
        double timeSeconds, int width, int height, IAudioVisualizationProvider? provider)
    {
        var settings = state.Visualization;
        if (settings is null || provider is null)
            return;

        // 音源クリップを探し、タイムライン時刻を素材内時刻へ変換する
        // （バインドの EvaluateBoundParameter と同じ変換）。
        var source = project.Tracks
            .SelectMany(t => t.Clips)
            .FirstOrDefault(c => c.Id == settings.SourceClipId);
        if (source is null || string.IsNullOrEmpty(source.SourcePath))
            return;
        var sourceTime = source.InPointSeconds + (timeSeconds - source.StartSeconds);
        if (sourceTime < source.InPointSeconds - 1e-6
            || sourceTime > source.InPointSeconds + source.DurationSeconds + 1e-6)
            return; // 音源がその時刻に流れていない。
        if (!provider.IsAnalyzed(source.Id))
            return; // 解析前は描かない（解析は ViewModel が裏で走らせる）。

        var color = TimelineFrameRenderer.ParseColor(settings.ColorHex, state.Opacity);

        canvas.Save();
        canvas.Translate((float)(width / 2.0 + state.PositionX), (float)(height / 2.0 + state.PositionY));
        canvas.RotateDegrees((float)state.RotationDegrees);
        canvas.Scale((float)state.Scale, (float)state.Scale);

        switch (settings.Kind)
        {
            case VisualizationKind.Spectrum:
                DrawSpectrum(canvas, settings, provider, source.Id, sourceTime, color, state.BlendMode, width, height);
                break;
            case VisualizationKind.Waveform:
                DrawWaveformVisualization(canvas, settings, provider, source.Id, sourceTime, color, state.BlendMode, width, height);
                break;
            case VisualizationKind.Lissajous:
                DrawLissajous(canvas, settings, provider, source.Id, sourceTime, color, state.BlendMode, width, height);
                break;
        }

        canvas.Restore();
    }

    /// <summary>
    /// スペクトルを棒グラフで描く（共通処理）。barArea は棒が下端から立ち上がる範囲
    /// （高さが棒の最大高さ）。クリップ描画ではフレーム下端から 55% の高さの範囲、
    /// プレビューオーバーレイでは下端の帯の範囲を渡す。色・ゲイン・ブレンドモードは呼び出し側が決める。
    /// </summary>
    public static void DrawSpectrumBars(
        SKCanvas canvas, SKRect barArea, double[] bands, SKColor color,
        double gain = 1.0, SKBlendMode blendMode = SKBlendMode.SrcOver)
    {
        var count = bands.Length;
        if (count == 0)
            return;

        const float gap = 2f;
        var barWidth = (barArea.Width - gap * (count + 1)) / count;
        if (barWidth <= 0)
            return;

        using var paint = new SKPaint
        {
            Color = color,
            IsAntialias = true,
            BlendMode = blendMode
        };
        for (var i = 0; i < count; i++)
        {
            var value = Math.Clamp(bands[i] * gain, 0, 1);
            var barHeight = (float)(value * barArea.Height);
            if (barHeight <= 0)
                continue;
            var x = barArea.Left + gap + i * (barWidth + gap);
            canvas.DrawRect(new SKRect(x, barArea.Bottom - barHeight, x + barWidth, barArea.Bottom), paint);
        }
    }

    /// <summary>
    /// リサジューの枠と十字線を描く（共通処理）。square は軌跡を収める正方形。
    /// 枠色は呼び出し側が決める（クリップ描画は白 40、プレビューオーバーレイは白 60）。
    /// </summary>
    public static void DrawLissajousFrame(SKCanvas canvas, SKRect square, SKColor frameColor)
    {
        using var framePaint = new SKPaint
        {
            Color = frameColor,
            StrokeWidth = 1,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke
        };
        canvas.DrawRect(square, framePaint);
        canvas.DrawLine(square.MidX, square.Top, square.MidX, square.Bottom, framePaint);
        canvas.DrawLine(square.Left, square.MidY, square.Right, square.MidY, framePaint);
    }

    /// <summary>
    /// リサジューの軌跡パスを作る（共通処理）。左チャンネル＝横、右チャンネル＝縦。
    /// 呼び出し側は作ったパスを自分のペイント（色・太さ・ブレンドモード）で描く。
    /// クリップ描画では残像の区間ごとに、プレビューオーバーレイでは窓全体を一度に作る。
    /// </summary>
    public static SKPath BuildLissajousPath(
        float[] left, float[] right, int from, int to,
        float cx, float cy, float half, float gain)
    {
        var path = new SKPath();
        for (var i = from; i < to; i++)
        {
            var x = cx + Math.Clamp(left[i] * gain, -1f, 1f) * half;
            var y = cy - Math.Clamp(right[i] * gain, -1f, 1f) * half;
            if (i == from)
                path.MoveTo(x, y);
            else
                path.LineTo(x, y);
        }
        return path;
    }

    /// <summary>
    /// スペクトル：周波数ごとの強さを下端から立ち上がる棒グラフで描く。
    /// 描画範囲はフレーム幅いっぱい、高さは最大でフレーム高さの 55%（ゲインで伸縮）。
    /// </summary>
    private static void DrawSpectrum(
        SKCanvas canvas, VisualizationSettings settings, IAudioVisualizationProvider provider,
        string sourceClipId, double sourceTime, SKColor color, BlendMode blendMode,
        int width, int height)
    {
        var count = (int)Math.Clamp(settings.BandCount, 4, 128);
        var bands = provider.GetSpectrumBands(
            sourceClipId, sourceTime, count, settings.LowHz, settings.HighHz, settings.Logarithmic);
        if (bands.Length == 0)
            return;

        // 描画は中心原点の座標系で行う。棒はフレーム下端（y = height / 2）から立ち上がる。
        var maxBarHeight = height * 0.55f;
        var barArea = new SKRect(
            -width / 2f, height / 2f - maxBarHeight,
            width / 2f, height / 2f);
        DrawSpectrumBars(canvas, barArea, bands, color, settings.Gain,
            TimelineFrameRenderer.MapBlendMode(blendMode));
    }

    /// <summary>
    /// 波形（オシロスコープ）：直前 WindowSeconds 分のモノラル波形（左右の平均）を
    /// フレーム幅いっぱいの線で描く。振幅はフレーム高さの 42% × ゲインまで。
    /// 描く点は最大 2048 点までに間引く（表示区間 1 秒でも標本は約 4000 点になるため）。
    /// </summary>
    private static void DrawWaveformVisualization(
        SKCanvas canvas, VisualizationSettings settings, IAudioVisualizationProvider provider,
        string sourceClipId, double sourceTime, SKColor color, BlendMode blendMode,
        int width, int height)
    {
        var (left, right) = provider.GetStereoWindow(sourceClipId, sourceTime, settings.WindowSeconds);
        var count = Math.Min(left.Length, right.Length);
        if (count < 2)
            return;

        var points = Math.Min(count, 2048);
        var amplitude = height * 0.42f * (float)settings.Gain;
        var halfHeight = height / 2f;
        using var path = new SKPath();
        for (var p = 0; p < points; p++)
        {
            var i = p * (count - 1) / (points - 1);
            var mono = (left[i] + right[i]) / 2;
            var x = -width / 2f + (float)p / (points - 1) * width;
            var y = Math.Clamp(-mono * amplitude, -halfHeight, halfHeight);
            if (p == 0)
                path.MoveTo(x, y);
            else
                path.LineTo(x, y);
        }
        using var paint = new SKPaint
        {
            Color = color,
            StrokeWidth = (float)settings.LineWidth,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            BlendMode = TimelineFrameRenderer.MapBlendMode(blendMode)
        };
        canvas.DrawPath(path, paint);
    }

    /// <summary>
    /// リサジュー：左チャンネル＝横、右チャンネル＝縦で、直前 TrailSeconds 分の軌跡を描く。
    /// 描画範囲はフレームの短辺の 90% の正方形（薄い枠と十字線付き）。
    /// 残像：軌跡を時間順の区間（約 10ms ずつ）に分け、古い区間ほど薄く描く。
    /// 残像 0（窓 0.02 秒）のときはほぼ残像なしになる。
    /// </summary>
    private static void DrawLissajous(
        SKCanvas canvas, VisualizationSettings settings, IAudioVisualizationProvider provider,
        string sourceClipId, double sourceTime, SKColor color, BlendMode blendMode,
        int width, int height)
    {
        var trail = Math.Max(0.02, settings.TrailSeconds);
        var (left, right) = provider.GetStereoWindow(sourceClipId, sourceTime, trail);
        var count = Math.Min(left.Length, right.Length);
        if (count < 2)
            return;

        var half = Math.Min(width, height) / 2f * 0.9f;
        var gain = (float)settings.Gain;

        // 枠と十字線（薄く）。可視化の範囲の目安。
        DrawLissajousFrame(canvas, new SKRect(-half, -half, half, half), new SKColor(255, 255, 255, 40));

        var samplesPerSlice = Math.Max(2, count / Math.Max(1, (int)Math.Round(trail / 0.01)));
        var sliceCount = (count + samplesPerSlice - 1) / samplesPerSlice;
        for (var s = 0; s < sliceCount; s++)
        {
            var from = s * samplesPerSlice;
            var to = Math.Min(count, from + samplesPerSlice);
            if (to - from < 2)
                continue;
            // 新しい区間ほど濃い（古い区間は残像として薄く残る）。
            var alpha = 0.15 + 0.85 * (s + 1.0) / sliceCount;
            using var path = BuildLissajousPath(left, right, from, to, 0, 0, half, gain);
            using var paint = new SKPaint
            {
                Color = color.WithAlpha((byte)Math.Clamp(color.Alpha * alpha, 0, 255)),
                StrokeWidth = (float)settings.LineWidth,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeCap = SKStrokeCap.Round,
                StrokeJoin = SKStrokeJoin.Round,
                BlendMode = TimelineFrameRenderer.MapBlendMode(blendMode)
            };
            canvas.DrawPath(path, paint);
        }
    }
}
