// 新エフェクト2種（Sharpen / Emboss）のテスト。
// 描画ヘルパー（NewEffectsDetail）の直接呼び出し、定義テーブル・キーフレーム・音連動の
// 汎用機構への乗り具合、TimelineFrameRenderer 経由の配線（映像・テキスト）をまとめて確認する。
// 実ファイル・FFmpeg には依存しない。画質の厳密な判定ではなく、
// 「効いている／壊れていない／無効な量では変わらない」の確認が目的。
// パターン作りは NewEffectsStyleTests / NewEffects3RenderTests の流儀に倣う。

using System.Runtime.InteropServices;
using Editor.Core;
using Editor.Render;
using SkiaSharp;
using Xunit;

namespace Editor.Tests;

/// <summary>新エフェクト2種（Sharpen / Emboss）のテスト。</summary>
public sealed class NewEffectsDetailTests
{
    private const int Width = 160;
    private const int Height = 90;

    /// <summary>横方向の正弦波グラデーション（周期40px、振幅100、中心128）。なだらかな輪郭があり、シャープの効きが見えやすい。</summary>
    private static SKBitmap BuildSinePattern()
    {
        var bitmap = new SKBitmap(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
            {
                var v = (byte)Math.Round(128 + 100 * Math.Sin(2 * Math.PI * x / 40.0));
                bitmap.SetPixel(x, y, new SKColor(v, v, v));
            }
        return bitmap;
    }

    /// <summary>左（黒）から右（白）への横グラデーション。画素 x の値は round(x * 255 / 159)。</summary>
    private static SKBitmap BuildGradientPattern()
    {
        var bitmap = new SKBitmap(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        for (var x = 0; x < Width; x++)
        {
            var v = (byte)Math.Round(x * 255.0 / (Width - 1));
            using var paint = new SKPaint { Color = new SKColor(v, v, v) };
            canvas.DrawRect(x, 0, 1, Height, paint);
        }
        return bitmap;
    }

    /// <summary>左半分が黒・右半分が白（x=幅/2 で段差）。エンボスの境界用。</summary>
    private static SKBitmap BuildSplitPattern()
    {
        var bitmap = new SKBitmap(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Black);
        using var paint = new SKPaint { Color = SKColors.White };
        canvas.DrawRect(Width / 2, 0, Width - Width / 2, Height, paint);
        return bitmap;
    }

    /// <summary>青背景に赤い四角。エンボスのグレースケール確認用（色のある画像）。</summary>
    private static SKBitmap BuildColorPattern()
    {
        var bitmap = new SKBitmap(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(0, 0, 200));
        using var paint = new SKPaint { Color = new SKColor(220, 30, 30) };
        canvas.DrawRect(50, 25, 60, 40, paint);
        return bitmap;
    }

    /// <summary>一色の平坦な画像（不透明度も指定できる）。</summary>
    private static SKBitmap BuildFlatPattern(byte value, byte alpha = 255, int width = Width, int height = Height)
    {
        var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(value, value, value, alpha));
        return bitmap;
    }

    /// <summary>透明背景に半透明（不透明度 128）の白い円と、不透明な赤い四角。Premul・透明背景の確認用。</summary>
    private static SKBitmap BuildTransparentPattern()
    {
        var bitmap = new SKBitmap(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { Color = new SKColor(255, 255, 255, 128) };
        canvas.DrawCircle(Width / 2, Height / 2, 30, paint);
        using var red = new SKPaint { Color = new SKColor(255, 0, 0, 255) };
        canvas.DrawRect(10, 10, 20, 20, red);
        return bitmap;
    }

    /// <summary>2枚のビットマップで画素が違う数を数える（GetPixel の比較）。</summary>
    private static int CountDiff(SKBitmap a, SKBitmap b)
    {
        var count = 0;
        for (var y = 0; y < a.Height; y++)
            for (var x = 0; x < a.Width; x++)
                if (a.GetPixel(x, y) != b.GetPixel(x, y))
                    count++;
        return count;
    }

    /// <summary>生の BGRA バイトで Premul の不変条件（RGB ≤ アルファ）が全画素で成り立つこと。</summary>
    private static void AssertPremulValid(SKBitmap bitmap)
    {
        var bytes = new byte[bitmap.RowBytes * bitmap.Height];
        Marshal.Copy(bitmap.GetPixels(), bytes, 0, bytes.Length);
        for (var i = 0; i < bytes.Length; i += 4)
        {
            var a = bytes[i + 3];
            Assert.True(bytes[i] <= a, $"B {bytes[i]} > A {a} at byte {i}");
            Assert.True(bytes[i + 1] <= a, $"G {bytes[i + 1]} > A {a} at byte {i}");
            Assert.True(bytes[i + 2] <= a, $"R {bytes[i + 2]} > A {a} at byte {i}");
        }
    }

    /// <summary>1x1・2x2 の小さいビットマップでも例外が出ず、サイズが保たれること。</summary>
    private static void AssertTinyBitmapsWork(Func<SKBitmap, ClipEffect, SKBitmap> apply, ClipEffect effect)
    {
        using var one = BuildFlatPattern(128, 255, 1, 1);
        using var oneOut = apply(one, effect);
        Assert.Equal(1, oneOut.Width);
        Assert.Equal(1, oneOut.Height);

        using var two = new SKBitmap(2, 2, SKColorType.Bgra8888, SKAlphaType.Premul);
        two.SetPixel(0, 0, SKColors.Black);
        two.SetPixel(1, 0, SKColors.White);
        two.SetPixel(0, 1, SKColors.White);
        two.SetPixel(1, 1, SKColors.Black);
        using var twoOut = apply(two, effect);
        Assert.Equal(2, twoOut.Width);
        Assert.Equal(2, twoOut.Height);
    }

    // ===== Sharpen（直接） =====

    /// <summary>シャープ：既定パラメータで正弦波グラデーションの山がより高く・谷がより低くなる（輪郭＝振幅の強調）。</summary>
    [Fact]
    public void Sharpen_Default_AmplifiesSineWave()
    {
        using var src = BuildSinePattern();
        using var result = NewEffectsDetail.ApplySharpen(src, new ClipEffect { Kind = EffectKind.Sharpen, Amount = 1, Radius = 2 });
        Assert.True(CountDiff(src, result) > 1000);
        // 山（x=10、元は 228）はさらに明るく、谷（x=30、元は 28）はさらに暗くなる。
        Assert.True(result.GetPixel(10, 45).Red > src.GetPixel(10, 45).Red);
        Assert.True(result.GetPixel(30, 45).Red < src.GetPixel(30, 45).Red);
    }

    /// <summary>シャープ：Amount 0 で元の画像と同一（src のコピー）。</summary>
    [Fact]
    public void Sharpen_ZeroAmount_IsIdentity()
    {
        using var src = BuildSinePattern();
        using var result = NewEffectsDetail.ApplySharpen(src, new ClipEffect { Kind = EffectKind.Sharpen, Amount = 0, Radius = 2 });
        Assert.Equal(0, CountDiff(src, result));
    }

    /// <summary>シャープ：平坦な画像は差分が 0 のため、強さを最大にしても変化しない。</summary>
    [Fact]
    public void Sharpen_FlatImage_IsIdentityEvenAtMaxStrength()
    {
        using var src = BuildFlatPattern(128);
        using var result = NewEffectsDetail.ApplySharpen(src, new ClipEffect { Kind = EffectKind.Sharpen, Amount = 2, Radius = 10 });
        Assert.Equal(0, CountDiff(src, result));
    }

    /// <summary>シャープ：範囲外・NaN のパラメータでも例外が出ず、サイズが保たれること（クランプ・既定値化される）。</summary>
    [Fact]
    public void Sharpen_ExtremeParameters_DoNotThrow()
    {
        using var src = BuildSinePattern();
        using var high = NewEffectsDetail.ApplySharpen(src, new ClipEffect { Kind = EffectKind.Sharpen, Amount = 100, Radius = 500 });
        Assert.Equal(Width, high.Width);
        Assert.Equal(Height, high.Height);
        using var nan = NewEffectsDetail.ApplySharpen(src, new ClipEffect { Kind = EffectKind.Sharpen, Amount = double.NaN, Radius = double.NaN });
        Assert.Equal(Width, nan.Width);
        Assert.Equal(Height, nan.Height);
    }

    /// <summary>シャープ：1x1・2x2 でも例外が出ないこと。</summary>
    [Fact]
    public void Sharpen_TinyBitmaps_DoNotThrow()
        => AssertTinyBitmapsWork(
            NewEffectsDetail.ApplySharpen,
            new ClipEffect { Kind = EffectKind.Sharpen, Amount = 1, Radius = 2 });

    // ===== Emboss（直接） =====

    /// <summary>エンボス：平坦な画像は Amount 1 で全面が中間グレー（128）になる（カーネルの総和 0 ＋ バイアス 128）。</summary>
    [Fact]
    public void Emboss_FlatImage_BecomesMidGray()
    {
        using var src = BuildFlatPattern(200);
        using var result = NewEffectsDetail.ApplyEmboss(src, new ClipEffect { Kind = EffectKind.Emboss, Amount = 1, Radius = 1 });
        Assert.Equal(new SKColor(128, 128, 128), result.GetPixel(Width / 2, Height / 2));
        Assert.Equal(new SKColor(128, 128, 128), result.GetPixel(0, 0));
        Assert.Equal(new SKColor(128, 128, 128), result.GetPixel(Width - 1, Height - 1));
    }

    /// <summary>エンボス：段差の境界に明るい稜線が出て、平坦部（黒側・白側とも）は中間グレーになる。</summary>
    [Fact]
    public void Emboss_StepEdge_HasBrightRidgeAndGrayFlats()
    {
        using var src = BuildSplitPattern();
        using var result = NewEffectsDetail.ApplyEmboss(src, new ClipEffect { Kind = EffectKind.Emboss, Amount = 1, Radius = 1 });
        // 境界の両側（x=79・80）は白側のサンプルが正の重みで効いて真っ白に飛ぶ。
        Assert.Equal(255, result.GetPixel(79, 45).Red);
        Assert.Equal(255, result.GetPixel(80, 45).Red);
        // 平坦部は元の明暗によらず中間グレー。
        Assert.Equal(new SKColor(128, 128, 128), result.GetPixel(40, 45));
        Assert.Equal(new SKColor(128, 128, 128), result.GetPixel(120, 45));
    }

    /// <summary>エンボス：Amount 1 では色のある画像でも全画素がグレースケール（R=G=B）になる。</summary>
    [Fact]
    public void Emboss_AmountOne_IsGrayscale()
    {
        using var src = BuildColorPattern();
        using var result = NewEffectsDetail.ApplyEmboss(src, new ClipEffect { Kind = EffectKind.Emboss, Amount = 1, Radius = 1 });
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
            {
                var pixel = result.GetPixel(x, y);
                Assert.Equal(pixel.Red, pixel.Green);
                Assert.Equal(pixel.Green, pixel.Blue);
            }
    }

    /// <summary>エンボス：Amount 0 で元の画像と同一（src のコピー）。</summary>
    [Fact]
    public void Emboss_ZeroAmount_IsIdentity()
    {
        using var src = BuildSplitPattern();
        using var result = NewEffectsDetail.ApplyEmboss(src, new ClipEffect { Kind = EffectKind.Emboss, Amount = 0, Radius = 1 });
        Assert.Equal(0, CountDiff(src, result));
    }

    /// <summary>エンボス：Radius（浮き彫りの距離）が大きいほどグラデーションの浮き彫りが強くなる（中間グレーから離れる）。</summary>
    [Fact]
    public void Emboss_LargerDistance_StrongerOnGradient()
    {
        using var src = BuildGradientPattern();
        using var near = NewEffectsDetail.ApplyEmboss(src, new ClipEffect { Kind = EffectKind.Emboss, Amount = 1, Radius = 1 });
        using var far = NewEffectsDetail.ApplyEmboss(src, new ClipEffect { Kind = EffectKind.Emboss, Amount = 1, Radius = 4 });
        Assert.True(CountDiff(near, far) > 1000);
        Assert.True(Math.Abs(far.GetPixel(80, 45).Red - 128) > Math.Abs(near.GetPixel(80, 45).Red - 128));
    }

    /// <summary>エンボス：範囲外・NaN のパラメータでも例外が出ず、サイズが保たれること（クランプ・既定値化される）。</summary>
    [Fact]
    public void Emboss_ExtremeParameters_DoNotThrow()
    {
        using var src = BuildSplitPattern();
        using var high = NewEffectsDetail.ApplyEmboss(src, new ClipEffect { Kind = EffectKind.Emboss, Amount = 5, Radius = 100 });
        Assert.Equal(Width, high.Width);
        Assert.Equal(Height, high.Height);
        using var nan = NewEffectsDetail.ApplyEmboss(src, new ClipEffect { Kind = EffectKind.Emboss, Amount = double.NaN, Radius = double.NaN });
        Assert.Equal(Width, nan.Width);
        Assert.Equal(Height, nan.Height);
    }

    /// <summary>エンボス：1x1・2x2 でも例外が出ないこと（端の座標クランプで安全）。</summary>
    [Fact]
    public void Emboss_TinyBitmaps_DoNotThrow()
        => AssertTinyBitmapsWork(
            NewEffectsDetail.ApplyEmboss,
            new ClipEffect { Kind = EffectKind.Emboss, Amount = 0.8, Radius = 1 });

    // ===== 横断（直接） =====

    /// <summary>両エフェクト：入力のビットマップを変更しないこと。戻り値は入力と別物であること。</summary>
    [Fact]
    public void SourceBitmap_IsNotModified_ByEitherEffect()
    {
        var cases = new (Func<SKBitmap, ClipEffect, SKBitmap> Apply, ClipEffect Effect)[]
        {
            (NewEffectsDetail.ApplySharpen, new ClipEffect { Kind = EffectKind.Sharpen, Amount = 1, Radius = 2 }),
            (NewEffectsDetail.ApplyEmboss, new ClipEffect { Kind = EffectKind.Emboss, Amount = 0.8, Radius = 1 }),
        };
        foreach (var (apply, effect) in cases)
        {
            using var src = BuildColorPattern();
            using var snapshot = src.Copy();
            using var result = apply(src, effect);
            Assert.NotSame(src, result);
            Assert.Equal(0, CountDiff(src, snapshot));
        }
    }

    /// <summary>両エフェクト：半透明の入力でも Premul の不変条件（RGB ≤ アルファ）が保たれ、透明画素は透明のまま。</summary>
    [Fact]
    public void PremulInvariant_Holds_ForBothEffects()
    {
        var cases = new (Func<SKBitmap, ClipEffect, SKBitmap> Apply, ClipEffect Effect)[]
        {
            (NewEffectsDetail.ApplySharpen, new ClipEffect { Kind = EffectKind.Sharpen, Amount = 2, Radius = 2 }),
            (NewEffectsDetail.ApplyEmboss, new ClipEffect { Kind = EffectKind.Emboss, Amount = 1, Radius = 1 }),
        };
        foreach (var (apply, effect) in cases)
        {
            using var src = BuildTransparentPattern();
            using var result = apply(src, effect);
            AssertPremulValid(result);
            // 左下隅は完全な透明。アルファは元のまま保持され、RGB も 0 のまま
            // （SKColors.Transparent は (255,255,255,0) なので成分で比べる）。
            var corner = result.GetPixel(0, Height - 1);
            Assert.Equal(0, corner.Alpha);
            Assert.Equal(0, corner.Red);
            Assert.Equal(0, corner.Green);
            Assert.Equal(0, corner.Blue);
        }
    }

    // ===== 定義・汎用機構 =====

    /// <summary>新2種の enum 数値（末尾 47・48）と定義（表示名・ラベル・既定値・範囲・テキスト適用可）。</summary>
    [Fact]
    public void NewKinds_ValuesAndDefinitions()
    {
        Assert.Equal(47, (int)EffectKind.Sharpen);
        Assert.Equal(48, (int)EffectKind.Emboss);
        Assert.Equal(46, (int)EffectKind.TiltShift); // 既存の末尾も変わっていないこと。

        var sharpen = EffectDefinitions.Get(EffectKind.Sharpen)!;
        Assert.Equal("シャープ", sharpen.DisplayName);
        Assert.Equal(1, sharpen.DefaultAmount);
        Assert.Equal(2, sharpen.DefaultRadius);
        Assert.InRange(sharpen.DefaultAmount, sharpen.AmountMin, sharpen.AmountMax);
        Assert.True(sharpen.UsesRadius);
        Assert.InRange(sharpen.DefaultRadius, sharpen.RadiusMin!.Value, sharpen.RadiusMax!.Value);
        Assert.False(string.IsNullOrEmpty(sharpen.RadiusLabel));
        Assert.True(sharpen.AppliesToText);

        var emboss = EffectDefinitions.Get(EffectKind.Emboss)!;
        Assert.Equal("エンボス", emboss.DisplayName);
        Assert.Equal(0.8, emboss.DefaultAmount);
        Assert.Equal(1, emboss.DefaultRadius);
        Assert.InRange(emboss.DefaultAmount, emboss.AmountMin, emboss.AmountMax);
        Assert.True(emboss.UsesRadius);
        Assert.InRange(emboss.DefaultRadius, emboss.RadiusMin!.Value, emboss.RadiusMax!.Value);
        Assert.False(string.IsNullOrEmpty(emboss.RadiusLabel));
        Assert.True(emboss.AppliesToText);
        Assert.True(EffectDefinitions.AppliesToText(EffectKind.Sharpen));
        Assert.True(EffectDefinitions.AppliesToText(EffectKind.Emboss));
    }

    /// <summary>新2種の Amount / Radius の丸めが定義の範囲に収まること。</summary>
    [Fact]
    public void NewKinds_Clamp()
    {
        Assert.Equal(2, ClipEffect.ClampAmount(EffectKind.Sharpen, 100));
        Assert.Equal(0, ClipEffect.ClampAmount(EffectKind.Sharpen, -1));
        Assert.Equal(10, ClipEffect.ClampRadius(EffectKind.Sharpen, 100));
        Assert.Equal(1, ClipEffect.ClampRadius(EffectKind.Sharpen, 0));
        Assert.Equal(1, ClipEffect.ClampAmount(EffectKind.Emboss, 100));
        Assert.Equal(0, ClipEffect.ClampAmount(EffectKind.Emboss, -1));
        Assert.Equal(4, ClipEffect.ClampRadius(EffectKind.Emboss, 100));
        Assert.Equal(1, ClipEffect.ClampRadius(EffectKind.Emboss, 0));
        Assert.True(ClipEffect.UsesRadius(EffectKind.Sharpen));
        Assert.True(ClipEffect.UsesRadius(EffectKind.Emboss));
    }

    /// <summary>新2種のキーフレーム（"Effect:{種類}.Amount/Radius"）が評価で効くこと。</summary>
    [Theory]
    [InlineData(EffectKind.Sharpen, true, 1, 2)]
    [InlineData(EffectKind.Sharpen, false, 2, 8)]
    [InlineData(EffectKind.Emboss, true, 0.8, 0.3)]
    [InlineData(EffectKind.Emboss, false, 1, 3)]
    public void NewKinds_KeyframeEvaluation(EffectKind kind, bool isAmount, double fixedValue, double keyedValue)
    {
        var parameter = ClipEffect.KeyframeParameterName(kind, isAmount);
        var clip = new Clip { StartSeconds = 0, DurationSeconds = 10 };
        var effect = new ClipEffect { Kind = kind, Amount = fixedValue, Radius = fixedValue };
        clip.Effects.Add(effect);
        clip.KeyframeTracks.Add(new KeyframeTrack
        {
            ParameterName = parameter,
            Keyframes =
            {
                new Keyframe { TimeSeconds = 0, Value = fixedValue },
                new Keyframe { TimeSeconds = 2, Value = keyedValue }
            }
        });

        var atStart = TimelineEvaluator.EvaluateClip(clip, 0);
        var atEnd = TimelineEvaluator.EvaluateClip(clip, 2);
        var evaluated = isAmount ? atEnd.Effects[0].Amount : atEnd.Effects[0].Radius;
        var initial = isAmount ? atStart.Effects[0].Amount : atStart.Effects[0].Radius;
        Assert.Equal(fixedValue, initial);
        Assert.Equal(keyedValue, evaluated);
    }

    /// <summary>新2種のエフェクト量が音連動バインドの対象になること（汎用機構は種類に依存しない）。</summary>
    [Fact]
    public void NewKinds_AudioBindingApplies()
    {
        var audio = new Clip
        {
            Id = "audio1",
            Kind = ClipKind.Audio,
            StartSeconds = 0,
            DurationSeconds = 10,
            SourcePath = "audio.mp3"
        };
        var target = new Clip { StartSeconds = 0, DurationSeconds = 10 };
        target.Effects.Add(new ClipEffect { Kind = EffectKind.Sharpen, Amount = 1 });
        target.AudioBindings.Add(new AudioBinding
        {
            ParameterName = ClipEffect.KeyframeParameterName(EffectKind.Sharpen, isAmount: true),
            SourceClipId = "audio1",
            LowHz = 20,
            HighHz = 80,
            MinValue = 0,
            MaxValue = 1,
            Combine = AudioBindCombine.Add
        });
        var project = new EditorProject();
        project.Tracks.Add(new Track { Clips = { audio, target } });

        // 帯域エネルギー 1.0（最大）なら、キーフレーム値 1 に (1 - 0) が加算されて 2 になる。
        var evaluated = TimelineEvaluator.EvaluateClip(target, 1, 1.0, project, new FixedEnergyProvider(1.0));
        Assert.Equal(2, evaluated.Effects[0].Amount);
        // 無音（エネルギー 0）ならキーフレーム値のまま。
        var silent = TimelineEvaluator.EvaluateClip(target, 1, 1.0, project, new FixedEnergyProvider(0.0));
        Assert.Equal(1, silent.Effects[0].Amount);
    }

    /// <summary>固定の帯域エネルギーを返すテスト用スタブ。</summary>
    private sealed class FixedEnergyProvider : IAudioBandEnergyProvider
    {
        private readonly double _energy;

        public FixedEnergyProvider(double energy) => _energy = energy;

        public void Precompute(string clipId, string sourcePath, double frameRate)
        {
        }

        public double GetBandEnergy(string clipId, double timeSeconds, double lowHz, double highHz)
            => _energy;
    }

    // ===== レンダー（TimelineFrameRenderer 経由） =====

    /// <summary>テスト用フレーム供給：SourcePath に応じてテストパターンの PNG を返す。</summary>
    private sealed class PatternFrameSource : IVideoFrameSource
    {
        public Task<byte[]?> GetFramePngAsync(string sourcePath, double timeSeconds, int maxWidth, CancellationToken ct = default)
        {
            using var bitmap = sourcePath switch
            {
                "pattern-noise" => BuildNoisePattern(),
                "pattern-black" => BuildBlackPattern(),
                _ => BuildCirclePattern()
            };
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return Task.FromResult<byte[]?>(data.ToArray());
        }

        /// <summary>黒背景に白い円（中心 80,45、半径 30）。</summary>
        private static SKBitmap BuildCirclePattern()
        {
            var bitmap = new SKBitmap(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Black);
            using var paint = new SKPaint { Color = SKColors.White };
            canvas.DrawCircle(Width / 2, Height / 2, 30, paint);
            return bitmap;
        }

        /// <summary>決定的な疑似ランダム輝度（細かい模様があり、シャープで変わりやすい）。</summary>
        private static SKBitmap BuildNoisePattern()
        {
            var bitmap = new SKBitmap(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            for (var y = 0; y < Height; y++)
                for (var x = 0; x < Width; x++)
                {
                    var v = (byte)((x * 41 + y * 29) % 256);
                    bitmap.SetPixel(x, y, new SKColor(v, v, v));
                }
            return bitmap;
        }

        /// <summary>真っ黒なフレーム（テキストの下のトラック用）。</summary>
        private static SKBitmap BuildBlackPattern()
        {
            var bitmap = new SKBitmap(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Black);
            return bitmap;
        }
    }

    private static async Task<SKBitmap> RenderAsync(ClipEffect effect, string pattern = "pattern-circle", double timeSeconds = 0.5)
    {
        var project = new EditorProject { Width = Width, Height = Height, FrameRate = 30 };
        var clip = new Clip
        {
            Kind = ClipKind.Video,
            StartSeconds = 0,
            DurationSeconds = 10,
            SourcePath = pattern
        };
        clip.Effects.Add(effect);
        project.Tracks.Add(new Track { Clips = { clip } });
        var renderer = new TimelineFrameRenderer(new PatternFrameSource());
        return await renderer.RenderFrameAsync(project, timeSeconds, Width, Height);
    }

    private static async Task<SKBitmap> RenderPlainAsync(string pattern = "pattern-circle")
        => await RenderAsync(new ClipEffect { Kind = EffectKind.Blur, Amount = 0 }, pattern);

    /// <summary>下に黒い映像クリップ、上にテキストクリップを重ねて描く。</summary>
    private static async Task<SKBitmap> RenderTextLayeredAsync(ClipEffect? effect, double timeSeconds = 0.5)
    {
        var project = new EditorProject { Width = Width, Height = Height, FrameRate = 30 };
        var video = new Clip
        {
            Kind = ClipKind.Video,
            StartSeconds = 0,
            DurationSeconds = 10,
            SourcePath = "pattern-black"
        };
        project.Tracks.Add(new Track { Clips = { video } });
        var text = new Clip
        {
            Kind = ClipKind.Text,
            StartSeconds = 0,
            DurationSeconds = 10,
            Text = "A",
            FontFamily = string.Empty,
            FontSize = 48,
            FontColorHex = "#FFFFFFFF"
        };
        if (effect is not null)
            text.Effects.Add(effect);
        project.Tracks.Add(new Track { Clips = { text } });
        var renderer = new TimelineFrameRenderer(new PatternFrameSource());
        return await renderer.RenderFrameAsync(project, timeSeconds, Width, Height);
    }

    /// <summary>シャープ：細かい模様のある映像が見た目に変わる（配線確認）。</summary>
    [Fact]
    public async Task Render_Sharpen_ChangesNoisePattern()
    {
        using var plain = await RenderPlainAsync("pattern-noise");
        using var sharpened = await RenderAsync(new ClipEffect { Kind = EffectKind.Sharpen, Amount = 1, Radius = 2 }, "pattern-noise");
        Assert.True(CountDiff(plain, sharpened) > 100);
    }

    /// <summary>シャープ：Amount 0 では無加工の描画と完全一致する。</summary>
    [Fact]
    public async Task Render_Sharpen_ZeroAmount_MatchesPlain()
    {
        using var plain = await RenderPlainAsync("pattern-noise");
        using var sharpened = await RenderAsync(new ClipEffect { Kind = EffectKind.Sharpen, Amount = 0, Radius = 2 }, "pattern-noise");
        Assert.Equal(0, CountDiff(plain, sharpened));
    }

    /// <summary>エンボス：円の内側の平坦な白が中間グレーになり、輪郭に浮き彫りが出る（配線確認）。</summary>
    [Fact]
    public async Task Render_Emboss_TurnsCircleInteriorGray()
    {
        using var plain = await RenderPlainAsync();
        using var embossed = await RenderAsync(new ClipEffect { Kind = EffectKind.Emboss, Amount = 1, Radius = 1 });
        Assert.True(CountDiff(plain, embossed) > 100);
        var interior = embossed.GetPixel(80, 45);
        Assert.InRange(interior.Red, 120, 136);
        Assert.Equal(interior.Red, interior.Green);
        Assert.Equal(interior.Green, interior.Blue);
    }

    /// <summary>エンボス：Amount 0 では無加工の描画と完全一致する。</summary>
    [Fact]
    public async Task Render_Emboss_ZeroAmount_MatchesPlain()
    {
        using var plain = await RenderPlainAsync();
        using var embossed = await RenderAsync(new ClipEffect { Kind = EffectKind.Emboss, Amount = 0, Radius = 1 });
        Assert.Equal(0, CountDiff(plain, embossed));
    }

    /// <summary>テキスト＋シャープ：黒背景の白文字の輪郭が強調されて見た目が変わる（テキストへの適用）。</summary>
    [Fact]
    public async Task Render_TextWithSharpen_ChangesText()
    {
        using var plain = await RenderTextLayeredAsync(null);
        using var sharpened = await RenderTextLayeredAsync(new ClipEffect { Kind = EffectKind.Sharpen, Amount = 2, Radius = 2 });
        Assert.True(CountDiff(plain, sharpened) > 10);
    }

    /// <summary>テキスト＋エンボス：黒背景の白文字が浮き彫り調に変わる（テキストへの適用）。</summary>
    [Fact]
    public async Task Render_TextWithEmboss_ChangesText()
    {
        using var plain = await RenderTextLayeredAsync(null);
        using var embossed = await RenderTextLayeredAsync(new ClipEffect { Kind = EffectKind.Emboss, Amount = 1, Radius = 1 });
        Assert.True(CountDiff(plain, embossed) > 10);
    }
}
