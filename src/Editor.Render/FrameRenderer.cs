// フレーム描画（設計プラン 5章）。
// 時刻 t のフレームを SkiaSharp で描く関数を1つだけ作る。
// プレビューと書き出しは同じ描画コードを使うため、表示の一致が保たれる。
// 映像フレームは Editor.Media から取得し、テキストは直接描く。

using System.IO;
using System.Runtime.InteropServices;
using Editor.Core;
using Editor.Media;
using SkiaSharp;

namespace Editor.Render;

/// <summary>時刻 t のフレームを描く口。</summary>
public interface IFrameRenderer
{
    Task<SKBitmap> RenderFrameAsync(
        EditorProject project, double timeSeconds, int width, int height,
        CancellationToken ct = default);
}

/// <summary>タイムラインの評価結果を1フレームに描く。bandProvider があれば周波数帯駆動バインドも評価する（プレビュー・書き出し共通）。</summary>
public sealed class TimelineFrameRenderer : IFrameRenderer
{
    private readonly IVideoFrameSource _frameSource;
    private readonly IStreamingFrameSource? _streamingSource;
    private readonly IAudioBandEnergyProvider? _bandProvider;

    public TimelineFrameRenderer(
        IVideoFrameSource frameSource, IStreamingFrameSource? streamingSource = null,
        IAudioBandEnergyProvider? bandProvider = null)
    {
        _frameSource = frameSource;
        _streamingSource = streamingSource;
        _bandProvider = bandProvider;
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
                    DrawTextClip(canvas, clip, state, width, height);
                    break;
                case ClipKind.Audio:
                    // 音声クリップは映像を持たない。
                    break;
            }
        }

        return bitmap;
    }

    private async Task DrawVideoClipAsync(
        SKCanvas canvas, Clip clip, EvaluatedClipState state, double timeSeconds,
        int width, int height, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(clip.SourcePath))
            return;

        // 素材内の時刻 ＝ イン点 ＋ タイムライン上での経過時間。
        // トランジションの前半ではクリップ開始より前の時刻になるため 0 で丸める
        // （イン点が 0 の場合は先頭フレームがフェードインする）。
        var sourceTime = Math.Max(0, clip.InPointSeconds + timeSeconds - clip.StartSeconds);

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
                    DrawBitmapWithTransform(canvas, streamBitmap, state, width, height);
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

        DrawBitmapWithTransform(canvas, frameBitmap, state, width, height);
    }

    /// <summary>BGRA の生画素を SKBitmap にコピーする。</summary>
    private static SKBitmap CreateBitmapFromBgra(VideoFrameData frame)
    {
        var bitmap = new SKBitmap(new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        Marshal.Copy(frame.Bgra, 0, bitmap.GetPixels(), frame.Bgra.Length);
        return bitmap;
    }

    private static void DrawImageClip(
        SKCanvas canvas, Clip clip, EvaluatedClipState state, int width, int height)
    {
        if (string.IsNullOrEmpty(clip.SourcePath) || !File.Exists(clip.SourcePath))
            return;

        using var image = SKBitmap.Decode(clip.SourcePath);
        if (image is null)
            return;

        DrawBitmapWithTransform(canvas, image, state, width, height);
    }

    /// <summary>RGBシフトで分けるチャンネル。</summary>
    private enum Channel
    {
        Red,
        Green,
        Blue
    }

    /// <summary>
    /// クリップの画像（映像フレーム・静止画）を変形・合成して描く。
    /// エフェクトはここで適用する（プレビューと書き出しで同じ描画コードを使うため、両方に反映される）。
    /// 適用順：クロマキー（画素処理）→ グロー（ぼかしたコピーを加算）→ 本体
    /// （ブラー、ブレンドモード、RGBシフト、スライス、ワープ）。
    /// </summary>
    private static void DrawBitmapWithTransform(
        SKCanvas canvas, SKBitmap image, EvaluatedClipState state, int width, int height)
    {
        var effects = state.Effects;

        // クロマキー：描く前に画素単位で抜く（この画像は呼び出し側が都度作ったものなので直接書き換えてよい）。
        var chroma = effects.FirstOrDefault(e => e.Kind == EffectKind.ChromaKey);
        if (chroma is not null)
            ApplyChromaKey(image, chroma);

        // ブラーは同種が複数あれば最も強いものを1つだけ使う。
        var blur = effects
            .Where(e => e.Kind == EffectKind.Blur)
            .Select(e => e.Amount)
            .DefaultIfEmpty(0)
            .Max();
        var glow = effects.FirstOrDefault(e => e.Kind == EffectKind.Glow);
        var rgbShift = effects.FirstOrDefault(e => e.Kind == EffectKind.RgbShift)?.Amount ?? 0;
        var slice = effects.FirstOrDefault(e => e.Kind == EffectKind.Slice)?.Amount ?? 0;
        var warp = effects.FirstOrDefault(e => e.Kind == EffectKind.Warp);

        // フレームに収まる倍率 × クリップの拡大率。
        var fit = Math.Min((double)width / image.Width, (double)height / image.Height);
        var scale = fit * state.Scale;

        // グロー：ぼかしたコピーを加算で先に描く（明るい部分がにじむ）。
        if (glow is not null && glow.Amount > 0)
        {
            using var glowFilter = SKImageFilter.CreateBlur((float)Math.Max(1, glow.Radius), (float)Math.Max(1, glow.Radius));
            using var glowPaint = CreateEffectPaint(state, SKBlendMode.Plus, state.Opacity * glow.Amount, glowFilter, null);
            DrawImagePass(canvas, image, glowPaint, width, height, scale, state, 0, 0, null);
        }

        if (rgbShift > 0)
        {
            // RGBシフト：3チャンネルをずらして加算で重ねる。チャンネルを分けて
            // 加算しないと色が再構成できないため、この場合はクリップのブレンド
            // モードは適用されない（加算が優先される）。
            var channels = new[]
            {
                (Channel.Red, -rgbShift),
                (Channel.Green, 0.0),
                (Channel.Blue, rgbShift)
            };
            foreach (var (channel, offsetX) in channels)
            {
                using var blurFilter = CreateBlurFilter(blur);
                using var paint = CreateEffectPaint(state, SKBlendMode.Plus, state.Opacity, blurFilter, channel);
                DrawImagePass(canvas, image, paint, width, height, scale, state, offsetX, slice, warp);
            }
        }
        else
        {
            using var blurFilter = CreateBlurFilter(blur);
            using var paint = CreateEffectPaint(state, MapBlendMode(state.BlendMode), state.Opacity, blurFilter, null);
            DrawImagePass(canvas, image, paint, width, height, scale, state, 0, slice, warp);
        }
    }

    /// <summary>ぼかし量が 0 より大きいときだけブラーフィルタを作る（0 以下は null）。</summary>
    private static SKImageFilter? CreateBlurFilter(double blur)
        => blur > 0 ? SKImageFilter.CreateBlur((float)blur, (float)blur) : null;

    /// <summary>エフェクト描画用のペイントを作る（不透明度・ブレンド・色調補正・ブラー・チャンネル分離）。</summary>
    private static SKPaint CreateEffectPaint(
        EvaluatedClipState state, SKBlendMode blendMode, double alpha,
        SKImageFilter? imageFilter, Channel? channel)
    {
        return new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(255, 255, 255, (byte)Math.Clamp(alpha * 255, 0, 255)),
            ColorFilter = ColorCorrectionFilter(state.Brightness, state.Contrast, channel),
            ImageFilter = imageFilter,
            BlendMode = blendMode
        };
    }

    /// <summary>クリップのブレンドモードを SkiaSharp のブレンドモードに写す。</summary>
    private static SKBlendMode MapBlendMode(BlendMode mode) => mode switch
    {
        BlendMode.Add => SKBlendMode.Plus,
        BlendMode.Screen => SKBlendMode.Screen,
        BlendMode.Multiply => SKBlendMode.Multiply,
        BlendMode.Overlay => SKBlendMode.Overlay,
        _ => SKBlendMode.SrcOver
    };

    /// <summary>
    /// 画像を1パス描く。スライス・ワープが無ければ1枚で描き、あれば横ストリップに
    /// 分けて描く（ストリップごとに横ずれを付ける）。
    /// baseOffsetX は RGBシフトなどパス全体のずれ、stripOffset はストリップごとのずれ。
    /// </summary>
    private static void DrawImagePass(
        SKCanvas canvas, SKBitmap image, SKPaint paint, int width, int height,
        double scale, EvaluatedClipState state, double baseOffsetX, double sliceAmount, ClipEffect? warp)
    {
        var halfW = image.Width / 2f;
        var halfH = image.Height / 2f;

        canvas.Save();
        canvas.Translate((float)(width / 2.0 + state.PositionX), (float)(height / 2.0 + state.PositionY));
        canvas.RotateDegrees((float)state.RotationDegrees);
        canvas.Scale((float)scale, (float)scale);

        if (sliceAmount <= 0 && warp is null)
        {
            // baseOffsetX（RGBシフトのチャンネルずれ）も忘れずに付ける。
            // 付けないと、スライス・ワープ無しでは3チャンネルが同じ位置に描かれて
            // RGBシフトが目に見えなくなる。
            canvas.DrawBitmap(image,
                new SKRect(-halfW + (float)baseOffsetX, -halfH, halfW + (float)baseOffsetX, halfH), paint);
        }
        else
        {
            // ストリップ描画。ワープは滑らかな波にするため細かく、スライスだけなら粗くてよい。
            var stripCount = warp is not null ? 48 : 16;
            var stripH = image.Height / (float)stripCount;
            // 乱数は時刻だけから決める。同じ時刻なら同じずれになり、プレビューと書き出しが一致する。
            var rnd = new Random((int)Math.Round(state.TimeSeconds * 1000));
            for (var i = 0; i < stripCount; i++)
            {
                var y0 = i * stripH;
                var y1 = Math.Min(image.Height, y0 + stripH);
                if (y1 <= y0)
                    continue;
                var offsetX = baseOffsetX + StripOffsetX(state, rnd, (y0 + y1) / 2, sliceAmount, warp);
                var src = new SKRect(0, y0, image.Width, y1);
                var dst = new SKRect(-halfW + (float)offsetX, -halfH + y0, halfW + (float)offsetX, -halfH + y1);
                canvas.DrawBitmap(image, src, dst, paint);
            }
        }

        canvas.Restore();
    }

    /// <summary>
    /// ストリップ1つ分の横ずれ（px、素材上の座標）。ワープは高さ方向のサイン波、
    /// スライスは時刻とストリップで決まる乱数ずれ。両方あれば足し合わせる。
    /// </summary>
    private static double StripOffsetX(
        EvaluatedClipState state, Random rnd, double yCenter, double sliceAmount, ClipEffect? warp)
    {
        var offset = 0.0;
        if (warp is not null)
        {
            // 波：位相は時刻でゆっくり進む（0.5Hz）。振幅と波長はエフェクトの値。
            var wavelength = Math.Max(20, warp.Radius);
            var phase = state.TimeSeconds * Math.PI;
            offset += warp.Amount * Math.Sin(2 * Math.PI * yCenter / wavelength + phase);
        }
        if (sliceAmount > 0 && rnd.NextDouble() < 0.5)
            offset += (rnd.NextDouble() * 2 - 1) * sliceAmount;
        return offset;
    }

    /// <summary>
    /// クロマキー：キー色に近い画素を透明にする（画素単位の CPU 処理）。
    /// しきい値以下は透明、しきい値＋なめらかさ以上は不透明、その間は滑らかに変化する。
    /// 抜けた分だけキー色のスピル（色かぶり）も落とす。Premul ビットマップのため
    /// RGB も新しいアルファに合わせて掛け直す。
    /// </summary>
    private static void ApplyChromaKey(SKBitmap bitmap, ClipEffect effect)
    {
        if (!SKColor.TryParse(effect.KeyColorHex, out var keyColor))
            keyColor = new SKColor(0, 255, 0);

        var threshold = Math.Clamp(effect.Amount, 0, 1);
        var softness = Math.Max(effect.Radius, 0.01);
        var kr = keyColor.Red;
        var kg = keyColor.Green;
        var kb = keyColor.Blue;

        // 色距離の二乗で判定する（sqrt は遷移帯の画素だけで済ませる）。
        // 距離は 0〜1 に正規化する（最大値 sqrt(3*255^2) = 441.673）。
        const double maxDist = 441.673;
        var t0 = threshold * maxDist;
        var t1 = (threshold + softness) * maxDist;
        var t0Sq = t0 * t0;
        var t1Sq = t1 * t1;

        // キー色で最も強いチャンネル（緑なら G）。スピル除去で使う。
        var dominant = kr >= kg && kr >= kb ? 0 : kg >= kb ? 1 : 2;

        var rowBytes = bitmap.RowBytes;
        var height = bitmap.Height;
        var width = bitmap.Width;
        var bytes = new byte[rowBytes * height];
        Marshal.Copy(bitmap.GetPixels(), bytes, 0, bytes.Length);

        for (var y = 0; y < height; y++)
        {
            var row = y * rowBytes;
            for (var x = 0; x < width; x++)
            {
                var i = row + x * 4;
                var b = bytes[i];
                var g = bytes[i + 1];
                var r = bytes[i + 2];
                var a = bytes[i + 3];

                var dr = r - kr;
                var dg = g - kg;
                var db = b - kb;
                var distSq = dr * dr + dg * dg + db * db;
                if (distSq >= t1Sq)
                    continue; // キー色から遠い（不透明のまま）。

                double keep;
                if (distSq <= t0Sq)
                {
                    keep = 0; // キー色そのもの（透明）。
                }
                else
                {
                    var dist = Math.Sqrt(distSq);
                    keep = (dist - t0) / (t1 - t0);
                }

                var na = (int)Math.Round(a * keep);

                // スピル除去：抜けた分だけ、キー色の強いチャンネルを他のチャンネルの最大値まで落とす。
                var spill = 1 - keep;
                if (spill > 0)
                {
                    var ri = r;
                    var gi = g;
                    var bi = b;
                    var otherMax = dominant == 0 ? Math.Max(gi, bi)
                        : dominant == 1 ? Math.Max(ri, bi)
                        : Math.Max(ri, gi);
                    if (dominant == 0)
                        ri = (int)Math.Round(ri - (ri - otherMax) * spill);
                    else if (dominant == 1)
                        gi = (int)Math.Round(gi - (gi - otherMax) * spill);
                    else
                        bi = (int)Math.Round(bi - (bi - otherMax) * spill);
                    r = (byte)Math.Clamp(ri, 0, 255);
                    g = (byte)Math.Clamp(gi, 0, 255);
                    b = (byte)Math.Clamp(bi, 0, 255);
                }

                // Premul に合わせる：RGB も新しいアルファに掛け直す。
                var scale = a > 0 ? (double)na / a : 0;
                bytes[i] = (byte)Math.Clamp(b * scale, 0, 255);
                bytes[i + 1] = (byte)Math.Clamp(g * scale, 0, 255);
                bytes[i + 2] = (byte)Math.Clamp(r * scale, 0, 255);
                bytes[i + 3] = (byte)na;
            }
        }

        Marshal.Copy(bytes, 0, bitmap.GetPixels(), bytes.Length);
    }

    private static void DrawTextClip(
        SKCanvas canvas, Clip clip, EvaluatedClipState state, int width, int height)
    {
        if (string.IsNullOrEmpty(clip.Text))
            return;

        // テキストにはブレンドモードとブラーだけ適用する（グロー・RGBシフト・
        // スライス・ワープ・クロマキーは画像クリップ専用）。
        var blur = state.Effects
            .Where(e => e.Kind == EffectKind.Blur)
            .Select(e => e.Amount)
            .DefaultIfEmpty(0)
            .Max();

        using var typeface = SKTypeface.FromFamilyName(clip.FontFamily);
        using var blurFilter = CreateBlurFilter(blur);
        using var paint = new SKPaint
        {
            Typeface = typeface,
            TextSize = (float)(clip.FontSize * state.Scale),
            Color = ParseColor(clip.FontColorHex, state.Opacity),
            IsAntialias = true,
            BlendMode = MapBlendMode(state.BlendMode),
            ImageFilter = blurFilter
        };

        var textWidth = paint.MeasureText(clip.Text);
        canvas.Save();
        canvas.Translate((float)(width / 2.0 + state.PositionX), (float)(height / 2.0 + state.PositionY));
        canvas.RotateDegrees((float)state.RotationDegrees);
        canvas.DrawText(clip.Text, -textWidth / 2, paint.TextSize * 0.35f, paint);
        canvas.Restore();
    }

    /// <summary>明るさ・コントラストのカラーマトリクス。フェーズ1の色調補正の土台。channel を指定するとそのチャンネルだけ残す（RGBシフト用）。</summary>
    internal static SKColorFilter? ColorCorrectionFilter(double brightness, double contrast, Channel? channel = null)
    {
        if (channel is null && Math.Abs(brightness) < 1e-6 && Math.Abs(contrast - 1.0) < 1e-6)
            return null;

        var c = (float)contrast;
        var offset = (float)(128 * (1 - contrast) + brightness * 255);
        // 行ごとに [R係数, G係数, B係数, A係数, オフセット]。残さないチャンネルの行は 0 にする。
        var rRow = channel is null or Channel.Red ? (c, offset) : (0f, 0f);
        var gRow = channel is null or Channel.Green ? (c, offset) : (0f, 0f);
        var bRow = channel is null or Channel.Blue ? (c, offset) : (0f, 0f);
        float[] matrix =
        {
            rRow.Item1, 0, 0, 0, rRow.Item2,
            0, gRow.Item1, 0, 0, gRow.Item2,
            0, 0, bRow.Item1, 0, bRow.Item2,
            0, 0, 0, 1, 0
        };
        return SKColorFilter.CreateColorMatrix(matrix);
    }

    private static SKColor ParseColor(string hex, double opacity)
    {
        if (SKColor.TryParse(hex, out var color))
            return color.WithAlpha((byte)Math.Clamp(opacity * 255, 0, 255));
        return SKColors.White.WithAlpha((byte)Math.Clamp(opacity * 255, 0, 255));
    }
}

/// <summary>
/// プレビュー用レンダラー。指定した解像度で描き、直近の結果をキャッシュする。
/// 返すビットマップはこのクラスが所有する（呼び出し側で破棄しないこと）。
/// maxDisplaySize を渡すと、画面に表示される大きさ以上には描かない。
/// フル解像度でも画面が小さければ軽くなる。
/// </summary>
public sealed class PreviewRenderer : IDisposable
{
    private readonly IFrameRenderer _inner;
    private SKBitmap? _lastBitmap;
    private (double TimeSeconds, PreviewResolution Resolution, int Width, int Height)? _lastKey;

    public PreviewRenderer(IFrameRenderer inner)
    {
        _inner = inner;
    }

    public async Task<SKBitmap> RenderPreviewAsync(
        EditorProject project, double timeSeconds, PreviewResolution resolution,
        (int Width, int Height)? maxDisplaySize = null,
        CancellationToken ct = default)
    {
        var (width, height) = resolution.ApplyTo(project.Width, project.Height);
        if (maxDisplaySize.HasValue)
        {
            var scale = Math.Min(1.0, Math.Min(
                maxDisplaySize.Value.Width / (double)width,
                maxDisplaySize.Value.Height / (double)height));
            if (scale < 1.0)
            {
                width = Math.Max(2, (int)(width * scale));
                height = Math.Max(2, (int)(height * scale));
            }
        }

        var key = (timeSeconds, resolution, width, height);
        if (_lastKey == key && _lastBitmap is not null)
            return _lastBitmap;

        var bitmap = await _inner.RenderFrameAsync(project, timeSeconds, width, height, ct)
            .ConfigureAwait(false);

        _lastBitmap?.Dispose();
        _lastBitmap = bitmap;
        _lastKey = key;
        return bitmap;
    }

    /// <summary>
    /// キャッシュを捨てる。プロジェクトが変わったときに呼ぶ（同じ時刻でも描き直すため）。
    /// ビットマップ自体は残す。表示中のコントロールがまだ参照しているため、
    /// ここで破棄すると描画時に破棄済みオブジェクトを触る恐れがある。
    /// 次の描画で置き換えるときに破棄される。
    /// </summary>
    public void Invalidate()
    {
        _lastKey = null;
    }

    public void Dispose()
    {
        _lastBitmap?.Dispose();
        _lastBitmap = null;
    }
}
