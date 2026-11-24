// テキストクリップへのエフェクト適用の描画テスト：TimelineFrameRenderer（プレビュー・書き出し共通）で
// テキストにエフェクトが効くこと、エフェクト無しの描画が従来と変わらないこと、
// キー系（背景抜き）は無視されることを確認する。実ファイル・FFmpeg には依存しない
// （フレーム供給はテスト用スタブが白い PNG を返す）。画質の厳密な判定ではなく、
// 「効いている／壊れていない／位置・大きさが変わらない」の確認が目的。

using Editor.Core;
using Editor.Media;
using Editor.Render;
using SkiaSharp;
using Xunit;

namespace Editor.Tests;

/// <summary>テキストクリップへのエフェクト適用の描画テスト（TimelineFrameRenderer 経由）。</summary>
public sealed class TextEffectsRenderTests
{
    private const int Width = 160;
    private const int Height = 90;

    /// <summary>テスト用フレーム供給：真っ白なフレームの PNG を返す（下のトラック用）。</summary>
    private sealed class WhiteFrameSource : IVideoFrameSource
    {
        public Task<byte[]?> GetFramePngAsync(string sourcePath, double timeSeconds, int maxWidth, CancellationToken ct = default)
        {
            using var bitmap = new SKBitmap(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.White);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return Task.FromResult<byte[]?>(data.ToArray());
        }
    }

    private static Clip MakeTextClip(
        ClipEffect? effect = null, string text = "A", string fontColorHex = "#FFFFFFFF", double fontSize = 48)
    {
        var clip = new Clip
        {
            Kind = ClipKind.Text,
            StartSeconds = 0,
            DurationSeconds = 10,
            Text = text,
            FontFamily = string.Empty,
            FontSize = fontSize,
            FontColorHex = fontColorHex
        };
        if (effect is not null)
            clip.Effects.Add(effect);
        return clip;
    }

    private static async Task<SKBitmap> RenderTextAsync(
        Clip clip, double timeSeconds = 0.5, TimelineFrameRenderer? renderer = null)
    {
        var project = new EditorProject { Width = Width, Height = Height, FrameRate = 30 };
        project.Tracks.Add(new Track { Clips = { clip } });
        renderer ??= new TimelineFrameRenderer(new WhiteFrameSource());
        return await renderer.RenderFrameAsync(project, timeSeconds, Width, Height);
    }

    /// <summary>下に白い映像クリップ、上にテキストクリップを重ねて描く（万華鏡の透明背景用）。</summary>
    private static async Task<SKBitmap> RenderLayeredAsync(Clip textClip, double timeSeconds = 0.5)
    {
        var project = new EditorProject { Width = Width, Height = Height, FrameRate = 30 };
        var video = new Clip
        {
            Kind = ClipKind.Video,
            StartSeconds = 0,
            DurationSeconds = 10,
            SourcePath = "white"
        };
        project.Tracks.Add(new Track { Clips = { video } });
        project.Tracks.Add(new Track { Clips = { textClip } });
        var renderer = new TimelineFrameRenderer(new WhiteFrameSource());
        return await renderer.RenderFrameAsync(project, timeSeconds, Width, Height);
    }

    /// <summary>2枚のビットマップが全画素一致すること。</summary>
    private static bool PixelsEqual(SKBitmap a, SKBitmap b)
    {
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
                if (a.GetPixel(x, y) != b.GetPixel(x, y))
                    return false;
        return true;
    }

    /// <summary>2枚のビットマップがどこか1画素でも違うこと。</summary>
    private static bool DiffersAnywhere(SKBitmap a, SKBitmap b)
        => !PixelsEqual(a, b);

    /// <summary>文字の画素（R &gt; 10）の外接矩形。文字が無ければ null。</summary>
    private static (int MinX, int MinY, int MaxX, int MaxY)? GlyphBounds(SKBitmap bitmap)
    {
        int? minX = null, minY = null, maxX = null, maxY = null;
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
            {
                if (bitmap.GetPixel(x, y).Red <= 10)
                    continue;
                minX = minX is null ? x : Math.Min(minX.Value, x);
                minY = minY is null ? y : Math.Min(minY.Value, y);
                maxX = maxX is null ? x : Math.Max(maxX.Value, x);
                maxY = maxY is null ? y : Math.Max(maxY.Value, y);
            }
        if (minX is null || minY is null || maxX is null || maxY is null)
            return null;
        return (minX.Value, minY.Value, maxX.Value, maxY.Value);
    }

    /// <summary>最も明るい画素の座標（文字の確実な内側）。無ければ null。</summary>
    private static (int X, int Y)? BrightestPixel(SKBitmap bitmap)
    {
        var best = -1;
        (int, int)? bestAt = null;
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
            {
                var v = bitmap.GetPixel(x, y).Red;
                if (v > best)
                {
                    best = v;
                    bestAt = (x, y);
                }
            }
        return best <= 0 ? null : bestAt;
    }

    /// <summary>エフェクト無しのテキストが描けること（前提の確認。文字があり、背景は黒）。</summary>
    [Fact]
    public async Task NoEffect_TextRenders()
    {
        using var plain = await RenderTextAsync(MakeTextClip());
        var bounds = GlyphBounds(plain);
        Assert.NotNull(bounds);
        // 中央付近に文字がある（160x90 の中央 80,45 の近く）。
        Assert.InRange(bounds!.Value.MinX, 40, 80);
        Assert.InRange(bounds.Value.MaxX, 80, 120);
        Assert.InRange(bounds.Value.MinY, 10, 45);
        Assert.InRange(bounds.Value.MaxY, 45, 80);
        Assert.Equal(SKColors.Black, plain.GetPixel(5, 5));
    }

    /// <summary>
    /// エフェクト無しの描画が実質無効なエフェクト付きとビット単位で一致すること。
    /// （量 0 のブラー・トーンカーブ・色相 0・彩度 1 は直接描画の経路を通るため、従来の描画結果が変わらない。）
    /// </summary>
    [Fact]
    public async Task NoEffect_PixelIdenticalToNoOpEffects()
    {
        using var plain = await RenderTextAsync(MakeTextClip());
        using var blur0 = await RenderTextAsync(MakeTextClip(new ClipEffect { Kind = EffectKind.Blur, Amount = 0 }));
        using var tone0 = await RenderTextAsync(MakeTextClip(new ClipEffect { Kind = EffectKind.ToneCurve, Amount = 0, Midtones = 0, Radius = 0 }));
        using var hue0 = await RenderTextAsync(MakeTextClip(new ClipEffect { Kind = EffectKind.HueShift, Amount = 0 }));
        using var sat1 = await RenderTextAsync(MakeTextClip(new ClipEffect { Kind = EffectKind.Saturation, Amount = 1 }));
        Assert.True(PixelsEqual(plain, blur0));
        Assert.True(PixelsEqual(plain, tone0));
        Assert.True(PixelsEqual(plain, hue0));
        Assert.True(PixelsEqual(plain, sat1));
    }

    /// <summary>ブラー：文字の縁がにじむ（外側の画素が白っぽくなる）。</summary>
    [Fact]
    public async Task Blur_SoftensTextEdge()
    {
        using var plain = await RenderTextAsync(MakeTextClip());
        var bounds = GlyphBounds(plain);
        Assert.NotNull(bounds);
        using var blurred = await RenderTextAsync(MakeTextClip(new ClipEffect { Kind = EffectKind.Blur, Amount = 8 }));
        Assert.True(DiffersAnywhere(plain, blurred));
        // 文字の右端のすぐ外側（従来は黒）がにじむ。
        Assert.True(blurred.GetPixel(bounds!.Value.MaxX + 3, (bounds.Value.MinY + bounds.Value.MaxY) / 2).Red > 0);
    }

    /// <summary>グロー：文字の周りが明るくなる。</summary>
    [Fact]
    public async Task Glow_BrightensAroundText()
    {
        using var plain = await RenderTextAsync(MakeTextClip());
        using var glow = await RenderTextAsync(MakeTextClip(new ClipEffect { Kind = EffectKind.Glow, Amount = 0.8, Radius = 16 }));
        Assert.True(DiffersAnywhere(plain, glow));
    }

    /// <summary>RGBシフト：文字の縁で R と B が食い違う。</summary>
    [Fact]
    public async Task RgbShift_SplitsTextChannels()
    {
        using var plain = await RenderTextAsync(MakeTextClip());
        using var shifted = await RenderTextAsync(MakeTextClip(new ClipEffect { Kind = EffectKind.RgbShift, Amount = 6 }));
        Assert.True(DiffersAnywhere(plain, shifted));
        var found = false;
        for (var y = 0; y < Height && !found; y++)
            for (var x = 0; x < Width && !found; x++)
            {
                var pixel = shifted.GetPixel(x, y);
                if (Math.Abs(pixel.Red - pixel.Blue) > 8)
                    found = true;
            }
        Assert.True(found);
    }

    /// <summary>スライス・ワープ：文字が横にずれて描かれる。</summary>
    [Fact]
    public async Task SliceAndWarp_DisplaceText()
    {
        using var plain = await RenderTextAsync(MakeTextClip());
        using var sliced = await RenderTextAsync(MakeTextClip(new ClipEffect { Kind = EffectKind.Slice, Amount = 24 }));
        using var warped = await RenderTextAsync(MakeTextClip(new ClipEffect { Kind = EffectKind.Warp, Amount = 20, Radius = 240 }));
        Assert.True(DiffersAnywhere(plain, sliced));
        Assert.True(DiffersAnywhere(plain, warped));
    }

    /// <summary>モザイク：文字がブロック状になる。</summary>
    [Fact]
    public async Task Mosaic_PixelatesText()
    {
        using var plain = await RenderTextAsync(MakeTextClip());
        using var mosaic = await RenderTextAsync(MakeTextClip(new ClipEffect { Kind = EffectKind.Mosaic, Amount = 8 }));
        Assert.True(DiffersAnywhere(plain, mosaic));
    }

    /// <summary>ビネット：画面の隅が暗くなる（文字は中央なのでほぼ変わらないが、隅の画素で確認）。</summary>
    [Fact]
    public async Task Vignette_DarkensCorners()
    {
        // 白い文字では隅の変化が見えないため、画面いっぱいの大きな文字で確認する。
        using var plain = await RenderTextAsync(MakeTextClip(fontSize: 200));
        using var vignette = await RenderTextAsync(MakeTextClip(
            new ClipEffect { Kind = EffectKind.Vignette, Amount = 1, Radius = 1 }, fontSize: 200));
        Assert.True(DiffersAnywhere(plain, vignette));
    }

    /// <summary>色反転：白い文字が黒くなり、背景は黒のまま（透明背景が白く漏れない）。</summary>
    [Fact]
    public async Task Invert_TurnsWhiteTextBlackWithoutLeakingBackground()
    {
        using var plain = await RenderTextAsync(MakeTextClip());
        var bright = BrightestPixel(plain);
        Assert.NotNull(bright);
        using var inverted = await RenderTextAsync(MakeTextClip(new ClipEffect { Kind = EffectKind.Invert, Amount = 1 }));
        Assert.True(DiffersAnywhere(plain, inverted));
        Assert.True(inverted.GetPixel(bright!.Value.X, bright.Value.Y).Red < 8);
        Assert.Equal(SKColors.Black, inverted.GetPixel(5, 5));
    }

    /// <summary>色相・彩度・モノクロ・セピア：赤い文字の色が変わる（位置・大きさは変わらない）。</summary>
    [Fact]
    public async Task ColorEffects_ChangeRedTextColor()
    {
        using var plain = await RenderTextAsync(MakeTextClip(fontColorHex: "#FFFF0000"));
        var bright = BrightestPixel(plain);
        Assert.NotNull(bright);
        var (bx, by) = bright!.Value;
        Assert.True(plain.GetPixel(bx, by).Red > 200 && plain.GetPixel(bx, by).Green < 30);

        using var hue = await RenderTextAsync(MakeTextClip(
            new ClipEffect { Kind = EffectKind.HueShift, Amount = 120 }, fontColorHex: "#FFFF0000"));
        Assert.True(hue.GetPixel(bx, by).Green > 100); // 赤→緑方向へ回る。

        using var mono = await RenderTextAsync(MakeTextClip(
            new ClipEffect { Kind = EffectKind.Monochrome, Amount = 1 }, fontColorHex: "#FFFF0000"));
        var gray = mono.GetPixel(bx, by);
        Assert.Equal(gray.Red, gray.Green);
        Assert.Equal(gray.Red, gray.Blue);
        Assert.InRange(gray.Red, 40, 70); // 赤の輝度 0.213*255 ≒ 54。

        using var sepia = await RenderTextAsync(MakeTextClip(
            new ClipEffect { Kind = EffectKind.Sepia, Amount = 1 }, fontColorHex: "#FFFF0000"));
        Assert.True(DiffersAnywhere(plain, sepia));
    }

    /// <summary>モノクロ：文字の外接矩形が変わらない（位置・大きさが保たれる）。</summary>
    [Fact]
    public async Task Monochrome_PreservesGlyphBounds()
    {
        using var plain = await RenderTextAsync(MakeTextClip(fontColorHex: "#FFFF0000"));
        using var mono = await RenderTextAsync(MakeTextClip(
            new ClipEffect { Kind = EffectKind.Monochrome, Amount = 1 }, fontColorHex: "#FFFF0000"));
        Assert.Equal(GlyphBounds(plain), GlyphBounds(mono));
    }

    /// <summary>ポスタリゼーション・ノイズ・フィルムグレイン：文字の画素値が変わる。</summary>
    [Fact]
    public async Task PosterizeNoiseFilmGrain_ChangeTextPixels()
    {
        using var plain = await RenderTextAsync(MakeTextClip());
        using var posterize = await RenderTextAsync(MakeTextClip(new ClipEffect { Kind = EffectKind.Posterize, Amount = 2 }));
        using var noise = await RenderTextAsync(MakeTextClip(new ClipEffect { Kind = EffectKind.Noise, Amount = 1, Radius = 1 }));
        using var grain = await RenderTextAsync(MakeTextClip(new ClipEffect { Kind = EffectKind.FilmGrain, Amount = 1, Radius = 1 }));
        Assert.True(DiffersAnywhere(plain, posterize));
        Assert.True(DiffersAnywhere(plain, noise));
        Assert.True(DiffersAnywhere(plain, grain));
    }

    /// <summary>色収差・魚眼・VHSグリッチ：文字の形・色が変わる。</summary>
    [Fact]
    public async Task ChromaticAberrationFisheyeVhs_ChangeText()
    {
        using var plain = await RenderTextAsync(MakeTextClip());
        using var ca = await RenderTextAsync(MakeTextClip(new ClipEffect { Kind = EffectKind.ChromaticAberration, Amount = 10 }));
        using var fisheye = await RenderTextAsync(MakeTextClip(new ClipEffect { Kind = EffectKind.Fisheye, Amount = 0.5, Radius = 1.15 }));
        using var vhs = await RenderTextAsync(MakeTextClip(new ClipEffect { Kind = EffectKind.VhsGlitch, Amount = 1, Radius = 3 }));
        Assert.True(DiffersAnywhere(plain, ca));
        Assert.True(DiffersAnywhere(plain, fisheye));
        Assert.True(DiffersAnywhere(plain, vhs));
    }

    /// <summary>
    /// ドロップシャドウ：文字の右下に影が出る。影は黒いため、黒背景では見えない。
    /// 下に白い映像を置き、影で暗くなることで確認する。
    /// </summary>
    [Fact]
    public async Task DropShadow_CastsShadowDownRight()
    {
        using var plain = await RenderTextAsync(MakeTextClip());
        var bounds = GlyphBounds(plain);
        Assert.NotNull(bounds);
        var shadowX = bounds!.Value.MaxX + 4;
        var shadowY = bounds.Value.MaxY + 4;
        using var layeredPlain = await RenderLayeredAsync(MakeTextClip());
        using var layeredShadow = await RenderLayeredAsync(MakeTextClip(new ClipEffect { Kind = EffectKind.DropShadow, Amount = 1, Radius = 4 }));
        Assert.True(DiffersAnywhere(layeredPlain, layeredShadow));
        // 文字の右下の外側：影が無ければ下の白い映像が見え、影があれば暗くなる。
        Assert.Equal(SKColors.White, layeredPlain.GetPixel(shadowX, shadowY));
        Assert.True(layeredShadow.GetPixel(shadowX, shadowY).Red < 200);
    }

    /// <summary>ズームブラー・モーションブラー：文字の縁がにじむ。</summary>
    [Fact]
    public async Task ZoomBlurMotionBlur_SoftenTextEdge()
    {
        using var plain = await RenderTextAsync(MakeTextClip());
        using var zoom = await RenderTextAsync(MakeTextClip(new ClipEffect { Kind = EffectKind.ZoomBlur, Amount = 1, Radius = 8 }));
        using var motion = await RenderTextAsync(MakeTextClip(new ClipEffect { Kind = EffectKind.MotionBlur, Amount = 16, Radius = 0 }));
        Assert.True(DiffersAnywhere(plain, zoom));
        Assert.True(DiffersAnywhere(plain, motion));
    }

    /// <summary>トーンカーブ：ハイライト -0.5 で白い文字がグレー（約128）になり、背景は黒のまま。</summary>
    [Fact]
    public async Task ToneCurve_Highlights_DarkenWhiteText()
    {
        using var plain = await RenderTextAsync(MakeTextClip());
        var bright = BrightestPixel(plain);
        Assert.NotNull(bright);
        using var curved = await RenderTextAsync(MakeTextClip(
            new ClipEffect { Kind = EffectKind.ToneCurve, Amount = 0, Midtones = 0, Radius = -0.5 }));
        Assert.True(DiffersAnywhere(plain, curved));
        var pixel = curved.GetPixel(bright!.Value.X, bright.Value.Y);
        Assert.InRange(pixel.Red, 120, 136); // 255*(1-0.5) ≒ 128。
        Assert.Equal(SKColors.Black, curved.GetPixel(5, 5));
    }

    /// <summary>万華鏡：文字が扇形に複製され、背景の透明部分は下のトラック（白）が見える。</summary>
    [Fact]
    public async Task Kaleidoscope_ReplicatesTextKeepsTransparentBackground()
    {
        using var plain = await RenderTextAsync(MakeTextClip());
        using var kaleido = await RenderTextAsync(MakeTextClip(new ClipEffect { Kind = EffectKind.Kaleidoscope, Amount = 1, Radius = 6 }));
        Assert.True(DiffersAnywhere(plain, kaleido));

        using var layered = await RenderLayeredAsync(MakeTextClip(
            new ClipEffect { Kind = EffectKind.Kaleidoscope, Amount = 1, Radius = 6 }, fontColorHex: "#FF000000"));
        // 隅は文字が無く透明のため、下の白い映像が見える（黒で塗りつぶされていたら白くならない）。
        Assert.Equal(SKColors.White, layered.GetPixel(5, 5));
        Assert.Equal(SKColors.White, layered.GetPixel(155, 5));
        // 万華鏡の文字はどこかにある（白文字では白背景に溶けるため、黒文字で比較する）。
        using var layeredPlain = await RenderLayeredAsync(MakeTextClip(fontColorHex: "#FF000000"));
        Assert.True(DiffersAnywhere(layered, layeredPlain));
    }

    /// <summary>クロマキー：テキストには無効（緑の文字がキー色でも消えない）。</summary>
    [Fact]
    public async Task ChromaKey_IgnoredOnText()
    {
        using var plain = await RenderTextAsync(MakeTextClip(fontColorHex: "#FF00FF00"));
        using var keyed = await RenderTextAsync(MakeTextClip(
            new ClipEffect { Kind = EffectKind.ChromaKey, Amount = 0.3, Radius = 0.15 }, fontColorHex: "#FF00FF00"));
        Assert.True(PixelsEqual(plain, keyed));
    }

    /// <summary>輝度キー：テキストには無効（白い文字が抜けない）。</summary>
    [Fact]
    public async Task LuminanceKey_IgnoredOnText()
    {
        using var plain = await RenderTextAsync(MakeTextClip());
        using var keyed = await RenderTextAsync(MakeTextClip(new ClipEffect { Kind = EffectKind.LuminanceKey, Amount = 0.5, Radius = 0.15 }));
        Assert.True(PixelsEqual(plain, keyed));
    }

    /// <summary>明るさ：暗いグレーの文字が明るくなる（色調補正もテキストに効く）。背景は黒のまま。</summary>
    [Fact]
    public async Task Brightness_AppliesToText()
    {
        var clip = MakeTextClip(fontColorHex: "#FF202020");
        clip.Brightness = 0.5;
        using var bright = await RenderTextAsync(clip);
        var brightPixel = BrightestPixel(bright);
        Assert.NotNull(brightPixel);
        // 32 + 0.5*255 ≒ 160。
        Assert.InRange(bright.GetPixel(brightPixel!.Value.X, brightPixel.Value.Y).Red, 150, 170);
        Assert.Equal(SKColors.Black, bright.GetPixel(5, 5));
    }

    /// <summary>
    /// キーフレーム：ブラーの量を 0→30 に動かすと、t=0 ではエフェクト無しと一致し、
    /// t=1 ではぼやける。エフェクト量のキーフレームがテキストでも評価される。
    /// </summary>
    [Fact]
    public async Task BlurKeyframe_Animates()
    {
        var clip = MakeTextClip(new ClipEffect { Kind = EffectKind.Blur, Amount = 0 });
        var track = clip.EnsureTrack(ClipEffect.KeyframeParameterName(EffectKind.Blur, isAmount: true));
        track.Keyframes.Add(new Keyframe(0.0, 0.0));
        track.Keyframes.Add(new Keyframe(1.0, 30.0));

        using var plain = await RenderTextAsync(MakeTextClip());
        using var atStart = await RenderTextAsync(clip, 0.0);
        using var atEnd = await RenderTextAsync(clip, 1.0);
        using var atMid = await RenderTextAsync(clip, 0.5);
        Assert.True(PixelsEqual(plain, atStart));
        Assert.True(DiffersAnywhere(plain, atEnd));
        Assert.True(DiffersAnywhere(atStart, atMid));
        Assert.True(DiffersAnywhere(atMid, atEnd));
    }

    /// <summary>
    /// エコー：縮んでいく文字の残像が出る（過去の大きい文字が現在の小さい文字からはみ出す）。
    /// 残像は過去フレームを現在の位置に描く仕組みのため、位置のキーフレームで動かした
    /// だけでは残像は現在の文字と重なって見えない。同じレンダラーで2フレーム描く。
    /// </summary>
    [Fact]
    public async Task Echo_FollowsMovingText()
    {
        var clip = MakeTextClip(new ClipEffect { Kind = EffectKind.Echo, Amount = 0.6, Radius = 4 });
        var track = clip.EnsureTrack("Scale");
        track.Keyframes.Add(new Keyframe(0.0, 2.0));
        track.Keyframes.Add(new Keyframe(1.0, 1.0));

        var shrinkingPlain = MakeTextClip();
        var plainTrack = shrinkingPlain.EnsureTrack("Scale");
        plainTrack.Keyframes.Add(new Keyframe(0.0, 2.0));
        plainTrack.Keyframes.Add(new Keyframe(1.0, 1.0));

        var renderer = new TimelineFrameRenderer(new WhiteFrameSource());
        using var noEcho = await RenderTextAsync(shrinkingPlain, 0.5);
        // 履歴をためるため、まず t=0.4 を描いてから t=0.5 を描く。
        using var before = await RenderTextAsync(clip, 0.4, renderer);
        using var echoed = await RenderTextAsync(clip, 0.5, renderer);
        Assert.True(DiffersAnywhere(noEcho, echoed));
        // 残像：現在の文字の外側 4px の帯に、エコー付きだけ明るい画素がある
        // （0.4 秒時点の大きい文字がはみ出す）。帯の基準は残像無しの文字の外接矩形
        // （残像付きの矩形だと残像まで含んでしまい、帯が残像の外に出る）。
        var bounds = GlyphBounds(noEcho);
        Assert.NotNull(bounds);
        var ghostPixels = 0;
        var plainGhostPixels = 0;
        for (var y = bounds!.Value.MinY - 4; y <= bounds.Value.MaxY + 4; y++)
            for (var x = bounds.Value.MinX - 4; x <= bounds.Value.MaxX + 4; x++)
            {
                if (x >= bounds.Value.MinX && x <= bounds.Value.MaxX
                    && y >= bounds.Value.MinY && y <= bounds.Value.MaxY)
                    continue; // 文字の内側は数えない。
                if (echoed.GetPixel(x, y).Red > 32)
                    ghostPixels++;
                if (noEcho.GetPixel(x, y).Red > 32)
                    plainGhostPixels++;
            }
        Assert.True(ghostPixels > 0);
        Assert.Equal(0, plainGhostPixels);
    }
}
