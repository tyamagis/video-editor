// クリップ画像の変形・合成（エフェクト適用パイプライン）。
// FrameRenderer.cs から切り出した、1クリップ分の描画を担うクラス。
// エコー履歴は EchoHistory が持ち、コンストラクタで受け取る。

using Editor.Core;
using SkiaSharp;

namespace Editor.Render;

/// <summary>
/// クリップの画像（映像フレーム・静止画・テキストのオフスクリーン）を変形・合成して描く。
/// エフェクトはここで適用する（プレビューと書き出しで同じ描画コードを使うため、両方に反映される）。
/// </summary>
internal sealed class ClipCompositor
{
    private readonly EchoHistory _echoHistory;

    public ClipCompositor(EchoHistory echoHistory)
    {
        _echoHistory = echoHistory;
    }

    /// <summary>RGBシフトで分けるチャンネル。ColorCorrectionFilter の仮引数の型。ClipCompositor 内でのみ使う。</summary>
    private enum Channel
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
    /// 適用順：クロマキー・輝度キー（画素処理）→ モザイク・万華鏡・魚眼／色収差・
    /// ミラー・ウェーブ・トゥワール・ディスプレイスメント・バルジ・ウォーターリップル・
    /// ピクセルソート・ズームブラー・モーションブラー・色調系（色温度を含む）・
    /// レベル補正・スプリットトーン・グラデーションマップ・LUT（色系チェーンの最後）・ソラリゼーション・
    /// エッジ検出・ハーフトーン・油絵風・シャープ・エンボス・VHSグリッチ・ブルーム・ライトリーク・
    /// スキャンライン・オールドフィルム・ティルトシフト・ポスタリゼーション・
    /// ノイズ・フィルムグレイン・トーンカーブ・ビネット（作業用ビットマップへの前処理）・
    /// レターボックス → ドロップシャドウ → エコーの残像 →
    /// グロー（ぼかしたコピーを加算）→ 本体（ブラー、ブレンドモード、RGBシフト、スライス、ワープ）。
    /// </summary>
    internal void DrawBitmapWithTransform(
        SKCanvas canvas, SKBitmap image, EvaluatedClipState state, int width, int height)
    {
        var effects = state.Effects;

        // クロマキー：描く前に画素単位で抜く（この画像は呼び出し側が都度作ったものなので直接書き換えてよい）。
        var chroma = effects.FirstOrDefault(e => e.Kind == EffectKind.ChromaKey);
        if (chroma is not null)
            KeyingEffects.ApplyChromaKey(image, chroma);

        // 輝度キー：明るい画素を抜く（クロマキーの明暗版。同じく描く前に画素単位で抜く）。
        var luminance = effects.FirstOrDefault(e => e.Kind == EffectKind.LuminanceKey);
        if (luminance is not null)
            KeyingEffects.ApplyLuminanceKey(image, luminance);

        // エコー用の残像（過去フレームの縮小コピーと不透明度）。履歴はここで更新する。
        var echo = effects.FirstOrDefault(e => e.Kind == EffectKind.Echo);
        var ghosts = _echoHistory.GetGhosts(state.Clip, image, echo, state);

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
                work = TrackIntermediate(work, BitmapGeometryEffects.ApplyMosaic(work, mosaic.Amount), intermediates);

            var kaleidoscope = effects.FirstOrDefault(e => e.Kind == EffectKind.Kaleidoscope);
            if (kaleidoscope is not null)
                // テキストは透明背景のオフスクリーンから来るため、万華鏡の背景も透明にする
                // （黒で塗るとテキストの周りが黒い四角で塗りつぶされて下のトラックが見えなくなる）。
                // 映像・静止画は従来どおり黒のまま（ズーム<1 で端に残る黒は従来の見た目を保つ）。
                work = TrackIntermediate(work, BitmapGeometryEffects.ApplyKaleidoscope(work, kaleidoscope, state.Clip.Kind == ClipKind.Text), intermediates);

            // 魚眼・色収差：どちらも中心からの放射状の処理なので1回の画素ループに統合する。
            var fisheye = effects.FirstOrDefault(e => e.Kind == EffectKind.Fisheye);
            var chromaticAberration = effects.FirstOrDefault(e => e.Kind == EffectKind.ChromaticAberration);
            if (fisheye is not null || chromaticAberration is not null)
                work = TrackIntermediate(work, BitmapGeometryEffects.ApplyRadialWarp(work, fisheye, chromaticAberration), intermediates);

            // 新エフェクト第3弾の幾何変換系：ミラー・ウェーブ・トゥワール・ディスプレイスメント・
            // バルジ・ウォーターリップル・ピクセルソート。いずれも画素の再配置（逆写像）で、
            // 範囲外は透明になる。色調系より先にかける。
            var mirror = effects.FirstOrDefault(e => e.Kind == EffectKind.Mirror);
            if (mirror is not null)
                work = TrackIntermediate(work, NewEffectsDistort.ApplyMirror(work, mirror), intermediates);

            var wave = effects.FirstOrDefault(e => e.Kind == EffectKind.Wave);
            if (wave is not null && wave.Amount > 0)
                work = TrackIntermediate(work, NewEffectsDistort.ApplyWave(work, wave), intermediates);

            var twirl = effects.FirstOrDefault(e => e.Kind == EffectKind.Twirl);
            if (twirl is not null && Math.Abs(twirl.Amount) > 1e-6)
                work = TrackIntermediate(work, NewEffectsDistort.ApplyTwirl(work, twirl), intermediates);

            var displacement = effects.FirstOrDefault(e => e.Kind == EffectKind.Displacement);
            if (displacement is not null && displacement.Amount > 0)
                work = TrackIntermediate(work, NewEffectsDistort.ApplyDisplacement(work, displacement), intermediates);

            var bulge = effects.FirstOrDefault(e => e.Kind == EffectKind.Bulge);
            if (bulge is not null && bulge.Amount > 0)
                work = TrackIntermediate(work, NewEffectsDistort.ApplyBulge(work, bulge), intermediates);

            var waterRipple = effects.FirstOrDefault(e => e.Kind == EffectKind.WaterRipple);
            if (waterRipple is not null && waterRipple.Amount > 0)
                work = TrackIntermediate(work, NewEffectsDistort.ApplyWaterRipple(work, waterRipple, state.TimeSeconds), intermediates);

            var pixelSort = effects.FirstOrDefault(e => e.Kind == EffectKind.PixelSort);
            if (pixelSort is not null && pixelSort.Amount > 0)
                work = TrackIntermediate(work, NewEffectsDistort.ApplyPixelSort(work, pixelSort), intermediates);

            // ズームブラー・モーションブラー：ずらしたコピーの平均（Plus 合成）でぼかす。
            var zoomBlur = effects.FirstOrDefault(e => e.Kind == EffectKind.ZoomBlur);
            if (zoomBlur is not null && zoomBlur.Amount > 0)
                work = TrackIntermediate(work, BitmapGeometryEffects.ApplyZoomBlur(work, zoomBlur), intermediates);

            var motionBlur = effects.FirstOrDefault(e => e.Kind == EffectKind.MotionBlur);
            if (motionBlur is not null && motionBlur.Amount > 0)
                work = TrackIntermediate(work, BitmapGeometryEffects.ApplyMotionBlur(work, motionBlur), intermediates);

            var colorMatrix = ColorMatrixEffects.BuildColorMatrix(effects);
            if (colorMatrix is not null)
                work = TrackIntermediate(work, ColorMatrixEffects.ApplyColorMatrix(work, colorMatrix), intermediates);

            // 新エフェクト第3弾の色・描画風系：レベル補正・スプリットトーン・グラデーションマップ・
            // ソラリゼーション・エッジ検出・ハーフトーン・油絵風。色調系の直後にかける。
            var levels = effects.FirstOrDefault(e => e.Kind == EffectKind.Levels);
            if (levels is not null && (levels.Amount > 0 || levels.Radius < 1 || Math.Abs(levels.Midtones - 1) > 1e-6))
                work = TrackIntermediate(work, NewEffectsColor.ApplyLevels(work, levels), intermediates);

            var splitTone = effects.FirstOrDefault(e => e.Kind == EffectKind.SplitTone);
            if (splitTone is not null && splitTone.Midtones > 0)
                work = TrackIntermediate(work, NewEffectsColor.ApplySplitTone(work, splitTone), intermediates);

            var gradientMap = effects.FirstOrDefault(e => e.Kind == EffectKind.GradientMap);
            if (gradientMap is not null)
                work = TrackIntermediate(work, NewEffectsColor.ApplyGradientMap(work, gradientMap), intermediates);

            // LUT（3D LUT）：色系チェーンの最後にかける（LUT は最終グレードを
            // 丸ごと表すのが普通で、レベル補正・色温度などの手調整を先に効かせてから
            // LUT で仕上げる）。この後のソラリゼーション・エッジ・油絵風・シャープ等の
            // 描画風／ディテール系は LUT の結果に対してかかる。
            // ファイル未指定・見つからない・不正な場合は ApplyLut がコピーを返す（無効化）。
            var lut = effects.FirstOrDefault(e => e.Kind == EffectKind.Lut);
            if (lut is not null && lut.Amount > 0)
                work = TrackIntermediate(work, LutEffect.ApplyLut(work, lut), intermediates);

            var solarize = effects.FirstOrDefault(e => e.Kind == EffectKind.Solarize);
            if (solarize is not null && solarize.Amount < 1)
                work = TrackIntermediate(work, NewEffectsStyle.ApplySolarize(work, solarize), intermediates);

            var edgeDetect = effects.FirstOrDefault(e => e.Kind == EffectKind.EdgeDetect);
            if (edgeDetect is not null && edgeDetect.Amount > 0)
                work = TrackIntermediate(work, NewEffectsStyle.ApplyEdgeDetect(work, edgeDetect), intermediates);

            var halftone = effects.FirstOrDefault(e => e.Kind == EffectKind.Halftone);
            if (halftone is not null)
                work = TrackIntermediate(work, NewEffectsStyle.ApplyHalftone(work, halftone), intermediates);

            var kuwahara = effects.FirstOrDefault(e => e.Kind == EffectKind.Kuwahara);
            if (kuwahara is not null && kuwahara.Radius > 0)
                work = TrackIntermediate(work, NewEffectsStyle.ApplyKuwahara(work, kuwahara), intermediates);

            // シャープ・エンボス：色調・描画風系の後にかける（油絵風などでなまった輪郭を
            // シャープで締め直せる。エンボスはグレースケール化するため色系の後）。
            var sharpen = effects.FirstOrDefault(e => e.Kind == EffectKind.Sharpen);
            if (sharpen is not null && sharpen.Amount > 0)
                work = TrackIntermediate(work, NewEffectsDetail.ApplySharpen(work, sharpen), intermediates);

            var emboss = effects.FirstOrDefault(e => e.Kind == EffectKind.Emboss);
            if (emboss is not null && emboss.Amount > 0)
                work = TrackIntermediate(work, NewEffectsDetail.ApplyEmboss(work, emboss), intermediates);

            // VHSグリッチ：色調補正の後にかけるとテープ劣化らしくなる。
            var vhs = effects.FirstOrDefault(e => e.Kind == EffectKind.VhsGlitch);
            if (vhs is not null && vhs.Amount > 0)
                work = TrackIntermediate(work, BitmapGeometryEffects.ApplyVhsGlitch(work, vhs, state.TimeSeconds), intermediates);

            // 新エフェクト第3弾の光・フィルム系：ブルーム・ライトリーク・スキャンライン・
            // オールドフィルム・ティルトシフト。VHSグリッチの後にかけると劣化の上に光が乗る。
            var bloom = effects.FirstOrDefault(e => e.Kind == EffectKind.Bloom);
            if (bloom is not null && bloom.Amount > 0)
                work = TrackIntermediate(work, NewEffectsLight.ApplyBloom(work, bloom), intermediates);

            var lightLeak = effects.FirstOrDefault(e => e.Kind == EffectKind.LightLeak);
            if (lightLeak is not null && lightLeak.Amount > 0)
                work = TrackIntermediate(work, NewEffectsLight.ApplyLightLeak(work, lightLeak), intermediates);

            var scanlines = effects.FirstOrDefault(e => e.Kind == EffectKind.Scanlines);
            if (scanlines is not null && scanlines.Amount > 0)
                work = TrackIntermediate(work, NewEffectsLight.ApplyScanlines(work, scanlines), intermediates);

            var oldFilm = effects.FirstOrDefault(e => e.Kind == EffectKind.OldFilm);
            if (oldFilm is not null && oldFilm.Amount > 0)
                work = TrackIntermediate(work, NewEffectsLight.ApplyOldFilm(work, oldFilm, state.TimeSeconds), intermediates);

            var tiltShift = effects.FirstOrDefault(e => e.Kind == EffectKind.TiltShift);
            if (tiltShift is not null && tiltShift.Amount > 0)
                work = TrackIntermediate(work, NewEffectsStyle.ApplyTiltShift(work, tiltShift), intermediates);

            CpuPixelEffects.Apply(work, effects, state.TimeSeconds);

            // レターボックス：黒帯は最後に描く（ノイズやビネットで汚れないよう）。
            var letterbox = effects.FirstOrDefault(e => e.Kind == EffectKind.Letterbox);
            if (letterbox is not null && letterbox.Amount > 0)
                work = TrackIntermediate(work, NewEffectsLight.ApplyLetterbox(work, letterbox), intermediates);

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
                    BlendMode = RenderHelpers.MapBlendMode(state.BlendMode)
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
                    using var blurFilter = RenderHelpers.CreateBlurFilter(blur);
                    using var paint = CreateEffectPaint(state, SKBlendMode.Plus, state.Opacity, blurFilter, channel);
                    DrawImagePass(canvas, work, paint, width, height, scale, state, offsetX, slice, warp);
                }
            }
            else
            {
                using var blurFilter = RenderHelpers.CreateBlurFilter(blur);
                using var paint = CreateEffectPaint(state, RenderHelpers.MapBlendMode(state.BlendMode), state.Opacity, blurFilter, null);
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

    /// <summary>
    /// 明るさ・コントラストのカラーマトリクス。フェーズ1の色調補正の土台。channel を指定するとそのチャンネルだけ残す（RGBシフト用）。
    /// Skia のカラーマトリクスは 0〜1 の浮動小数で扱うため、オフセットも 0〜1
    /// スケールにする（byte スケールの 128 / 255 だとクランプで全白になる。旧版のバグ）。
    /// 式の意味は旧版と同じ：out = contrast * in + (1 - contrast) * 128/255 + brightness。
    /// </summary>
    private static SKColorFilter? ColorCorrectionFilter(double brightness, double contrast, Channel? channel = null)
    {
        if (channel is null && Math.Abs(brightness) < 1e-6 && Math.Abs(contrast - 1.0) < 1e-6)
            return null;

        var c = (float)contrast;
        var offset = (float)((1 - contrast) * 128 / 255.0 + brightness);
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
}
