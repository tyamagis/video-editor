// LUT（3D LUT）エフェクトのテスト。
// .cube パーサ（正常系・異常系）、トリリニア補間の正確さ、描画ヘルパー
// （恒等LUT・Amount混合・Premul・ファイル無しでの無効化・キャッシュ再読み込み）、
// TimelineFrameRenderer 経由の配線（映像・テキスト・キーフレーム）、
// JSON 往復と旧 JSON の読み込みを確認する。実ファイルは TempDirectory 配下だけを使う。

using System.Globalization;
using System.Runtime.InteropServices;
using Editor.Core;
using Editor.Render;
using SkiaSharp;
using Xunit;

namespace Editor.Tests;

/// <summary>LUTエフェクト（CubeLut パーサ／LutEffect 描画／配線／保存）のテスト。</summary>
public sealed class LutTests
{
    private const int Width = 160;
    private const int Height = 90;

    // ---------- .cube テキスト生成ヘルパー ----------

    /// <summary>恒等 LUT（出力＝入力）の .cube テキスト。size は一辺の格子数。</summary>
    private static string IdentityCubeText(int size, string title = "")
    {
        var lines = new List<string>();
        if (title.Length > 0)
            lines.Add($"TITLE \"{title}\"");
        lines.Add($"LUT_3D_SIZE {size}");
        for (var b = 0; b < size; b++)
            for (var g = 0; g < size; g++)
                for (var r = 0; r < size; r++)
                    lines.Add($"{F(r / (double)(size - 1))} {F(g / (double)(size - 1))} {F(b / (double)(size - 1))}");
        return string.Join("\n", lines);
    }

    /// <summary>反転 LUT（出力＝1−入力）の .cube テキスト。</summary>
    private static string InvertCubeText(int size)
    {
        var lines = new List<string> { $"LUT_3D_SIZE {size}" };
        for (var b = 0; b < size; b++)
            for (var g = 0; g < size; g++)
                for (var r = 0; r < size; r++)
                    lines.Add($"{F(1 - r / (double)(size - 1))} {F(1 - g / (double)(size - 1))} {F(1 - b / (double)(size - 1))}");
        return string.Join("\n", lines);
    }

    private static string F(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private static string WriteCube(TempDirectory temp, string name, string text)
    {
        var path = temp.File(name);
        File.WriteAllText(path, text);
        return path;
    }

    // ---------- パーサ：正常系 ----------

    [Fact]
    public void Parse_Identity_SizeAndCorners()
    {
        Assert.True(CubeLut.TryParse(IdentityCubeText(2), out var lut, out var error), error);
        Assert.Equal(2, lut!.Size);
        Assert.Equal(8 * 3, lut.Data.Length);
        // 格子の隅は入力そのもの（恒等）。
        Assert.Equal((0f, 0f, 0f), lut.Sample(0, 0, 0));
        Assert.Equal((1f, 1f, 1f), lut.Sample(1, 1, 1));
        Assert.Equal((1f, 0f, 0f), lut.Sample(1, 0, 0));
    }

    [Fact]
    public void Parse_TitleAndCommentsAndBlankLines()
    {
        var text = "# コメント\n\nTITLE \"My LUT\"\n" + IdentityCubeText(2) + "\n# 末尾コメント\n";
        Assert.True(CubeLut.TryParse(text, out var lut, out var error), error);
        Assert.Equal("My LUT", lut!.Title);
    }

    [Fact]
    public void Parse_CrLfAndExtraSpaces()
    {
        var text = IdentityCubeText(2).Replace(" ", "  ").Replace("\n", "\r\n");
        Assert.True(CubeLut.TryParse(text, out var lut, out var error), error);
        Assert.Equal((1f, 0f, 1f), lut!.Sample(1, 0, 1));
    }

    [Fact]
    public void Parse_Domain_RemapsInput()
    {
        // ドメイン 0.25〜0.75 を 0〜1 に伸ばす恒等 LUT。
        var text = "DOMAIN_MIN 0.25 0.25 0.25\nDOMAIN_MAX 0.75 0.75 0.75\n" + IdentityCubeText(2);
        Assert.True(CubeLut.TryParse(text, out var lut, out var error), error);
        Assert.Equal((0.25f, 0.25f, 0.25f), lut!.DomainMin);
        Assert.Equal((0.5f, 0.5f, 0.5f), lut.Sample(0.5f, 0.5f, 0.5f));
        Assert.Equal((0f, 0f, 0f), lut.Sample(0f, 0f, 0f)); // ドメイン下限未満は 0 に丸める。
        Assert.Equal((1f, 1f, 1f), lut.Sample(1f, 1f, 1f)); // ドメイン上限超は 1 に丸める。
    }

    [Fact]
    public void Parse_With1DTable_SkipsItAndReads3D()
    {
        var lines = new List<string> { "LUT_1D_SIZE 4", "LUT_3D_SIZE 2" };
        for (var i = 0; i < 4; i++)
            lines.Add("0 0 0"); // 1D テーブル（読み飛ばされる）。
        lines.AddRange(IdentityCubeText(2).Split('\n').Skip(1));
        Assert.True(CubeLut.TryParse(string.Join("\n", lines), out var lut, out var error), error);
        Assert.Equal((1f, 1f, 0f), lut!.Sample(1, 1, 0));
    }

    [Fact]
    public void TryLoad_ValidFile()
    {
        using var temp = new TempDirectory();
        var path = WriteCube(temp, "ok.cube", IdentityCubeText(4, "ok"));
        Assert.True(CubeLut.TryLoad(path, out var lut, out var error), error);
        Assert.Equal(4, lut!.Size);
        Assert.Equal("ok", lut.Title);
    }

    // ---------- パーサ：異常系（例外でなく false） ----------

    [Fact]
    public void TryLoad_MissingFile_ReturnsFalse()
    {
        using var temp = new TempDirectory();
        Assert.False(CubeLut.TryLoad(temp.File("none.cube"), out var lut, out var error));
        Assert.Null(lut);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n # コメントだけ\n")]
    [InlineData("TITLE \"x\"\n0 0 0\n")] // LUT_3D_SIZE 無し。
    [InlineData("LUT_3D_SIZE 1\n0 0 0\n")] // サイズ 1 は不可。
    [InlineData("LUT_3D_SIZE abc\n0 0 0\n")] // サイズが数値でない。
    [InlineData("LUT_3D_SIZE 2\n0 0 0\n")] // データ不足。
    [InlineData("LUT_3D_SIZE 2\n0 0 0\n0 0 0\n0 0 0\n0 0 0\n0 0 0\n0 0 0\n0 0 0\n0 0 0\n0 0 0\n")] // データ過多（9行）。
    [InlineData("LUT_3D_SIZE 2\n0 0 0\n0 0 0\n0 0 0\n0 0 0\n0 0 0\n0 0 0\n0 0 0\nx y z\n")] // データ行が数値でない。
    [InlineData("LUT_3D_SIZE 2\n0 0 0\n0 0 0\n0 0 0\n0 0 0\n0 0 0\n0 0 0\n0 0 0\nNaN 0 0\n")] // NaN は不可。
    [InlineData("DOMAIN_MIN 0.5 0 0\nDOMAIN_MAX 0.5 1 1\nLUT_3D_SIZE 2\n0 0 0\n0 0 0\n0 0 0\n0 0 0\n0 0 0\n0 0 0\n0 0 0\n0 0 0\n")] // ドメイン逆転。
    [InlineData("LUT_1D_SIZE 4\n0 0 0\n0 0 0\n0 0 0\n0 0 0\n")] // 1D のみは未対応。
    public void TryParse_Invalid_ReturnsFalse(string text)
    {
        Assert.False(CubeLut.TryParse(text, out var lut, out var error));
        Assert.Null(lut);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void TryParse_TooLargeSize_ReturnsFalse()
    {
        Assert.False(CubeLut.TryParse($"LUT_3D_SIZE {CubeLut.MaxSize + 1}\n", out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    // ---------- トリリニア補間 ----------

    [Fact]
    public void Sample_Identity17_AnyColorUnchanged()
    {
        Assert.True(CubeLut.TryParse(IdentityCubeText(17), out var lut, out var error), error);
        var (r, g, b) = lut!.Sample(0.25f, 0.5f, 0.75f);
        Assert.Equal(0.25f, r, 5);
        Assert.Equal(0.5f, g, 5);
        Assert.Equal(0.75f, b, 5);
    }

    [Fact]
    public void Sample_Trilinear_InterpolatesBetweenGridPoints()
    {
        // R チャンネルだけ r×g（隅では積、中間はトリリニア＝双線形で厳密に再現される）。
        var size = 2;
        var data = new float[size * size * size * 3];
        for (var bb = 0; bb < size; bb++)
            for (var gg = 0; gg < size; gg++)
                for (var rr = 0; rr < size; rr++)
                {
                    var i = ((bb * size) + gg) * size + rr;
                    data[i * 3] = rr * gg;
                }
        var lut = new CubeLut(size, data);
        var (r, _, _) = lut.Sample(0.5f, 0.5f, 0.3f);
        Assert.Equal(0.25f, r, 6);
        var (r2, _, _) = lut.Sample(0.25f, 1f, 0f);
        Assert.Equal(0.25f, r2, 6);
    }

    [Fact]
    public void Sample_InvertCorners()
    {
        Assert.True(CubeLut.TryParse(InvertCubeText(2), out var lut, out var error), error);
        Assert.Equal((1f, 1f, 1f), lut!.Sample(0, 0, 0));
        Assert.Equal((0.5f, 0.5f, 0.5f), lut.Sample(0.5f, 0.5f, 0.5f));
    }

    // ---------- 描画ヘルパー（LutEffect.ApplyLut 直接） ----------

    private static SKBitmap BuildFlat(byte r, byte g, byte b, byte a = 255)
    {
        var bitmap = new SKBitmap(16, 16, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(r, g, b, a));
        return bitmap;
    }

    [Fact]
    public void Apply_IdentityLut_Unchanged()
    {
        using var temp = new TempDirectory();
        var path = WriteCube(temp, "id.cube", IdentityCubeText(17));
        using var src = BuildFlat(100, 150, 200);
        using var dst = LutEffect.ApplyLut(src, new ClipEffect { Kind = EffectKind.Lut, Amount = 1, LutPath = path });
        Assert.Equal(new SKColor(100, 150, 200), dst.GetPixel(8, 8));
    }

    [Fact]
    public void Apply_InvertLut_InvertsColor()
    {
        using var temp = new TempDirectory();
        var path = WriteCube(temp, "inv.cube", InvertCubeText(17));
        using var src = BuildFlat(100, 150, 200);
        using var dst = LutEffect.ApplyLut(src, new ClipEffect { Kind = EffectKind.Lut, Amount = 1, LutPath = path });
        var pixel = dst.GetPixel(8, 8);
        Assert.InRange(pixel.Red, 154, 156);
        Assert.InRange(pixel.Green, 104, 106);
        Assert.InRange(pixel.Blue, 54, 56);
    }

    [Fact]
    public void Apply_AmountHalf_MixesHalfway()
    {
        using var temp = new TempDirectory();
        var path = WriteCube(temp, "inv.cube", InvertCubeText(17));
        using var src = BuildFlat(100, 150, 200);
        using var dst = LutEffect.ApplyLut(src, new ClipEffect { Kind = EffectKind.Lut, Amount = 0.5, LutPath = path });
        var pixel = dst.GetPixel(8, 8);
        Assert.InRange(pixel.Red, 126, 129); // (100 + 155) / 2 = 127.5。
        Assert.InRange(pixel.Green, 126, 129);
        Assert.InRange(pixel.Blue, 126, 129);
    }

    [Fact]
    public void Apply_ZeroAmount_Unchanged()
    {
        using var temp = new TempDirectory();
        var path = WriteCube(temp, "inv.cube", InvertCubeText(2));
        using var src = BuildFlat(100, 150, 200);
        using var dst = LutEffect.ApplyLut(src, new ClipEffect { Kind = EffectKind.Lut, Amount = 0, LutPath = path });
        Assert.Equal(new SKColor(100, 150, 200), dst.GetPixel(8, 8));
    }

    [Fact]
    public void Apply_MissingOrInvalidOrUnspecified_Unchanged()
    {
        using var temp = new TempDirectory();
        var badPath = WriteCube(temp, "bad.cube", "これは LUT ではありません");
        using var src = BuildFlat(100, 150, 200);
        foreach (var path in new[] { string.Empty, temp.File("none.cube"), badPath })
        {
            using var dst = LutEffect.ApplyLut(src, new ClipEffect { Kind = EffectKind.Lut, Amount = 1, LutPath = path });
            Assert.Equal(new SKColor(100, 150, 200), dst.GetPixel(8, 8));
        }
    }

    [Fact]
    public void Apply_SemiTransparent_KeepsPremulAndAlpha()
    {
        using var temp = new TempDirectory();
        var path = WriteCube(temp, "inv.cube", InvertCubeText(17));
        // 半透明の白（Premul では RGB=128, A=128）。反転で黒になり、アルファは保たれる。
        using var src = BuildFlat(255, 255, 255, 128);
        using var dst = LutEffect.ApplyLut(src, new ClipEffect { Kind = EffectKind.Lut, Amount = 1, LutPath = path });
        var pixel = dst.GetPixel(8, 8);
        Assert.Equal(128, pixel.Alpha);
        Assert.Equal(0, pixel.Red);
        Assert.Equal(0, pixel.Green);
        Assert.Equal(0, pixel.Blue);
        AssertPremulValid(dst);
    }

    [Fact]
    public void Apply_TransparentPixel_StaysTransparent()
    {
        using var temp = new TempDirectory();
        var path = WriteCube(temp, "inv.cube", InvertCubeText(2));
        using var src = BuildFlat(0, 0, 0, 0);
        using var dst = LutEffect.ApplyLut(src, new ClipEffect { Kind = EffectKind.Lut, Amount = 1, LutPath = path });
        Assert.Equal(new SKColor(0, 0, 0, 0), dst.GetPixel(8, 8));
    }

    [Fact]
    public void Apply_FileReplaced_ReparsesViaIdentity()
    {
        using var temp = new TempDirectory();
        var path = WriteCube(temp, "swap.cube", IdentityCubeText(2));
        using var src = BuildFlat(100, 150, 200);
        var effect = new ClipEffect { Kind = EffectKind.Lut, Amount = 1, LutPath = path };
        using (var first = LutEffect.ApplyLut(src, effect))
            Assert.Equal(new SKColor(100, 150, 200), first.GetPixel(8, 8));

        // 同じパスで中身を差し替える（長さも変わるため SourceIdentity が変わる）。
        File.WriteAllText(path, InvertCubeText(17));
        using var second = LutEffect.ApplyLut(src, effect);
        Assert.InRange(second.GetPixel(8, 8).Red, 154, 156);
    }

    [Fact]
    public void Apply_LargeLut33_IdentityUnchanged()
    {
        using var temp = new TempDirectory();
        var path = WriteCube(temp, "big.cube", IdentityCubeText(33));
        using var src = BuildFlat(37, 111, 222);
        using var dst = LutEffect.ApplyLut(src, new ClipEffect { Kind = EffectKind.Lut, Amount = 1, LutPath = path });
        Assert.Equal(new SKColor(37, 111, 222), dst.GetPixel(8, 8));
    }

    private static void AssertPremulValid(SKBitmap bitmap)
    {
        var bytes = new byte[bitmap.RowBytes * bitmap.Height];
        Marshal.Copy(bitmap.GetPixels(), bytes, 0, bytes.Length);
        for (var i = 0; i < bytes.Length; i += 4)
        {
            var a = bytes[i + 3];
            Assert.True(bytes[i] <= a && bytes[i + 1] <= a && bytes[i + 2] <= a,
                $"Premul 違反 at byte {i}");
        }
    }

    // ---------- 定義・汎用機構 ----------

    [Fact]
    public void Definition_Lut_NoRadius_AppliesToText_DefaultFullStrength()
    {
        var definition = EffectDefinitions.Get(EffectKind.Lut);
        Assert.NotNull(definition);
        Assert.False(definition!.UsesRadius);
        Assert.True(definition.AppliesToText);
        Assert.Equal(1, definition.DefaultAmount);
        Assert.Equal(49, (int)EffectKind.Lut); // 末尾に追加（既存 0〜48 は不変）。
        Assert.Equal(1, ClipEffect.ClampAmount(EffectKind.Lut, 5));
        Assert.Equal(0, ClipEffect.ClampAmount(EffectKind.Lut, -1));
        Assert.Equal(123, ClipEffect.ClampRadius(EffectKind.Lut, 123)); // Radius は使わないので素通し。
    }

    [Fact]
    public void Clone_CopiesLutPath()
    {
        var effect = new ClipEffect { Kind = EffectKind.Lut, Amount = 0.7, LutPath = "C:\\luts\\a.cube" };
        var clone = effect.Clone();
        Assert.Equal("C:\\luts\\a.cube", clone.LutPath);
        Assert.Equal(0.7, clone.Amount);
    }

    // ---------- JSON ----------

    [Fact]
    public void Json_RoundTrip_RestoresLutPath()
    {
        var project = new EditorProject();
        var track = project.AddTrack(TrackKind.Video, "ビデオ 1");
        var clip = new Clip { Kind = ClipKind.Video, DurationSeconds = 5 };
        clip.Effects.Add(new ClipEffect { Kind = EffectKind.Lut, Amount = 0.8, LutPath = "D:\\assets\\teal.cube" });
        track.Clips.Add(clip);

        var restored = ProjectStore.Deserialize(ProjectStore.Serialize(project));
        var effect = restored.Tracks[0].Clips[0].Effects[0];
        Assert.Equal(EffectKind.Lut, effect.Kind);
        Assert.Equal("D:\\assets\\teal.cube", effect.LutPath);
        Assert.Equal(0.8, effect.Amount, 9);
    }

    [Fact]
    public void Json_OldProjectWithoutLutPath_LoadsAsEmpty()
    {
        var project = new EditorProject();
        var track = project.AddTrack(TrackKind.Video, "ビデオ 1");
        var clip = new Clip { Kind = ClipKind.Video, DurationSeconds = 5 };
        clip.Effects.Add(new ClipEffect { Kind = EffectKind.Lut, Amount = 1, LutPath = "x.cube" });
        track.Clips.Add(clip);
        var json = ProjectStore.Serialize(project);
        // 旧版で保存された形（LutPath の行が無い JSON）を再現する。
        // LutPath は ClipEffect の末尾項目なので、行ごと消して直前行の末尾のカンマも取る。
        var lines = json.Split('\n').ToList();
        var index = lines.FindIndex(line => line.Contains("LutPath"));
        Assert.True(index > 0);
        lines.RemoveAt(index);
        lines[index - 1] = lines[index - 1].TrimEnd().TrimEnd(',');
        var oldJson = string.Join("\n", lines);
        Assert.DoesNotContain("LutPath", oldJson);

        var restored = ProjectStore.Deserialize(oldJson);
        Assert.Equal(string.Empty, restored.Tracks[0].Clips[0].Effects[0].LutPath);
    }

    // ---------- TimelineFrameRenderer 経由の配線 ----------

    private sealed class PatternFrameSource : IVideoFrameSource
    {
        public Task<byte[]?> GetFramePngAsync(string sourcePath, double timeSeconds, int maxWidth, CancellationToken ct = default)
        {
            var bitmap = new SKBitmap(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var canvas = new SKCanvas(bitmap))
                canvas.Clear(new SKColor(100, 150, 200));
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            var png = data.ToArray();
            bitmap.Dispose();
            return Task.FromResult<byte[]?>(png);
        }
    }

    private static async Task<SKBitmap> RenderAsync(ClipEffect effect, Action<Clip>? configure = null)
    {
        var project = new EditorProject { Width = Width, Height = Height, FrameRate = 30 };
        var clip = new Clip
        {
            Kind = ClipKind.Video,
            StartSeconds = 0,
            DurationSeconds = 10,
            SourcePath = "pattern-flat"
        };
        clip.Effects.Add(effect);
        configure?.Invoke(clip);
        project.Tracks.Add(new Track { Clips = { clip } });
        var renderer = new TimelineFrameRenderer(new PatternFrameSource());
        return await renderer.RenderFrameAsync(project, 0.5, Width, Height);
    }

    [Fact]
    public async Task Render_InvertLut_ChangesColor()
    {
        using var temp = new TempDirectory();
        var path = WriteCube(temp, "inv.cube", InvertCubeText(17));
        using var plain = await RenderAsync(new ClipEffect { Kind = EffectKind.Blur, Amount = 0 });
        using var lutted = await RenderAsync(new ClipEffect { Kind = EffectKind.Lut, Amount = 1, LutPath = path });
        Assert.Equal(new SKColor(100, 150, 200), plain.GetPixel(80, 45));
        var pixel = lutted.GetPixel(80, 45);
        Assert.InRange(pixel.Red, 154, 156);
        Assert.InRange(pixel.Green, 104, 106);
        Assert.InRange(pixel.Blue, 54, 56);
    }

    [Fact]
    public async Task Render_MissingLutFile_MatchesPlain()
    {
        using var temp = new TempDirectory();
        using var plain = await RenderAsync(new ClipEffect { Kind = EffectKind.Blur, Amount = 0 });
        using var lutted = await RenderAsync(new ClipEffect
        {
            Kind = EffectKind.Lut,
            Amount = 1,
            LutPath = temp.File("gone.cube")
        });
        Assert.Equal(plain.GetPixel(80, 45), lutted.GetPixel(80, 45));
    }

    [Fact]
    public async Task Render_KeyframeDrivesAmount()
    {
        using var temp = new TempDirectory();
        var path = WriteCube(temp, "inv.cube", InvertCubeText(17));
        // 固定値は 0（無変化）だが、キーフレームで 1 にしてあるため反転する。
        using var lutted = await RenderAsync(
            new ClipEffect { Kind = EffectKind.Lut, Amount = 0, LutPath = path },
            clip => clip.KeyframeTracks.Add(new KeyframeTrack
            {
                ParameterName = ClipEffect.KeyframeParameterName(EffectKind.Lut, isAmount: true),
                Keyframes = { new Keyframe(0, 1) }
            }));
        Assert.InRange(lutted.GetPixel(80, 45).Red, 154, 156);
    }

    [Fact]
    public async Task Render_TextClip_LutApplies()
    {
        using var temp = new TempDirectory();
        var path = WriteCube(temp, "inv.cube", InvertCubeText(17));
        var project = new EditorProject { Width = Width, Height = Height, FrameRate = 30 };
        var video = new Clip { Kind = ClipKind.Video, StartSeconds = 0, DurationSeconds = 10, SourcePath = "pattern-flat" };
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
        text.Effects.Add(new ClipEffect { Kind = EffectKind.Lut, Amount = 1, LutPath = path });
        project.Tracks.Add(new Track { Clips = { text } });
        var renderer = new TimelineFrameRenderer(new PatternFrameSource());
        using var lutted = await renderer.RenderFrameAsync(project, 0.5, Width, Height);
        // 白文字は反転で黒くなるため、文字の画素は下の色（100,150,200）より暗くなる。
        var found = false;
        for (var y = 0; y < Height && !found; y++)
            for (var x = 0; x < Width; x++)
            {
                var p = lutted.GetPixel(x, y);
                if (p.Red < 100 && p.Green < 150 && p.Blue < 200)
                {
                    found = true;
                    break;
                }
            }
        Assert.True(found, "LUTで反転した黒い文字の画素が見つかりませんでした。");
    }
}
