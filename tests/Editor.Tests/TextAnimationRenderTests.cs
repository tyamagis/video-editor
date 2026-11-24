// テキストアニメーションの描画テスト：TimelineFrameRenderer（プレビュー・書き出し共通）で
// フェード・タイプライター・カラオケ・スライド・スケールのアニメーションが描画に
// 反映されること、アニメーション無しの描画が従来と変わらないこと、エフェクト併用
// （オフスクリーンパス）でもアニメーションが効くことを確認する。
// 実ファイル・FFmpeg には依存しない（フレーム供給はテスト用スタブが白い PNG を返す）。
// 画質の厳密な判定ではなく、「効いている／壊れていない／位置・大きさが変わらない」の確認が目的。
// 評価値が予測しやすいよう、数値を検証するテストではイージングを Linear にする
// （既定は EaseOut のため、既定のままでは t=0.5 の不透明度は 0.75 になる）。

using Editor.Core;
using Editor.Media;
using Editor.Render;
using SkiaSharp;
using Xunit;

namespace Editor.Tests;

/// <summary>テキストアニメーションの描画テスト（TimelineFrameRenderer 経由）。</summary>
public sealed class TextAnimationRenderTests
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
        TextAnimation? animationIn = null, string text = "A", string fontColorHex = "#FFFFFFFF",
        double fontSize = 48, double durationSeconds = 10)
    {
        var clip = new Clip
        {
            Kind = ClipKind.Text,
            StartSeconds = 0,
            DurationSeconds = durationSeconds,
            Text = text,
            FontFamily = string.Empty,
            FontSize = fontSize,
            FontColorHex = fontColorHex
        };
        if (animationIn is not null)
            clip.AnimationIn = animationIn;
        return clip;
    }

    private static ClipEffect BlurEffect(double amount = 8)
        => new() { Kind = EffectKind.Blur, Amount = amount };

    private static async Task<SKBitmap> RenderTextAsync(
        Clip clip, double timeSeconds = 0.5, TimelineFrameRenderer? renderer = null)
    {
        var project = new EditorProject { Width = Width, Height = Height, FrameRate = 30 };
        project.Tracks.Add(new Track { Clips = { clip } });
        renderer ??= new TimelineFrameRenderer(new WhiteFrameSource());
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

    /// <summary>最も明るい画素の R 値（0 なら何も描かれていない）。</summary>
    private static int MaxBrightness(SKBitmap bitmap)
    {
        var best = 0;
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
                best = Math.Max(best, bitmap.GetPixel(x, y).Red);
        return best;
    }

    /// <summary>文字の画素（R &gt; 10）の数。</summary>
    private static int CountBrightPixels(SKBitmap bitmap)
    {
        var count = 0;
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
                if (bitmap.GetPixel(x, y).Red > 10)
                    count++;
        return count;
    }

    /// <summary>条件を満たす画素の数。</summary>
    private static int CountPixels(SKBitmap bitmap, Func<SKColor, bool> predicate)
    {
        var count = 0;
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
                if (predicate(bitmap.GetPixel(x, y)))
                    count++;
        return count;
    }

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

    /// <summary>フェードイン：開始時は文字が見えない（ほぼ真っ黒）、途中は薄く、終了後ははっきり見える。</summary>
    [Fact]
    public async Task FadeIn_StartsInvisible_MidFaint_EndClear()
    {
        var anim = new TextAnimation
        {
            Kind = TextAnimationKind.Fade,
            DurationSeconds = 1,
            Easing = TextAnimationEasing.Linear
        };
        var clip = MakeTextClip(anim);

        using var start = await RenderTextAsync(clip, 0.0);
        Assert.InRange(MaxBrightness(start), 0, 10); // 不透明度 0。

        using var mid = await RenderTextAsync(clip, 0.5);
        Assert.InRange(MaxBrightness(mid), 80, 180); // 不透明度 0.5（255*0.5 ≒ 128）。

        using var end = await RenderTextAsync(clip, 1.0);
        Assert.InRange(MaxBrightness(end), 240, 255); // 不透明度 1。

        Assert.True(MaxBrightness(start) < MaxBrightness(mid));
        Assert.True(MaxBrightness(mid) < MaxBrightness(end));
    }

    /// <summary>
    /// フェードアウト（アウト側）：終了間際に薄くなり、終了直前まで文字がある。
    /// アウトも同じ TextAnimationState 経由で描画されることの確認。
    /// </summary>
    [Fact]
    public async Task FadeOut_EndFadesOut()
    {
        var clip = MakeTextClip(durationSeconds: 10);
        clip.AnimationOut = new TextAnimation
        {
            Kind = TextAnimationKind.Fade,
            DurationSeconds = 1,
            Easing = TextAnimationEasing.Linear
        };

        using var before = await RenderTextAsync(clip, 9.0);
        Assert.InRange(MaxBrightness(before), 240, 255); // まだアニメーション区間外。

        using var mid = await RenderTextAsync(clip, 9.5);
        Assert.InRange(MaxBrightness(mid), 80, 180); // 不透明度 0.5。
    }

    /// <summary>
    /// タイプライター：早い時刻より遅い時刻の方が描かれた画素が多い。
    /// 表示部分はフルテキストの左端に揃うため、最初の文字の左端は時刻によらず同じ。
    /// </summary>
    [Fact]
    public async Task Typewriter_LaterTimeHasMorePixels_LeftEdgeStable()
    {
        var anim = new TextAnimation { Kind = TextAnimationKind.Typewriter, DurationSeconds = 1 };
        var clip = MakeTextClip(anim, text: "HELLO", fontSize: 40);

        using var none = await RenderTextAsync(clip, 0.0);
        Assert.Equal(0, CountBrightPixels(none)); // 0 文字。

        using var early = await RenderTextAsync(clip, 0.2);
        using var late = await RenderTextAsync(clip, 0.8);
        var earlyCount = CountBrightPixels(early); // 1 文字。
        var lateCount = CountBrightPixels(late); // 4 文字。
        Assert.True(earlyCount > 0);
        Assert.True(lateCount > earlyCount);

        var earlyBounds = GlyphBounds(early);
        var lateBounds = GlyphBounds(late);
        Assert.NotNull(earlyBounds);
        Assert.NotNull(lateBounds);
        // 最初の文字は同じ位置に描かれる（中央揃えの基準はフルテキストの幅）。
        Assert.InRange(Math.Abs(earlyBounds!.Value.MinX - lateBounds!.Value.MinX), 0, 1);
    }

    /// <summary>カラオケ：先頭の文字がハイライト色（赤）になり、残りは基本色（白）のまま。</summary>
    [Fact]
    public async Task Karaoke_HighlightColorPixelsExist()
    {
        var anim = new TextAnimation
        {
            Kind = TextAnimationKind.Karaoke,
            DurationSeconds = 1,
            HighlightColorHex = "#FFFF0000"
        };
        var clip = MakeTextClip(anim, text: "ABC");

        static bool isRed(SKColor c) => c.Red > 150 && c.Green < 100 && c.Blue < 100;
        static bool isWhite(SKColor c) => c.Red > 200 && c.Green > 200 && c.Blue > 200;

        using var start = await RenderTextAsync(clip, 0.0);
        Assert.Equal(0, CountPixels(start, isRed)); // ハイライト 0 文字。

        using var mid = await RenderTextAsync(clip, 0.5);
        Assert.True(CountPixels(mid, isRed) > 0); // 先頭 1 文字が赤。
        Assert.True(CountPixels(mid, isWhite) > 0); // 残り 2 文字は白。
    }

    /// <summary>カラオケのハイライト色が不正値なら黄色 #FFFF00 にフォールバックする（クラッシュしない）。</summary>
    [Fact]
    public async Task Karaoke_InvalidHighlightColor_FallsBackToYellow()
    {
        var anim = new TextAnimation
        {
            Kind = TextAnimationKind.Karaoke,
            DurationSeconds = 1,
            HighlightColorHex = "not-a-color"
        };
        var clip = MakeTextClip(anim, text: "ABC");

        using var mid = await RenderTextAsync(clip, 0.5);
        static bool isYellow(SKColor c) => c.Red > 150 && c.Green > 150 && c.Blue < 100;
        Assert.True(CountPixels(mid, isYellow) > 0);
    }

    /// <summary>
    /// スライド：アニメーション中の文字位置が最終位置からずれている。
    /// 左から現れる（Direction.Left）ため、途中は最終位置より左にある。
    /// </summary>
    [Fact]
    public async Task Slide_PositionOffsetDuringAnimation()
    {
        var anim = new TextAnimation
        {
            Kind = TextAnimationKind.Slide,
            Direction = TextAnimationDirection.Left,
            DurationSeconds = 1,
            Easing = TextAnimationEasing.Linear,
            Fade = false
        };
        var clip = MakeTextClip(anim);

        using var mid = await RenderTextAsync(clip, 0.5);
        using var end = await RenderTextAsync(clip, 1.0);
        var midBounds = GlyphBounds(mid);
        var endBounds = GlyphBounds(end);
        Assert.NotNull(midBounds);
        Assert.NotNull(endBounds);
        var midCenterX = (midBounds!.Value.MinX + midBounds.Value.MaxX) / 2.0;
        var endCenterX = (endBounds!.Value.MinX + endBounds.Value.MaxX) / 2.0;
        // ずれ量は (1-0.5)*0.25*90 ≒ 11px 左。
        Assert.InRange(endCenterX - midCenterX, 5, 20);

        // 終了後はアニメーション無しと画素一致する。
        using var plain = await RenderTextAsync(MakeTextClip());
        Assert.True(PixelsEqual(plain, end));
    }

    /// <summary>
    /// スケール：アニメーション中の文字が小さい（0.5 倍から始まり 1 倍になる）。
    /// Fade=false にして大きさだけを比べる。
    /// </summary>
    [Fact]
    public async Task Scale_SmallerDuringAnimation()
    {
        var anim = new TextAnimation
        {
            Kind = TextAnimationKind.Scale,
            DurationSeconds = 1,
            Easing = TextAnimationEasing.Linear,
            Fade = false
        };
        var clip = MakeTextClip(anim);

        using var small = await RenderTextAsync(clip, 0.0);
        using var end = await RenderTextAsync(clip, 1.0);
        var smallBounds = GlyphBounds(small);
        var endBounds = GlyphBounds(end);
        Assert.NotNull(smallBounds);
        Assert.NotNull(endBounds);
        var smallWidth = smallBounds!.Value.MaxX - smallBounds.Value.MinX + 1;
        var endWidth = endBounds!.Value.MaxX - endBounds.Value.MinX + 1;
        Assert.InRange((double)smallWidth / endWidth, 0.35, 0.65); // 0.5 倍。

        // 終了後はアニメーション無しと画素一致する。
        using var plain = await RenderTextAsync(MakeTextClip());
        Assert.True(PixelsEqual(plain, end));
    }

    /// <summary>
    /// アニメーション無しのクリップは従来と画素一致する（回帰防止）。
    /// Kind=None のクリップと、終了後のフェード／タイプライターも従来の描画と一致する。
    /// </summary>
    [Fact]
    public async Task NoAnimation_PixelIdenticalToPlain()
    {
        using var plain = await RenderTextAsync(MakeTextClip());

        using var none = await RenderTextAsync(MakeTextClip(new TextAnimation { Kind = TextAnimationKind.None }));
        Assert.True(PixelsEqual(plain, none));

        var fade = MakeTextClip(new TextAnimation
        {
            Kind = TextAnimationKind.Fade,
            DurationSeconds = 1,
            Easing = TextAnimationEasing.Linear
        });
        using var fadeEnd = await RenderTextAsync(fade, 1.0);
        Assert.True(PixelsEqual(plain, fadeEnd));

        var typewriter = MakeTextClip(new TextAnimation
        {
            Kind = TextAnimationKind.Typewriter,
            DurationSeconds = 1
        }, text: "HELLO", fontSize: 40);
        using var plainHello = await RenderTextAsync(MakeTextClip(text: "HELLO", fontSize: 40));
        using var typeEnd = await RenderTextAsync(typewriter, 1.0);
        Assert.True(PixelsEqual(plainHello, typeEnd));
    }

    /// <summary>
    /// エフェクト併用（オフスクリーンパス）でもアニメーションが効くこと。
    /// ブラー付きフェード：開始時は真っ黒、終了後はブラーだけの描画と画素一致する。
    /// </summary>
    [Fact]
    public async Task EffectCombined_BlurAndFade_AnimationWorks()
    {
        var anim = new TextAnimation
        {
            Kind = TextAnimationKind.Fade,
            DurationSeconds = 1,
            Easing = TextAnimationEasing.Linear
        };
        var clip = MakeTextClip(anim);
        clip.Effects.Add(BlurEffect(8));

        using var start = await RenderTextAsync(clip, 0.0);
        Assert.InRange(MaxBrightness(start), 0, 10);

        using var mid = await RenderTextAsync(clip, 0.5);
        Assert.InRange(MaxBrightness(mid), 50, 200);

        var blurOnly = MakeTextClip();
        blurOnly.Effects.Add(BlurEffect(8));
        using var blurPlain = await RenderTextAsync(blurOnly, 0.5);
        using var end = await RenderTextAsync(clip, 1.0);
        Assert.True(DiffersAnywhere(blurPlain, mid));
        Assert.True(PixelsEqual(blurPlain, end));
    }

    /// <summary>空文字のテキストクリップは黒フレームになり、クラッシュしない。</summary>
    [Fact]
    public async Task EmptyText_RendersBlackWithoutCrash()
    {
        var clip = MakeTextClip(
            new TextAnimation { Kind = TextAnimationKind.Fade, DurationSeconds = 1 },
            text: string.Empty);
        using var frame = await RenderTextAsync(clip, 0.5);
        Assert.Equal(0, MaxBrightness(frame));
        Assert.Equal(SKColors.Black, frame.GetPixel(5, 5));
    }
}
