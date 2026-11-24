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

/// <summary>タイムラインの評価結果を1フレームに描く。bandProvider があれば周波数帯駆動バインドも評価する（プレビュー・書き出し共通）。visualizationProvider があれば可視化クリップも描く。</summary>
public sealed class TimelineFrameRenderer : IFrameRenderer
{
    private readonly IVideoFrameSource _frameSource;
    private readonly IStreamingFrameSource? _streamingSource;
    private readonly IAudioBandEnergyProvider? _bandProvider;
    private readonly IAudioVisualizationProvider? _visualizationProvider;

    /// <summary>エコー（残像）用の過去フレーム履歴。クリップ Id ごとに、縮小コピーを数枚だけ持つ。</summary>
    private readonly Dictionary<string, EchoState> _echoStates = new();

    /// <summary>1クリップ分のエコー履歴。</summary>
    private sealed class EchoState
    {
        public Queue<SKBitmap> Frames { get; } = new();
        public double LastTimeSeconds { get; set; } = double.NaN;
    }

    public TimelineFrameRenderer(
        IVideoFrameSource frameSource, IStreamingFrameSource? streamingSource = null,
        IAudioBandEnergyProvider? bandProvider = null,
        IAudioVisualizationProvider? visualizationProvider = null)
    {
        _frameSource = frameSource;
        _streamingSource = streamingSource;
        _bandProvider = bandProvider;
        _visualizationProvider = visualizationProvider;
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
        if (_echoStates.Count > 0)
        {
            var activeClipIds = new HashSet<string>(states.Select(s => s.Clip.Id));
            foreach (var id in _echoStates.Keys.Where(k => !activeClipIds.Contains(k)).ToList())
            {
                foreach (var old in _echoStates[id].Frames)
                    old.Dispose();
                _echoStates.Remove(id);
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

    // DrawBitmapWithTransform はエコー履歴（インスタンス状態）を使うため、こちらもインスタンスメソッド。
    private void DrawImageClip(
        SKCanvas canvas, Clip clip, EvaluatedClipState state, int width, int height)
    {
        if (string.IsNullOrEmpty(clip.SourcePath) || !File.Exists(clip.SourcePath))
            return;

        using var image = SKBitmap.Decode(clip.SourcePath);
        if (image is null)
            return;

        DrawBitmapWithTransform(canvas, image, state, width, height);
    }

    /// <summary>RGBシフトで分けるチャンネル。ColorCorrectionFilter（internal）の仮引数の型のため internal。</summary>
    internal enum Channel
    {
        Red,
        Green,
        Blue
    }

    /// <summary>ドロップシャドウ用のカラーマトリクス：RGB を 0 にし、アルファはそのまま残す（黒いシルエットになる）。</summary>
    private static readonly float[] BlackSilhouetteMatrix =
    {
        0, 0, 0, 0, 0,
        0, 0, 0, 0, 0,
        0, 0, 0, 0, 0,
        0, 0, 0, 1, 0
    };

    /// <summary>
    /// クリップの画像（映像フレーム・静止画）を変形・合成して描く。
    /// エフェクトはここで適用する（プレビューと書き出しで同じ描画コードを使うため、両方に反映される）。
    /// 適用順：クロマキー・輝度キー（画素処理）→ モザイク・万華鏡・魚眼／色収差・ズームブラー・
    /// モーションブラー・色調系・VHSグリッチ・ポスタリゼーション・ノイズ・フィルムグレイン・
    /// トーンカーブ・ビネット（作業用ビットマップへの前処理）→ ドロップシャドウ → エコーの残像 →
    /// グロー（ぼかしたコピーを加算）→ 本体（ブラー、ブレンドモード、RGBシフト、スライス、ワープ）。
    /// </summary>
    private void DrawBitmapWithTransform(
        SKCanvas canvas, SKBitmap image, EvaluatedClipState state, int width, int height)
    {
        var effects = state.Effects;

        // クロマキー：描く前に画素単位で抜く（この画像は呼び出し側が都度作ったものなので直接書き換えてよい）。
        var chroma = effects.FirstOrDefault(e => e.Kind == EffectKind.ChromaKey);
        if (chroma is not null)
            ApplyChromaKey(image, chroma);

        // 輝度キー：明るい画素を抜く（クロマキーの明暗版。同じく描く前に画素単位で抜く）。
        var luminance = effects.FirstOrDefault(e => e.Kind == EffectKind.LuminanceKey);
        if (luminance is not null)
            ApplyLuminanceKey(image, luminance);

        // エコー用の残像（過去フレームの縮小コピーと不透明度）。履歴はここで更新する。
        var echo = effects.FirstOrDefault(e => e.Kind == EffectKind.Echo);
        var ghosts = GetEchoGhosts(state.Clip, image, echo, state);

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
        var dropShadow = effects.FirstOrDefault(e => e.Kind == EffectKind.DropShadow);

        // フレームに収まる倍率 × クリップの拡大率。
        var fit = Math.Min((double)width / image.Width, (double)height / image.Height);
        var scale = fit * state.Scale;

        // モザイク・万華鏡・魚眼／色収差・ズームブラー・モーションブラー・色調系・VHSグリッチ・
        // ポスタリゼーション・ノイズ・フィルムグレイン・トーンカーブ・ビネットは
        // 描く前に作業用ビットマップへ前処理する。前処理が無ければ元の画像をそのまま使う。
        var work = image;
        var intermediates = new List<SKBitmap>();
        try
        {
            var mosaic = effects.FirstOrDefault(e => e.Kind == EffectKind.Mosaic);
            if (mosaic is not null && mosaic.Amount >= 2)
                work = TrackIntermediate(work, ApplyMosaic(work, mosaic.Amount), intermediates);

            var kaleidoscope = effects.FirstOrDefault(e => e.Kind == EffectKind.Kaleidoscope);
            if (kaleidoscope is not null)
                work = TrackIntermediate(work, ApplyKaleidoscope(work, kaleidoscope), intermediates);

            // 魚眼・色収差：どちらも中心からの放射状の処理なので1回の画素ループに統合する。
            var fisheye = effects.FirstOrDefault(e => e.Kind == EffectKind.Fisheye);
            var chromaticAberration = effects.FirstOrDefault(e => e.Kind == EffectKind.ChromaticAberration);
            if (fisheye is not null || chromaticAberration is not null)
                work = TrackIntermediate(work, ApplyRadialWarp(work, fisheye, chromaticAberration), intermediates);

            // ズームブラー・モーションブラー：ずらしたコピーの平均（Plus 合成）でぼかす。
            var zoomBlur = effects.FirstOrDefault(e => e.Kind == EffectKind.ZoomBlur);
            if (zoomBlur is not null && zoomBlur.Amount > 0)
                work = TrackIntermediate(work, ApplyZoomBlur(work, zoomBlur), intermediates);

            var motionBlur = effects.FirstOrDefault(e => e.Kind == EffectKind.MotionBlur);
            if (motionBlur is not null && motionBlur.Amount > 0)
                work = TrackIntermediate(work, ApplyMotionBlur(work, motionBlur), intermediates);

            var colorMatrix = BuildColorMatrix(effects);
            if (colorMatrix is not null)
                work = TrackIntermediate(work, ApplyColorMatrix(work, colorMatrix), intermediates);

            // VHSグリッチ：色調補正の後にかけるとテープ劣化らしくなる。
            var vhs = effects.FirstOrDefault(e => e.Kind == EffectKind.VhsGlitch);
            if (vhs is not null && vhs.Amount > 0)
                work = TrackIntermediate(work, ApplyVhsGlitch(work, vhs, state.TimeSeconds), intermediates);

            ApplyCpuEffects(work, effects, state.TimeSeconds);

            // ドロップシャドウ：本体・残像より先に、黒くぼかしたシルエットを右下へずらして描く。
            if (dropShadow is not null && dropShadow.Amount > 0)
            {
                var shadowRadius = Math.Clamp(dropShadow.Radius, 1, 50);
                var shadowOffset = 4 + shadowRadius * 0.3; // 素材上のずれ（px、右下）。
                using var shadowFilter = SKImageFilter.CreateBlur((float)shadowRadius, (float)shadowRadius);
                using var shadowPaint = new SKPaint
                {
                    IsAntialias = true,
                    Color = new SKColor(255, 255, 255, (byte)Math.Clamp(state.Opacity * dropShadow.Amount * 255, 0, 255)),
                    ColorFilter = SKColorFilter.CreateColorMatrix(BlackSilhouetteMatrix),
                    ImageFilter = shadowFilter,
                    BlendMode = SKBlendMode.SrcOver
                };
                DrawImagePass(canvas, work, shadowPaint, width, height, scale, state, shadowOffset, 0, null, shadowOffset);
            }

            // エコーの残像：本体より先に、同じ変形で薄く描く（エフェクト無しの素のフレーム）。
            foreach (var (ghostBitmap, alpha) in ghosts)
            {
                using var ghostPaint = new SKPaint
                {
                    IsAntialias = true,
                    Color = new SKColor(255, 255, 255, (byte)Math.Clamp(alpha * 255, 0, 255)),
                    BlendMode = MapBlendMode(state.BlendMode)
                };
                DrawImagePass(canvas, ghostBitmap, ghostPaint, width, height, scale, state, 0, 0, null);
            }

            // グロー：ぼかしたコピーを加算で先に描く（明るい部分がにじむ）。
            if (glow is not null && glow.Amount > 0)
            {
                using var glowFilter = SKImageFilter.CreateBlur((float)Math.Max(1, glow.Radius), (float)Math.Max(1, glow.Radius));
                using var glowPaint = CreateEffectPaint(state, SKBlendMode.Plus, state.Opacity * glow.Amount, glowFilter, null);
                DrawImagePass(canvas, work, glowPaint, width, height, scale, state, 0, 0, null);
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
                    DrawImagePass(canvas, work, paint, width, height, scale, state, offsetX, slice, warp);
                }
            }
            else
            {
                using var blurFilter = CreateBlurFilter(blur);
                using var paint = CreateEffectPaint(state, MapBlendMode(state.BlendMode), state.Opacity, blurFilter, null);
                DrawImagePass(canvas, work, paint, width, height, scale, state, 0, slice, warp);
            }
        }
        finally
        {
            foreach (var intermediate in intermediates)
                intermediate.Dispose();
        }
    }

    /// <summary>前処理で作った中間ビットマップを破棄リストに載せて返す（元の画像と同じなら載せない）。</summary>
    private static SKBitmap TrackIntermediate(SKBitmap current, SKBitmap next, List<SKBitmap> intermediates)
    {
        if (!ReferenceEquals(next, current))
            intermediates.Add(next);
        return next;
    }

    /// <summary>
    /// エコーの残像を返す（古い順）。ついでに現在のフレームを履歴に入れる。
    /// 履歴は縮小コピー（幅最大 960px）で、残像の数だけ持つ。
    /// 再生ヘッドを前に戻したときやエコーを切ったときは履歴を捨てる。
    /// </summary>
    private IReadOnlyList<(SKBitmap Bitmap, double Alpha)> GetEchoGhosts(
        Clip clip, SKBitmap currentFrame, ClipEffect? echo, EvaluatedClipState state)
    {
        if (echo is null || echo.Amount <= 0)
        {
            if (_echoStates.TryGetValue(clip.Id, out var stale))
            {
                foreach (var old in stale.Frames)
                    old.Dispose();
                _echoStates.Remove(clip.Id);
            }
            return Array.Empty<(SKBitmap, double)>();
        }

        var count = (int)Math.Clamp(Math.Round(echo.Radius), 1, 8);
        if (!_echoStates.TryGetValue(clip.Id, out var echoState))
        {
            echoState = new EchoState();
            _echoStates[clip.Id] = echoState;
        }

        // 再生ヘッドを前に戻したり、大きく前に飛ばしたりしたら履歴は無効（古い残像を出さない）。
        // 通常の再生・書き出しではフレーム間隔は 1/30 秒程度なので、0.5 秒の猶予を持たせる。
        if (!double.IsNaN(echoState.LastTimeSeconds)
            && (state.TimeSeconds < echoState.LastTimeSeconds - 1e-6
                || state.TimeSeconds > echoState.LastTimeSeconds + 0.5))
        {
            foreach (var old in echoState.Frames)
                old.Dispose();
            echoState.Frames.Clear();
        }
        echoState.LastTimeSeconds = state.TimeSeconds;

        // 履歴は古い順に並ぶ。新しい残像ほど濃くする（1フレーム前が Amount、2フレーム前が Amount^2、…）。
        var frames = echoState.Frames.ToArray();
        var ghosts = new List<(SKBitmap, double)>();
        for (var i = 0; i < frames.Length; i++)
        {
            var age = frames.Length - i; // 最も新しい履歴は age=1。
            ghosts.Add((frames[i], state.Opacity * Math.Pow(echo.Amount, age)));
        }

        // 現在のフレームを履歴に入れる（次フレーム以降の残像になる）。
        echoState.Frames.Enqueue(DownscaleForEcho(currentFrame));
        while (echoState.Frames.Count > count)
            echoState.Frames.Dequeue().Dispose();

        return ghosts;
    }

    /// <summary>エコー履歴用にフレームを縮小コピーする（幅最大 960px）。呼び出し側が破棄する元画像とは別物にする。</summary>
    private static SKBitmap DownscaleForEcho(SKBitmap src)
    {
        const int maxWidth = 960;
        if (src.Width <= maxWidth)
            return src.Copy();

        var width = maxWidth;
        var height = Math.Max(1, (int)Math.Round(src.Height * (double)maxWidth / src.Width));
        var dst = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(dst);
        canvas.DrawBitmap(src, new SKRect(0, 0, width, height));
        return dst;
    }

    /// <summary>モザイク：小さく縮小してから最近傍で拡大し、ブロック状にする。</summary>
    private static SKBitmap ApplyMosaic(SKBitmap src, double blockSize)
    {
        var block = (int)Math.Clamp(Math.Round(blockSize), 2, 64);
        var smallWidth = Math.Max(1, src.Width / block);
        var smallHeight = Math.Max(1, src.Height / block);
        using var small = new SKBitmap(smallWidth, smallHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(small))
        using (var paint = new SKPaint { FilterQuality = SKFilterQuality.Medium })
            canvas.DrawBitmap(src, new SKRect(0, 0, smallWidth, smallHeight), paint);

        var dst = new SKBitmap(src.Width, src.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var dstCanvas = new SKCanvas(dst);
        using var upscalePaint = new SKPaint { FilterQuality = SKFilterQuality.None };
        dstCanvas.DrawBitmap(small, new SKRect(0, 0, src.Width, src.Height), upscalePaint);
        return dst;
    }

    /// <summary>
    /// 万華鏡：画像を扇形に切り、隣り合う扇形を鏡像にして円状に並べる。
    /// 扇形の枚数だけ画像全体を描き直すため、分割数が多いほど重い（最大12枚）。
    /// </summary>
    private static SKBitmap ApplyKaleidoscope(SKBitmap src, ClipEffect effect)
    {
        var segments = (int)Math.Clamp(Math.Round(effect.Radius), 3, 12);
        var zoom = (float)Math.Clamp(effect.Amount, 0.25, 2);
        var dst = new SKBitmap(src.Width, src.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(dst);
        canvas.Clear(SKColors.Black);

        var cx = src.Width / 2f;
        var cy = src.Height / 2f;
        var radius = MathF.Sqrt(cx * cx + cy * cy) + 1;
        var wedgeAngle = 360f / segments;
        var half = wedgeAngle / 2 * (MathF.PI / 180f);
        using var wedge = new SKPath();
        wedge.MoveTo(0, 0);
        wedge.LineTo(radius * MathF.Cos(-half), radius * MathF.Sin(-half));
        wedge.LineTo(radius * MathF.Cos(half), radius * MathF.Sin(half));
        wedge.Close();

        var halfWidth = cx * zoom;
        var halfHeight = cy * zoom;
        for (var i = 0; i < segments; i++)
        {
            canvas.Save();
            canvas.Translate(cx, cy);
            canvas.RotateDegrees(i * wedgeAngle);
            if (i % 2 == 1)
                canvas.Scale(-1, 1); // 隣り合う扇形は鏡像にする。
            canvas.ClipPath(wedge);
            canvas.DrawBitmap(src, new SKRect(-halfWidth, -halfHeight, halfWidth, halfHeight));
            canvas.Restore();
        }
        return dst;
    }

    /// <summary>最近傍で1画素を読む（範囲外は端で止める）。Premul BGRA の (B, G, R, A)。</summary>
    private static (byte B, byte G, byte R, byte A) SampleNearest(
        byte[] bytes, int rowBytes, int width, int height, double x, double y)
    {
        var ix = (int)Math.Clamp(Math.Round(x), 0, width - 1);
        var iy = (int)Math.Clamp(Math.Round(y), 0, height - 1);
        var i = iy * rowBytes + ix * 4;
        return (bytes[i], bytes[i + 1], bytes[i + 2], bytes[i + 3]);
    }

    /// <summary>
    /// 魚眼・色収差を1回の画素ループで適用する（どちらも中心からの放射状の処理のため統合）。
    /// 魚眼：中心からの距離でサンプル位置を内側へ寄せる（膨らんだ見た目）。範囲外は透明になる。
    /// 色収差：中心からの距離に比例して R を中心側・B を外側からサンプルする（放射状の色ずれ。
    /// RGBシフトの一律ずれとは別物）。どちらも無効なら元のビットマップをそのまま返す
    /// （TrackIntermediate が破棄リストに載せないため、呼び出し側の所有権は変わらない）。
    /// キーで抜いた縁では、色収差の R/B が透明な隣から読まれて少し暗くなる場合がある。
    /// </summary>
    private static SKBitmap ApplyRadialWarp(SKBitmap src, ClipEffect? fisheye, ClipEffect? chromaticAberration)
    {
        var distortion = fisheye is not null ? Math.Clamp(fisheye.Amount, 0, 1) : 0;
        var zoom = fisheye is not null ? Math.Clamp(fisheye.Radius, 1, 2) : 1;
        var caAmount = chromaticAberration is not null ? Math.Clamp(chromaticAberration.Amount, 0, 20) : 0;
        var caInner = chromaticAberration is not null ? Math.Clamp(chromaticAberration.Radius, 0, 0.5) : 0;
        if (distortion <= 0 && caAmount <= 0)
            return src;

        var width = src.Width;
        var height = src.Height;
        var rowBytes = src.RowBytes;
        var srcBytes = new byte[rowBytes * height];
        Marshal.Copy(src.GetPixels(), srcBytes, 0, srcBytes.Length);
        var dstBytes = new byte[rowBytes * height];

        var cx = width / 2.0;
        var cy = height / 2.0;
        var halfDiag = Math.Sqrt(cx * cx + cy * cy); // 正規化距離の分母（隅が 1）。

        for (var y = 0; y < height; y++)
        {
            var dy = y - cy;
            var row = y * rowBytes;
            for (var x = 0; x < width; x++)
            {
                var dx = x - cx;
                var i = row + x * 4;
                var dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist < 1e-9)
                {
                    // 中心：そのままコピー。
                    dstBytes[i] = srcBytes[i];
                    dstBytes[i + 1] = srcBytes[i + 1];
                    dstBytes[i + 2] = srcBytes[i + 2];
                    dstBytes[i + 3] = srcBytes[i + 3];
                    continue;
                }

                // 魚眼：サンプル位置を中心へ寄せる（1 + 歪み×r^2 で割る）。ズームはその後に掛ける。
                var r2 = (dx / cx) * (dx / cx) + (dy / cy) * (dy / cy);
                var factor = distortion > 0 ? zoom / (1 + distortion * r2) : 1;
                var sx = cx + dx * factor;
                var sy = cy + dy * factor;
                if (distortion > 0 && (sx < 0 || sx >= width || sy < 0 || sy >= height))
                    continue; // 魚眼で範囲外：透明（dst は 0 で初期化済み）。

                // 色収差：中心からの距離（半対角線に対する割合）に比例したずれ。
                var d = dist / halfDiag;
                var shift = caAmount > 0 ? caAmount * Math.Max(0, d - caInner) : 0;
                var ux = dx / dist;
                var uy = dy / dist;

                var g = SampleNearest(srcBytes, rowBytes, width, height, sx, sy);
                byte r = g.R;
                byte b = g.B;
                if (shift > 0)
                {
                    var rs = SampleNearest(srcBytes, rowBytes, width, height, sx - shift * ux, sy - shift * uy);
                    var bs = SampleNearest(srcBytes, rowBytes, width, height, sx + shift * ux, sy + shift * uy);
                    r = rs.R;
                    b = bs.B;
                }
                dstBytes[i] = b;
                dstBytes[i + 1] = g.G;
                dstBytes[i + 2] = r;
                dstBytes[i + 3] = g.A;
            }
        }

        var dst = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        Marshal.Copy(dstBytes, 0, dst.GetPixels(), dstBytes.Length);
        return dst;
    }

    /// <summary>
    /// ズームブラー：中心から少しずつ拡大したコピーを Plus 合成で平均化する
    /// （不透明度 1/N ずつ加算すると平均になり、透明度も正しく保たれる）。
    /// 強さが小さいほど拡大率の広がりも小さいため、0 に近いと元の画像に近づく。
    /// サンプル数（Radius）が多いほど滑らかだが、コピーの枚数だけ描画が重くなる。
    /// </summary>
    private static SKBitmap ApplyZoomBlur(SKBitmap src, ClipEffect effect)
    {
        var strength = Math.Clamp(effect.Amount, 0, 1);
        var samples = (int)Math.Clamp(Math.Round(effect.Radius), 4, 16);
        var dst = new SKBitmap(src.Width, src.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(dst);
        var cx = src.Width / 2f;
        var cy = src.Height / 2f;
        var rect = new SKRect(0, 0, src.Width, src.Height);
        var maxScale = 1f + (float)strength * 0.12f; // 端で最大 12% 広がる。
        var alpha = (byte)Math.Round(255.0 / samples);
        for (var i = 0; i < samples; i++)
        {
            var t = samples == 1 ? 0 : (float)i / (samples - 1);
            var scale = 1 + (maxScale - 1) * t;
            using var paint = new SKPaint
            {
                Color = new SKColor(255, 255, 255, alpha),
                BlendMode = SKBlendMode.Plus
            };
            canvas.Save();
            canvas.Translate(cx, cy);
            canvas.Scale(scale, scale);
            canvas.Translate(-cx, -cy);
            canvas.DrawBitmap(src, rect, paint);
            canvas.Restore();
        }
        return dst;
    }

    /// <summary>
    /// モーションブラー：指定した方向へ均等にずらしたコピー 8 枚を Plus 合成で平均化する
    /// （ボックスブラー近似）。長さが 0 なら呼ばれない。端ではサンプルが画面外へはみ出す分だけ
    /// 少し暗く・透明になる（ボックスブラーの端の性質）。
    /// </summary>
    private static SKBitmap ApplyMotionBlur(SKBitmap src, ClipEffect effect)
    {
        var length = Math.Clamp(effect.Amount, 0, 64);
        const int samples = 8;
        var angle = Math.Clamp(effect.Radius, 0, 360) * Math.PI / 180;
        var dx = Math.Cos(angle);
        var dy = Math.Sin(angle);
        var dst = new SKBitmap(src.Width, src.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(dst);
        var alpha = (byte)Math.Round(255.0 / samples);
        for (var i = 0; i < samples; i++)
        {
            var t = (double)i / (samples - 1) - 0.5; // -0.5〜+0.5
            var ox = (float)(dx * length * t);
            var oy = (float)(dy * length * t);
            using var paint = new SKPaint
            {
                Color = new SKColor(255, 255, 255, alpha),
                BlendMode = SKBlendMode.Plus
            };
            canvas.DrawBitmap(src, new SKRect(ox, oy, src.Width + ox, src.Height + oy), paint);
        }
        return dst;
    }

    /// <summary>
    /// VHSグリッチ：トラッキングずれ（横帯ごとのランダムな横ずれ）・走査線・色のにじみを
    /// 1回の画素ループで適用する。帯のずれは時刻で変わるため、再生するたびに揺れる。
    /// </summary>
    private static SKBitmap ApplyVhsGlitch(SKBitmap src, ClipEffect effect, double timeSeconds)
    {
        var strength = Math.Clamp(effect.Amount, 0, 1);
        var spacing = (int)Math.Clamp(Math.Round(effect.Radius), 2, 8);
        var width = src.Width;
        var height = src.Height;
        var rowBytes = src.RowBytes;
        var srcBytes = new byte[rowBytes * height];
        Marshal.Copy(src.GetPixels(), srcBytes, 0, srcBytes.Length);
        var dstBytes = new byte[rowBytes * height];

        var frameIndex = (int)Math.Round(timeSeconds * 1000);
        const int bandHeight = 24;
        var bandCount = height / bandHeight + 1;
        var bandOffsets = new int[bandCount];
        var maxOffset = strength * 32;
        for (var band = 0; band < bandCount; band++)
        {
            // 帯の半分くらいがずれ、残りはそのまま（トラッキングが時々乱れる感じ）。
            bandOffsets[band] = NoiseValue(band, 0, frameIndex) < 0.5
                ? 0
                : (int)Math.Round((NoiseValue(band, 1, frameIndex) - 0.5) * 2 * maxOffset);
        }
        var bleed = 1 + (int)Math.Round(strength * 2); // R/B の横ずれ（px）。
        var scanlineDarken = 1 - 0.35 * strength;

        for (var y = 0; y < height; y++)
        {
            var offset = bandOffsets[y / bandHeight];
            var isScanline = y % spacing == spacing - 1;
            var row = y * rowBytes;
            for (var x = 0; x < width; x++)
            {
                var sx = Math.Clamp(x - offset, 0, width - 1);
                var i = row + x * 4;
                var si = row + sx * 4;
                var g = srcBytes[si + 1];
                var a = srcBytes[si + 3];
                var r = srcBytes[row + Math.Clamp(sx + bleed, 0, width - 1) * 4 + 2];
                var b = srcBytes[row + Math.Clamp(sx - bleed, 0, width - 1) * 4];
                if (isScanline)
                {
                    r = (byte)(r * scanlineDarken);
                    g = (byte)(g * scanlineDarken);
                    b = (byte)(b * scanlineDarken);
                }
                // Premul を壊さないよう、RGB はアルファ以下に丸める。
                dstBytes[i] = (byte)Math.Min(b, a);
                dstBytes[i + 1] = (byte)Math.Min(g, a);
                dstBytes[i + 2] = (byte)Math.Min(r, a);
                dstBytes[i + 3] = a;
            }
        }

        var dst = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        Marshal.Copy(dstBytes, 0, dst.GetPixels(), dstBytes.Length);
        return dst;
    }

    /// <summary>
    /// 色調系エフェクト（色反転・色相・彩度・モノクロ・セピア）を1つのカラーマトリクスに合成する。
    /// 該当するエフェクトが無ければ null。マトリクスはネイティブ側で適用されるため、画素ループより速い。
    /// </summary>
    private static float[]? BuildColorMatrix(IReadOnlyList<ClipEffect> effects)
    {
        float[]? matrix = null;
        foreach (var effect in effects)
        {
            float[]? next = effect.Kind switch
            {
                EffectKind.Invert when effect.Amount > 0 => InvertMatrix(effect.Amount),
                EffectKind.HueShift when Math.Abs(effect.Amount) > 1e-6 => HueRotateMatrix(effect.Amount),
                EffectKind.Saturation when Math.Abs(effect.Amount - 1) > 1e-6 => SaturationMatrix(effect.Amount),
                EffectKind.Monochrome when effect.Amount > 0 => MonochromeMatrix(effect.Amount),
                EffectKind.Sepia when effect.Amount > 0 => SepiaMatrix(effect.Amount),
                _ => null
            };
            if (next is null)
                continue;
            // 後に来るエフェクトを後に適用する（matrix は先に適用される側）。
            matrix = matrix is null ? next : MultiplyColorMatrix(next, matrix);
        }
        return matrix;
    }

    /// <summary>4x5 カラーマトリクス同士の合成（a∘b：b を先に、a を後に適用する）。</summary>
    private static float[] MultiplyColorMatrix(float[] a, float[] b)
    {
        var result = new float[20];
        for (var row = 0; row < 4; row++)
        {
            for (var col = 0; col < 4; col++)
            {
                var sum = 0f;
                for (var k = 0; k < 4; k++)
                    sum += a[row * 5 + k] * b[k * 5 + col];
                result[row * 5 + col] = sum;
            }
            var offset = a[row * 5 + 4];
            for (var k = 0; k < 4; k++)
                offset += a[row * 5 + k] * b[k * 5 + 4];
            result[row * 5 + 4] = offset;
        }
        return result;
    }

    /// <summary>色反転のマトリクス（量 t：0 で元の色、1 で完全反転）。</summary>
    private static float[] InvertMatrix(double t)
    {
        var f = (float)(1 - 2 * t);
        var offset = (float)(255 * t);
        return new float[]
        {
            f, 0, 0, 0, offset,
            0, f, 0, 0, offset,
            0, 0, f, 0, offset,
            0, 0, 0, 1, 0
        };
    }

    /// <summary>色相回転のマトリクス（CSS/SVG の hue-rotate と同じ式。角度は度）。</summary>
    private static float[] HueRotateMatrix(double degrees)
    {
        var radians = degrees * Math.PI / 180;
        var c = (float)Math.Cos(radians);
        var s = (float)Math.Sin(radians);
        return new float[]
        {
            0.213f + 0.787f * c - 0.213f * s, 0.715f - 0.715f * c - 0.715f * s, 0.072f - 0.072f * c + 0.928f * s, 0, 0,
            0.213f - 0.213f * c + 0.143f * s, 0.715f + 0.285f * c + 0.140f * s, 0.072f - 0.072f * c - 0.283f * s, 0, 0,
            0.213f - 0.213f * c - 0.787f * s, 0.715f - 0.715f * c + 0.715f * s, 0.072f + 0.928f * c + 0.072f * s, 0, 0,
            0, 0, 0, 1, 0
        };
    }

    /// <summary>彩度のマトリクス（倍率 s：0 でモノクロ、1 で元のまま）。</summary>
    private static float[] SaturationMatrix(double s)
    {
        var f = (float)s;
        return new float[]
        {
            0.213f + 0.787f * f, 0.715f - 0.715f * f, 0.072f - 0.072f * f, 0, 0,
            0.213f - 0.213f * f, 0.715f + 0.285f * f, 0.072f - 0.072f * f, 0, 0,
            0.213f - 0.213f * f, 0.715f - 0.715f * f, 0.072f + 0.928f * f, 0, 0,
            0, 0, 0, 1, 0
        };
    }

    /// <summary>モノクロ化のマトリクス（量 t：0 で元の色、1 で完全モノクロ。輝度は 0.213R + 0.715G + 0.072B）。</summary>
    private static float[] MonochromeMatrix(double t)
    {
        var f = (float)(1 - t);
        var m = (float)t;
        return new float[]
        {
            f + 0.213f * m, 0.715f * m, 0.072f * m, 0, 0,
            0.213f * m, f + 0.715f * m, 0.072f * m, 0, 0,
            0.213f * m, 0.715f * m, f + 0.072f * m, 0, 0,
            0, 0, 0, 1, 0
        };
    }

    /// <summary>セピア化のマトリクス（量 t：0 で元の色、1 で完全セピア）。</summary>
    private static float[] SepiaMatrix(double t)
    {
        var f = (float)(1 - t);
        var m = (float)t;
        return new float[]
        {
            f + 0.393f * m, 0.769f * m, 0.189f * m, 0, 0,
            0.349f * m, f + 0.686f * m, 0.168f * m, 0, 0,
            0.272f * m, 0.534f * m, f + 0.131f * m, 0, 0,
            0, 0, 0, 1, 0
        };
    }

    /// <summary>カラーマトリクスを画像全体に適用した新しいビットマップを作る。</summary>
    private static SKBitmap ApplyColorMatrix(SKBitmap src, float[] matrix)
    {
        var dst = new SKBitmap(src.Width, src.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(dst);
        using var filter = SKColorFilter.CreateColorMatrix(matrix);
        using var paint = new SKPaint { ColorFilter = filter };
        canvas.DrawBitmap(src, new SKRect(0, 0, src.Width, src.Height), paint);
        return dst;
    }

    /// <summary>
    /// ポスタリゼーション・ノイズ・フィルムグレイン・トーンカーブ・ビネットを1回の画素ループで適用する（Premul BGRA）。
    /// ポスタリゼーション：チャンネルごとに階調数へ量子化する。
    /// ノイズ：粒ごとに決まるハッシュ値で輝度を揺らす（時刻で変わる）。
    /// フィルムグレイン：ノイズより弱い振幅の粒状感（ノイズとは別の種でずらすため、両方付けても粒が重ならない）。
    /// トーンカーブ：シャドウ／ハイライトの簡易2点調整（LUT。中間調は両者の中間から自動補間）。
    /// ビネット：中心からの距離で周囲を暗くする。
    /// RGB はアルファを超えないよう丸める（Premul を壊さないため）。
    /// </summary>
    private static void ApplyCpuEffects(SKBitmap bitmap, IReadOnlyList<ClipEffect> effects, double timeSeconds)
    {
        var posterize = effects.FirstOrDefault(e => e.Kind == EffectKind.Posterize);
        var noise = effects.FirstOrDefault(e => e.Kind == EffectKind.Noise);
        var filmGrain = effects.FirstOrDefault(e => e.Kind == EffectKind.FilmGrain);
        var toneCurve = effects.FirstOrDefault(e => e.Kind == EffectKind.ToneCurve);
        var vignette = effects.FirstOrDefault(e => e.Kind == EffectKind.Vignette);
        if (posterize is null && noise is null && filmGrain is null && toneCurve is null && vignette is null)
            return;

        var levels = posterize is not null
            ? (int)Math.Clamp(Math.Round(posterize.Amount), 2, 16) : 0;
        var noiseAmount = noise?.Amount ?? 0;
        var grain = noise is not null ? (int)Math.Clamp(Math.Round(noise.Radius), 1, 8) : 1;
        var filmGrainAmount = filmGrain?.Amount ?? 0;
        var filmGrainSize = filmGrain is not null ? (int)Math.Clamp(Math.Round(filmGrain.Radius), 1, 8) : 1;
        var toneTable = toneCurve is not null
            ? BuildToneCurveTable(toneCurve.Amount, toneCurve.Radius) : null;
        var vignetteAmount = vignette?.Amount ?? 0;
        var vignetteRadius = vignette is not null ? Math.Clamp(vignette.Radius, 0.5, 2) : 1;

        // ノイズはフレーム（時刻のミリ秒）ごとに変わる。同じ時刻なら同じ粒になる。
        var frameIndex = (int)Math.Round(timeSeconds * 1000);

        var rowBytes = bitmap.RowBytes;
        var height = bitmap.Height;
        var width = bitmap.Width;
        var bytes = new byte[rowBytes * height];
        Marshal.Copy(bitmap.GetPixels(), bytes, 0, bytes.Length);

        var step = levels > 0 ? 255.0 / (levels - 1) : 0;
        var halfWidth = Math.Max(1, width / 2.0);
        var halfHeight = Math.Max(1, height / 2.0);

        for (var y = 0; y < height; y++)
        {
            var row = y * rowBytes;
            var ny = (y - height / 2.0) / halfHeight;
            for (var x = 0; x < width; x++)
            {
                var i = row + x * 4;
                var b = bytes[i];
                var g = bytes[i + 1];
                var r = bytes[i + 2];
                var a = bytes[i + 3];

                if (levels > 0)
                {
                    r = (byte)Math.Clamp(Math.Round(r / step) * step, 0, 255);
                    g = (byte)Math.Clamp(Math.Round(g / step) * step, 0, 255);
                    b = (byte)Math.Clamp(Math.Round(b / step) * step, 0, 255);
                }

                if (toneTable is not null)
                {
                    r = toneTable[r];
                    g = toneTable[g];
                    b = toneTable[b];
                }

                if (vignetteAmount > 0)
                {
                    var nx = (x - width / 2.0) / halfWidth;
                    var d = Math.Sqrt(nx * nx + ny * ny);
                    var edge = Math.Clamp((d - vignetteRadius * 0.55) / (vignetteRadius * 0.45 + 0.05), 0, 1);
                    var factor = 1 - vignetteAmount * edge * edge;
                    r = (byte)(r * factor);
                    g = (byte)(g * factor);
                    b = (byte)(b * factor);
                }

                if (noiseAmount > 0 || filmGrainAmount > 0)
                {
                    // ノイズとフィルムグレインは両方あれば足し合わせる（粒の種が違うため相関しない）。
                    var delta = 0.0;
                    if (noiseAmount > 0)
                        delta += (NoiseValue(x / grain, y / grain, frameIndex) - 0.5) * 2 * noiseAmount * 96;
                    if (filmGrainAmount > 0)
                        delta += (NoiseValue(x / filmGrainSize, y / filmGrainSize, frameIndex + 7919) - 0.5) * 2 * filmGrainAmount * 48;
                    r = (byte)Math.Clamp(r + delta, 0, a);
                    g = (byte)Math.Clamp(g + delta, 0, a);
                    b = (byte)Math.Clamp(b + delta, 0, a);
                }
                else
                {
                    // ノイズ類が無くても、ポスタリゼーション・トーンカーブで RGB がアルファを超えた分を丸める。
                    r = (byte)Math.Min(r, a);
                    g = (byte)Math.Min(g, a);
                    b = (byte)Math.Min(b, a);
                }

                bytes[i] = b;
                bytes[i + 1] = g;
                bytes[i + 2] = r;
            }
        }

        Marshal.Copy(bytes, 0, bitmap.GetPixels(), bytes.Length);
    }

    /// <summary>粒の位置とフレームから決まる 0〜1 の疑似乱数（ハッシュ関数。時刻が変わると粒が変わる）。</summary>
    private static double NoiseValue(int blockX, int blockY, int frame)
    {
        uint h = (uint)(blockX * 374761393 + blockY * 668265263 + frame * 974634211);
        h = (h ^ (h >> 13)) * 1274126177u;
        h ^= h >> 16;
        return (h & 0xFFFF) / 65535.0;
    }

    /// <summary>
    /// 簡易トーンカーブの LUT（256段）。シャドウ s・ハイライト h は -1〜1。
    /// y = x + s(1-x)^2 + h x^2：暗部・明部を持ち上げ／締め、中間調は両者の中間から自動補間される。
    /// 両方 0 なら null（変化なし）。
    /// </summary>
    private static byte[]? BuildToneCurveTable(double shadows, double highlights)
    {
        if (Math.Abs(shadows) < 1e-6 && Math.Abs(highlights) < 1e-6)
            return null;
        var s = Math.Clamp(shadows, -1, 1);
        var h = Math.Clamp(highlights, -1, 1);
        var table = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            var x = i / 255.0;
            var y = x + s * (1 - x) * (1 - x) + h * x * x;
            table[i] = (byte)Math.Clamp(Math.Round(y * 255), 0, 255);
        }
        return table;
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

    /// <summary>クリップのブレンドモードを SkiaSharp のブレンドモードに写す。VisualizationRenderer からも使うため internal。</summary>
    internal static SKBlendMode MapBlendMode(BlendMode mode) => mode switch
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
    /// baseOffsetX / baseOffsetY は RGBシフトなどパス全体のずれ（ドロップシャドウの影ずらしにも使う）。
    /// stripOffset はストリップごとのずれ。
    /// </summary>
    private static void DrawImagePass(
        SKCanvas canvas, SKBitmap image, SKPaint paint, int width, int height,
        double scale, EvaluatedClipState state, double baseOffsetX, double sliceAmount, ClipEffect? warp,
        double baseOffsetY = 0)
    {
        var halfW = image.Width / 2f;
        var halfH = image.Height / 2f;

        canvas.Save();
        canvas.Translate((float)(width / 2.0 + state.PositionX), (float)(height / 2.0 + state.PositionY));
        canvas.RotateDegrees((float)state.RotationDegrees);
        canvas.Scale((float)scale, (float)scale);

        if (sliceAmount <= 0 && warp is null)
        {
            // baseOffsetX / baseOffsetY（RGBシフトのチャンネルずれ・影のずれ）も忘れずに付ける。
            // 付けないと、スライス・ワープ無しでは3チャンネルが同じ位置に描かれて
            // RGBシフトが目に見えなくなる。
            canvas.DrawBitmap(image,
                new SKRect(-halfW + (float)baseOffsetX, -halfH + (float)baseOffsetY, halfW + (float)baseOffsetX, halfH + (float)baseOffsetY), paint);
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
                var dst = new SKRect(-halfW + (float)offsetX, -halfH + y0 + (float)baseOffsetY, halfW + (float)offsetX, -halfH + y1 + (float)baseOffsetY);
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
                // （int で持つ。byte のままだと (int) への代入と Math.Clamp の解決ができない。）
                var spill = 1 - keep;
                if (spill > 0)
                {
                    var ri = (int)r;
                    var gi = (int)g;
                    var bi = (int)b;
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

    /// <summary>
    /// 輝度キー：明るい画素を透明にする（クロマキーの明暗版。白背景の除去など）。
    /// しきい値より明るい画素を抜き始め、しきい値＋なめらかさより明るい画素を完全に抜く。
    /// その間は滑らかに変化する。Premul ビットマップのため RGB も新しいアルファに合わせて掛け直す。
    /// </summary>
    private static void ApplyLuminanceKey(SKBitmap bitmap, ClipEffect effect)
    {
        var threshold = Math.Clamp(effect.Amount, 0, 1);
        var softness = Math.Max(effect.Radius, 0.01);
        var t0 = threshold * 255;
        var t1 = Math.Min((threshold + softness) * 255, 255);

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

                // 輝度（Premul の RGB から。0.213R + 0.715G + 0.072B）。
                var luminance = 0.213 * r + 0.715 * g + 0.072 * b;
                if (luminance <= t0)
                    continue; // 暗いまま（不透明）。

                double keep;
                if (luminance >= t1)
                {
                    keep = 0; // 明るい（透明）。
                }
                else
                {
                    keep = (t1 - luminance) / (t1 - t0);
                }

                var na = (int)Math.Round(a * keep);

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

    // VisualizationRenderer からも使うため internal。
    internal static SKColor ParseColor(string hex, double opacity)
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
