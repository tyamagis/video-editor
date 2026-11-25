// 音声フェード／ダッキングの単体テスト。
// 包絡計算（Core の AudioGainEnvelope）、トリガー区間解析（AudioActivityAnalyzer）、
// プレビュー側ゲイン適用（EnvelopeSampleProvider・TempoSampleProvider・
// ReversedAudioStream）、書き出しフィルタ文字列（AudioExportPlanner）、
// Clip の JSON 往復を対象とする。プレビューと書き出しが同じブレークポイント列を
// 読むことが一致の担保なので、包絡の再現性（BuildBreakpoints →
// EvaluateBreakpoints ＝ CombinedGainAt）を特に確認する。

using System.Text.Json.Nodes;
using Editor.Audio;
using Editor.Core;
using Editor.Media;
using Editor.Render;
using NAudio.Wave;
using Xunit;

namespace Editor.Tests;

/// <summary>音声フェード／ダッキングのテスト。</summary>
public sealed class AudioFadeDuckingTests
{
    private static Clip MakeClip(
        double start = 10, double duration = 4, double inPoint = 0,
        bool reversed = false, double speed = 1.0)
        => new()
        {
            Kind = ClipKind.Audio,
            SourcePath = "dummy.wav",
            StartSeconds = start,
            DurationSeconds = duration,
            InPointSeconds = inPoint,
            IsReversed = reversed,
            Speed = speed
        };

    private static Clip MakeTarget(double start = 10, double duration = 4)
    {
        var clip = MakeClip(start, duration);
        clip.DuckingRole = DuckingRole.Target;
        return clip;
    }

    // ---- フェード包絡 ----

    [Theory]
    [InlineData(0, 0.0)] // 開始直後は 0
    [InlineData(0.5, 0.5)]
    [InlineData(1.0, 1.0)] // フェードイン完了
    [InlineData(2.0, 1.0)]
    public void FadeGainAt_FadeIn_LinearRamp(double local, double expected)
    {
        Assert.Equal(expected, AudioGainEnvelope.FadeGainAt(1.0, 0, 4.0, local), 6);
    }

    [Theory]
    [InlineData(2.0, 1.0)]
    [InlineData(3.0, 1.0)] // フェードアウト開始
    [InlineData(3.5, 0.5)]
    [InlineData(4.0, 0.0)]
    public void FadeGainAt_FadeOut_LinearRamp(double local, double expected)
    {
        Assert.Equal(expected, AudioGainEnvelope.FadeGainAt(0, 1.0, 4.0, local), 6);
    }

    [Fact]
    public void FadeGainAt_NoFade_IsOne()
    {
        Assert.Equal(1.0, AudioGainEnvelope.FadeGainAt(0, 0, 4.0, 0));
        Assert.Equal(1.0, AudioGainEnvelope.FadeGainAt(0, 0, 4.0, 2.0));
    }

    [Fact]
    public void FadeGainAt_OverlappingInOut_Multiplies()
    {
        // 長さ2秒にイン2秒・アウト2秒：中央では in=0.5 × out=0.5。
        Assert.Equal(0.25, AudioGainEnvelope.FadeGainAt(2.0, 2.0, 2.0, 1.0), 6);
    }

    [Fact]
    public void FadeGainAt_FadeLongerThanClip_ClampsToDuration()
    {
        // フェード10秒 > クリップ2秒：末尾でも上がりきらない（local/duration）。
        Assert.Equal(0.5, AudioGainEnvelope.FadeGainAt(10.0, 0, 2.0, 1.0), 6);
    }

    [Theory]
    [InlineData(-1.0, 0.0)]
    [InlineData(100.0, 60.0)]
    [InlineData(double.NaN, 0.0)]
    [InlineData(1.5, 1.5)]
    public void ClampFadeSeconds_Range(double input, double expected)
    {
        Assert.Equal(expected, AudioGainEnvelope.ClampFadeSeconds(input));
    }

    [Fact]
    public void Clip_FadeSetters_Clamp()
    {
        var clip = new Clip();
        clip.AudioFadeInSeconds = 999;
        clip.AudioFadeOutSeconds = -3;
        Assert.Equal(60.0, clip.AudioFadeInSeconds);
        Assert.Equal(0.0, clip.AudioFadeOutSeconds);
    }

    [Fact]
    public void CombinedGain_FadeIsTimelineBased_EvenWhenReversedAndSped()
    {
        // 逆再生＋2倍速でも、フェードインはクリップ先頭（タイムライン）から
        // 1秒で上がりきる。素材の向き・速度には引きずられない。
        var clip = MakeClip(reversed: true, speed: 2.0);
        clip.AudioFadeInSeconds = 1.0;
        var none = Array.Empty<AudioSegment>();
        Assert.Equal(0.0, AudioGainEnvelope.CombinedGainAt(clip, 10.0, none), 6);
        Assert.Equal(0.5, AudioGainEnvelope.CombinedGainAt(clip, 10.5, none), 6);
        Assert.Equal(1.0, AudioGainEnvelope.CombinedGainAt(clip, 11.0, none), 6);
        // クリップ範囲外は 0。
        Assert.Equal(0.0, AudioGainEnvelope.CombinedGainAt(clip, 9.0, none));
        Assert.Equal(0.0, AudioGainEnvelope.CombinedGainAt(clip, 15.0, none));
    }

    // ---- ダッキング包絡 ----

    private static readonly List<AudioSegment> OneTrigger = new() { new AudioSegment(11, 13) };

    [Fact]
    public void DuckGainLinear_Default12Db()
    {
        Assert.Equal(0.2512, AudioGainEnvelope.DuckGainLinear(12.0), 4);
        Assert.Equal(1.0, AudioGainEnvelope.DuckGainLinear(0));
    }

    [Theory]
    // トリガー [11,13]、アタック0.1・リリース0.3・-12dB（0.2512）。
    [InlineData(10.5, 1.0)] // トリガー前
    [InlineData(11.0, 1.0)] // アタック開始
    [InlineData(11.05, 0.6256)] // アタック途中（線形の中点）
    [InlineData(11.1, 0.2512)] // 下がりきり
    [InlineData(12.0, 0.2512)] // 保持
    [InlineData(13.0, 0.2512)] // リリース開始
    [InlineData(13.15, 0.6256)] // リリース途中
    [InlineData(13.3, 1.0)] // 戻りきり
    [InlineData(14.0, 1.0)]
    public void DuckGainAt_Trapezoid(double t, double expected)
    {
        Assert.Equal(expected,
            AudioGainEnvelope.DuckGainAt(t, OneTrigger, 12.0, 0.1, 0.3), 4);
    }

    [Fact]
    public void DuckGainAt_NoTrigger_IsOne()
    {
        Assert.Equal(1.0, AudioGainEnvelope.DuckGainAt(12,
            Array.Empty<AudioSegment>(), 12.0, 0.1, 0.3));
    }

    [Fact]
    public void DuckGainAt_ZeroAmount_IsOne()
    {
        Assert.Equal(1.0, AudioGainEnvelope.DuckGainAt(12, OneTrigger, 0, 0.1, 0.3));
    }

    [Fact]
    public void DuckGainAt_ShortGapIntervals_DoNotPump()
    {
        // 間隔0.2秒（アタック＋リリース＝0.4より短い）の2区間はつながり、
        // 間の時刻でも下がったままになる（ポンピングしない）。
        var triggers = new List<AudioSegment> { new(11, 12), new(12.2, 13) };
        Assert.Equal(AudioGainEnvelope.DuckGainLinear(12.0),
            AudioGainEnvelope.DuckGainAt(12.1, triggers, 12.0, 0.1, 0.3), 4);
    }

    [Fact]
    public void DuckGainAt_LongGapIntervals_ReleaseAndReattack()
    {
        // 間隔2秒の2区間は独立し、間では完全に戻る。
        var triggers = new List<AudioSegment> { new(11, 12), new(14, 15) };
        Assert.Equal(1.0, AudioGainEnvelope.DuckGainAt(13.0, triggers, 12.0, 0.1, 0.3), 6);
    }

    [Fact]
    public void MergeIntervals_OverlappingAndGap()
    {
        var merged = AudioGainEnvelope.MergeIntervals(new[]
        {
            new AudioSegment(5, 6), new AudioSegment(1, 3),
            new AudioSegment(2.5, 4), new AudioSegment(10, 11)
        });
        Assert.Equal(new[] { new AudioSegment(1, 4), new AudioSegment(5, 6), new AudioSegment(10, 11) }, merged);

        var gapMerged = AudioGainEnvelope.MergeIntervals(new[]
        {
            new AudioSegment(1, 2), new AudioSegment(2.3, 3)
        }, gapSeconds: 0.5);
        Assert.Equal(new[] { new AudioSegment(1, 3) }, gapMerged);
    }

    [Fact]
    public void CombinedGain_FadeTimesDuck_Multiplies()
    {
        var clip = MakeTarget();
        clip.AudioFadeInSeconds = 2.0; // ローカル1秒（t=11）で 0.5
        // t=11 はトリガー開始直後（アタック前）なのでダッキングはまだ 1。
        Assert.Equal(0.5, AudioGainEnvelope.CombinedGainAt(clip, 11.0, OneTrigger), 4);
        // t=12 はフェード完了（1.0）× ダッキング保持（0.2512）。
        Assert.Equal(0.2512, AudioGainEnvelope.CombinedGainAt(clip, 12.0, OneTrigger), 4);
    }

    [Fact]
    public void CombinedGain_NonTargetClip_IgnoresDucking()
    {
        var clip = MakeClip(); // 役割なし
        Assert.Equal(1.0, AudioGainEnvelope.CombinedGainAt(clip, 12.0, OneTrigger));
    }

    [Fact]
    public void BuildBreakpoints_EvaluateReproducesCombinedGain()
    {
        // フェードのランプとダッキングのランプが重ならない配置では、
        // ブレークポイントの線形補間が CombinedGainAt を厳密に再現する。
        // （両ランプが重なる区間では積が2次になるため線形近似になるが、
        // プレビューと書き出しは同じブレークポイント列を読むため両者は一致する。）
        var clip = MakeTarget();
        clip.AudioFadeInSeconds = 0.5;
        clip.AudioFadeOutSeconds = 0.5;
        var triggers = new List<AudioSegment> { new(11, 12.5) };
        var breakpoints = AudioGainEnvelope.BuildBreakpoints(clip, triggers);

        Assert.Equal(0.0, breakpoints[0].TimeSeconds);
        Assert.Equal(clip.DurationSeconds, breakpoints[^1].TimeSeconds);
        for (var local = 0.0; local <= clip.DurationSeconds; local += 0.05)
        {
            var expected = AudioGainEnvelope.CombinedGainAt(clip, clip.StartSeconds + local, triggers);
            var actual = AudioGainEnvelope.EvaluateBreakpoints(breakpoints, local);
            Assert.Equal(expected, actual, 6);
        }
    }

    [Fact]
    public void HasEnvelope_Cases()
    {
        var none = Array.Empty<AudioSegment>();
        Assert.False(AudioGainEnvelope.HasEnvelope(MakeClip(), none));
        var faded = MakeClip();
        faded.AudioFadeOutSeconds = 0.5;
        Assert.True(AudioGainEnvelope.HasEnvelope(faded, none));
        Assert.False(AudioGainEnvelope.HasEnvelope(MakeTarget(), none)); // トリガー無し
        Assert.True(AudioGainEnvelope.HasEnvelope(MakeTarget(), OneTrigger));
        var far = new List<AudioSegment> { new(100, 101) }; // 対象と重ならない
        Assert.False(AudioGainEnvelope.HasEnvelope(MakeTarget(), far));
    }

    [Fact]
    public void Clip_DuckingDefaults_AndClamps()
    {
        var clip = new Clip();
        Assert.Equal(DuckingRole.None, clip.DuckingRole);
        Assert.Equal(12.0, clip.DuckingAmountDb);
        Assert.Equal(0.10, clip.DuckingAttackSeconds);
        Assert.Equal(0.30, clip.DuckingReleaseSeconds);
        Assert.Equal(-40.0, clip.DuckingThresholdDb);

        clip.DuckingAmountDb = 99;
        clip.DuckingAttackSeconds = 0;
        clip.DuckingReleaseSeconds = 99;
        clip.DuckingThresholdDb = 5;
        Assert.Equal(30.0, clip.DuckingAmountDb);
        Assert.Equal(AudioGainEnvelope.MinRampSeconds, clip.DuckingAttackSeconds);
        Assert.Equal(5.0, clip.DuckingReleaseSeconds);
        Assert.Equal(0.0, clip.DuckingThresholdDb);
    }

    // ---- トリガー区間の写像・収集 ----

    [Fact]
    public void MapTriggerSegments_Forward_MapsWithInPointAndSpeed()
    {
        // 素材相対 [1,2]、イン点10・速度2：素材 [11,12] → タイムラインは
        // 開始2 + (11-10)/2 = 2.5 〜 3.0。
        var trigger = MakeClip(start: 2, duration: 4, inPoint: 10, speed: 2.0);
        var mapped = AudioGainEnvelope.MapTriggerSegmentsToTimeline(
            trigger, new[] { new AudioSegment(1, 2) });
        Assert.Equal(new[] { new AudioSegment(2.5, 3.0) }, mapped);
    }

    [Fact]
    public void MapTriggerSegments_Reversed_FlipsInterval()
    {
        // 逆再生：素材相対 [0,1]（素材 [10,11]）はタイムラインの末尾側
        // [5,6]（開始2・長さ4）へ写る。
        var trigger = MakeClip(start: 2, duration: 4, inPoint: 10, reversed: true);
        var mapped = AudioGainEnvelope.MapTriggerSegmentsToTimeline(
            trigger, new[] { new AudioSegment(0, 1) });
        Assert.Single(mapped);
        Assert.Equal(5.0, mapped[0].StartSeconds, 6);
        Assert.Equal(6.0, mapped[0].EndSeconds, 6);
    }

    [Fact]
    public void CollectTriggerIntervals_GathersAllTriggers_ExcludesSelfAndNonTriggers()
    {
        var project = new EditorProject();
        var track = project.AddTrack(TrackKind.Audio, "A1");
        var target = MakeTarget(start: 0, duration: 10);
        var triggerA = MakeClip(start: 0, duration: 10);
        triggerA.DuckingRole = DuckingRole.Trigger;
        var plain = MakeClip(start: 0, duration: 10);
        track.Clips.Add(target);
        track.Clips.Add(triggerA);
        track.Clips.Add(plain);

        var intervals = AudioGainEnvelope.CollectTriggerIntervals(project, target,
            clip => clip.Id == triggerA.Id
                ? new[] { new AudioSegment(2, 3) }
                : new[] { new AudioSegment(5, 6) });

        // トリガーAの区間だけ（対象自身・役割なしは数えない）。
        Assert.Equal(new[] { new AudioSegment(2, 3) }, intervals);
    }

    // ---- トリガー区間解析（AudioActivityAnalyzer） ----

    [Fact]
    public void FindActiveSegments_RunsGapsAndShortRuns()
    {
        // 10ms 窓 × 200（2秒）。0.2〜0.5 が鳴り、0.8 に単発（10ms）、1.2〜1.5 が鳴る。
        // 単発は前後の区間から MaxGapSeconds 以上離れているため橋渡しされず、
        // MinActiveSeconds 未満で落ちる。
        var rms = new double[200];
        for (var i = 20; i < 50; i++) rms[i] = 0.5;
        rms[80] = 0.5;
        for (var i = 120; i < 150; i++) rms[i] = 0.5;

        var segments = AudioActivityAnalyzer.FindActiveSegments(rms, 2.0, 0.01);

        Assert.Equal(new[] { new AudioSegment(0.2, 0.5), new AudioSegment(1.2, 1.5) }, segments);
    }

    [Fact]
    public void FindActiveSegments_ShortGapIsBridged()
    {
        // 0.10 秒の途切れ（MaxGapSeconds 0.15 以下）はつながる。
        var rms = new double[100];
        for (var i = 10; i < 40; i++) rms[i] = 0.5;
        for (var i = 50; i < 80; i++) rms[i] = 0.5;

        var segments = AudioActivityAnalyzer.FindActiveSegments(rms, 1.0, 0.01);

        Assert.Equal(new[] { new AudioSegment(0.1, 0.8) }, segments);
    }

    private static void WriteWav(string path, float[] mono, int sampleRate = 8000)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        var frames = mono.Length;
        writer.Write("RIFF".ToCharArray());
        writer.Write(36 + frames * 2);
        writer.Write("WAVE".ToCharArray());
        writer.Write("fmt ".ToCharArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data".ToCharArray());
        writer.Write(frames * 2);
        foreach (var s in mono)
            writer.Write((short)Math.Round(Math.Clamp(s, -1f, 1f) * short.MaxValue));
    }

    [Fact]
    public async Task GetActiveSegmentsAsync_LoudThenSilent_ReturnsLoudPart()
    {
        using var temp = new TempDirectory();
        var path = temp.File("voice.wav");
        var samples = new float[8000]; // 前半0.5秒は 0.5、後半は無音。
        for (var i = 0; i < 4000; i++) samples[i] = 0.5f;
        WriteWav(path, samples);
        var analyzer = new AudioActivityAnalyzer();

        var segments = await analyzer.GetActiveSegmentsAsync(path, 0, 1.0, -40);

        Assert.Single(segments);
        Assert.Equal(0.0, segments[0].StartSeconds, 2);
        Assert.Equal(0.5, segments[0].EndSeconds, 2);
    }

    [Fact]
    public async Task GetActiveSegmentsAsync_MissingFile_ReturnsEmpty()
    {
        var analyzer = new AudioActivityAnalyzer();
        var segments = await analyzer.GetActiveSegmentsAsync("no-such-file.wav", 0, 1.0, -40);
        Assert.Empty(segments);
    }

    // ---- プレビュー側ゲイン適用 ----

    /// <summary>定数サンプルを返すだけの ISampleProvider スタブ。</summary>
    private sealed class ConstantSampleProvider : ISampleProvider
    {
        private readonly float _value;
        public ConstantSampleProvider(float value, int sampleRate = 100)
        {
            _value = value;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);
        }
        public WaveFormat WaveFormat { get; }
        public int Read(float[] buffer, int offset, int count)
        {
            Array.Fill(buffer, _value, offset, count);
            return count;
        }
    }

    [Fact]
    public void EnvelopeSampleProvider_AppliesFadeOverTime()
    {
        // サンプルレート100Hz、フェードイン1秒：フレーム0で0、50で0.5、100以降1。
        var clip = MakeClip(start: 0, duration: 4);
        clip.AudioFadeInSeconds = 1.0;
        var breakpoints = AudioGainEnvelope.BuildBreakpoints(clip, Array.Empty<AudioSegment>());
        var provider = new EnvelopeSampleProvider(
            new ConstantSampleProvider(1.0f), breakpoints, startLocalSeconds: 0, gain: 2.0);
        var buffer = new float[200];
        var read = provider.Read(buffer, 0, buffer.Length);

        Assert.Equal(200, read);
        Assert.Equal(0.0f, buffer[0]);
        Assert.Equal(1.0f, buffer[50], 3); // 0.5（包絡）× 2.0（静的ゲイン）
        Assert.Equal(2.0f, buffer[150], 3);
    }

    [Fact]
    public void TempoSampleProvider_AppliesEnvelopeInOutputTime()
    {
        // 2倍速でも包絡は出力フレーム位置（タイムライン時刻）で効く：
        // 出力50フレーム目（0.5秒）でフェードイン1秒の半分。
        var clip = MakeClip(start: 0, duration: 4, speed: 2.0);
        clip.AudioFadeInSeconds = 1.0;
        var breakpoints = AudioGainEnvelope.BuildBreakpoints(clip, Array.Empty<AudioSegment>());
        var provider = new TempoSampleProvider(
            new ConstantSampleProvider(1.0f), speed: 2.0, gain: 1.0,
            envelope: breakpoints, envelopeStartLocalSeconds: 0);
        var buffer = new float[200];
        provider.Read(buffer, 0, buffer.Length);

        Assert.Equal(0.0f, buffer[0], 3);
        Assert.Equal(0.5f, buffer[50], 2);
        Assert.Equal(1.0f, buffer[150], 2);
    }

    [Fact]
    public void ReversedAudioStream_AppliesEnvelopeInOutputTime()
    {
        // 逆再生でもフェードインは「聞こえる先頭」から効く。素材末尾の
        // サンプルが最初に出るが、ゲインは出力位置0で0になる。
        var clip = MakeClip(start: 0, duration: 4, reversed: true);
        clip.AudioFadeInSeconds = 1.0;
        var breakpoints = AudioGainEnvelope.BuildBreakpoints(clip, Array.Empty<AudioSegment>());
        var format = WaveFormat.CreateIeeeFloatWaveFormat(100, 1);
        var samples = new float[400];
        Array.Fill(samples, 1.0f);
        var stream = new ReversedAudioStream(format, samples, startFrame: 399, step: 1.0,
            envelope: breakpoints, envelopeStartLocalSeconds: 0);
        var bytes = new byte[400 * 4];
        var read = stream.Read(bytes, 0, bytes.Length);

        Assert.Equal(400 * 4, read);
        Assert.Equal(0.0f, BitConverter.ToSingle(bytes, 0), 3);
        Assert.Equal(0.5f, BitConverter.ToSingle(bytes, 50 * 4), 2);
        Assert.Equal(1.0f, BitConverter.ToSingle(bytes, 150 * 4), 2);
    }

    // ---- 書き出しフィルタ ----

    [Fact]
    public void EnvelopeVolumeFilter_NoneOrAllOne_IsEmpty()
    {
        Assert.Equal(string.Empty, AudioExportPlanner.EnvelopeVolumeFilter(null));
        var ones = new List<GainBreakpoint> { new(0, 1.0), new(4, 1.0) };
        Assert.Equal(string.Empty, AudioExportPlanner.EnvelopeVolumeFilter(ones));
    }

    [Fact]
    public void EnvelopeVolumeFilter_FadeIn_BuildsLinearExpression()
    {
        var clip = MakeClip(start: 0, duration: 4);
        clip.AudioFadeInSeconds = 1.0;
        var breakpoints = AudioGainEnvelope.BuildBreakpoints(clip, Array.Empty<AudioSegment>());

        var filter = AudioExportPlanner.EnvelopeVolumeFilter(breakpoints);

        Assert.StartsWith("volume='", filter);
        Assert.Contains(":eval=frame,", filter);
        Assert.Contains("if(lt(t,1.0000)", filter); // 1秒で上がりきる分岐
        Assert.Contains("0.0000+(1.0000-0.0000)*(t-0.0000)/1.0000", filter); // 線形補間
    }

    [Fact]
    public void EnvelopeVolumeFilter_OffsetShiftsBreakpoints()
    {
        // 書き出し範囲がクリップ途中から始まる場合、ストリーム時刻0が
        // クリップ相対1秒に当たるよう、ブレークポイントが -1 秒ずれる。
        var clip = MakeClip(start: 0, duration: 4);
        clip.AudioFadeInSeconds = 2.0;
        var breakpoints = AudioGainEnvelope.BuildBreakpoints(clip, Array.Empty<AudioSegment>());

        var filter = AudioExportPlanner.EnvelopeVolumeFilter(breakpoints, envelopeStartLocalSeconds: 1.0);

        Assert.Contains("if(lt(t,1.0000)", filter); // 相対2秒 - 1秒 = ストリーム1秒
    }

    [Fact]
    public void BuildClipFilter_EnvelopeGoesAfterAsetptsBeforeAdelay()
    {
        var clip = MakeClip(start: 0, duration: 4, reversed: true, speed: 2.0);
        clip.AudioFadeInSeconds = 1.0;
        var breakpoints = AudioGainEnvelope.BuildBreakpoints(clip, Array.Empty<AudioSegment>());

        var filter = AudioExportPlanner.BuildClipFilter(
            1, clip, 0, 8.0, 500, "a1", 1.0, breakpoints, 0);

        var atrim = filter.IndexOf("atrim=", StringComparison.Ordinal);
        var reverse = filter.IndexOf("areverse", StringComparison.Ordinal);
        var tempo = filter.IndexOf("atempo=", StringComparison.Ordinal);
        var asetpts = filter.IndexOf("asetpts=", StringComparison.Ordinal);
        var volume = filter.IndexOf("volume='", StringComparison.Ordinal);
        var adelay = filter.IndexOf("adelay=", StringComparison.Ordinal);
        Assert.True(0 <= atrim && atrim < reverse && reverse < tempo && tempo < asetpts
            && asetpts < volume && volume < adelay, filter);
    }

    [Fact]
    public void BuildClipFilter_NoEnvelope_UnchangedShape()
    {
        var clip = MakeClip(start: 0, duration: 4);
        var filter = AudioExportPlanner.BuildClipFilter(1, clip, 0, 4.0, 0, "a1");
        Assert.Equal("[1:a]atrim=start=0.000:end=4.000,asetpts=PTS-STARTPTS,adelay=0:all=1[a1]", filter);
    }

    /// <summary>固定の鳴っている区間を返す提供口スタブ。</summary>
    private sealed class StubActivityProvider : IAudioActivityProvider
    {
        private readonly IReadOnlyList<AudioSegment> _segments;
        public StubActivityProvider(IReadOnlyList<AudioSegment> segments) => _segments = segments;
        public Task<IReadOnlyList<AudioSegment>> GetActiveSegmentsAsync(
            string sourcePath, double inPointSeconds, double durationSeconds, double thresholdDb,
            CancellationToken ct = default)
            => Task.FromResult(_segments);
    }

    [Fact]
    public async Task BuildAudioPlanAsync_DuckingTarget_AddsEnvelopeFilter()
    {
        using var temp = new TempDirectory();
        var path = temp.File("tone.wav");
        WriteWav(path, Enumerable.Repeat(0.5f, 8000).ToArray());
        var project = new EditorProject();
        var track = project.AddTrack(TrackKind.Audio, "A1");
        var bgm = MakeTarget(start: 0, duration: 1.0);
        bgm.SourcePath = path;
        var narration = MakeClip(start: 0, duration: 1.0);
        narration.SourcePath = path;
        narration.DuckingRole = DuckingRole.Trigger;
        track.Clips.Add(bgm);
        track.Clips.Add(narration);
        // トリガーは素材相対 [0.2, 0.6] で鳴っている。
        var planner = new AudioExportPlanner(new MediaProbe(new FfmpegSettings()), null,
            new StubActivityProvider(new[] { new AudioSegment(0.2, 0.6) }));

        var (inputs, _) = await planner.BuildAudioPlanAsync(project, 0, 1.0, CancellationToken.None);

        var bgmFilter = inputs.Single(i => i.Path == path && i.Filter.Contains("volume='"));
        Assert.Contains(":eval=frame", bgmFilter.Filter);
        // ダッキングの保持ゲイン（-12dB ≒ 0.2512）が式に現れる。
        Assert.Contains("0.2512", bgmFilter.Filter);
    }

    [Fact]
    public async Task BuildAudioPlanAsync_FadeOnly_AddsEnvelopeFilter()
    {
        using var temp = new TempDirectory();
        var path = temp.File("tone.wav");
        WriteWav(path, Enumerable.Repeat(0.5f, 8000).ToArray());
        var project = new EditorProject();
        var track = project.AddTrack(TrackKind.Audio, "A1");
        var clip = MakeClip(start: 0, duration: 1.0);
        clip.SourcePath = path;
        clip.AudioFadeInSeconds = 0.5;
        track.Clips.Add(clip);
        var planner = new AudioExportPlanner(new MediaProbe(new FfmpegSettings()));

        var (inputs, _) = await planner.BuildAudioPlanAsync(project, 0, 1.0, CancellationToken.None);

        Assert.Single(inputs);
        Assert.Contains("volume='", inputs[0].Filter);
        Assert.Contains(":eval=frame", inputs[0].Filter);
    }

    [Fact]
    public async Task BuildAudioPlanAsync_NoFadeNoDucking_NoEnvelopeFilter()
    {
        using var temp = new TempDirectory();
        var path = temp.File("tone.wav");
        WriteWav(path, Enumerable.Repeat(0.5f, 8000).ToArray());
        var project = new EditorProject();
        var track = project.AddTrack(TrackKind.Audio, "A1");
        var clip = MakeClip(start: 0, duration: 1.0);
        clip.SourcePath = path;
        track.Clips.Add(clip);
        var planner = new AudioExportPlanner(new MediaProbe(new FfmpegSettings()), null,
            new StubActivityProvider(new[] { new AudioSegment(0, 1) }));

        var (inputs, _) = await planner.BuildAudioPlanAsync(project, 0, 1.0, CancellationToken.None);

        Assert.Single(inputs);
        Assert.DoesNotContain("volume=", inputs[0].Filter);
    }

    // ---- JSON 往復 ----

    [Fact]
    public void ProjectStore_RoundTrip_PreservesFadeAndDucking()
    {
        var project = new EditorProject();
        var track = project.AddTrack(TrackKind.Audio, "A1");
        var clip = MakeTarget();
        clip.AudioFadeInSeconds = 0.5;
        clip.AudioFadeOutSeconds = 1.5;
        clip.DuckingAmountDb = 18;
        clip.DuckingAttackSeconds = 0.2;
        clip.DuckingReleaseSeconds = 0.8;
        clip.DuckingThresholdDb = -30;
        track.Clips.Add(clip);

        var loaded = ProjectStore.Deserialize(ProjectStore.Serialize(project));

        var c = loaded.Tracks[0].Clips[0];
        Assert.Equal(0.5, c.AudioFadeInSeconds);
        Assert.Equal(1.5, c.AudioFadeOutSeconds);
        Assert.Equal(DuckingRole.Target, c.DuckingRole);
        Assert.Equal(18.0, c.DuckingAmountDb);
        Assert.Equal(0.2, c.DuckingAttackSeconds);
        Assert.Equal(0.8, c.DuckingReleaseSeconds);
        Assert.Equal(-30.0, c.DuckingThresholdDb);
    }

    [Fact]
    public void ProjectStore_OldJsonWithoutFadeDucking_OpensAsNone()
    {
        var project = new EditorProject();
        var track = project.AddTrack(TrackKind.Audio, "A1");
        var clip = MakeTarget();
        clip.AudioFadeInSeconds = 0.5;
        track.Clips.Add(clip);

        var node = JsonNode.Parse(ProjectStore.Serialize(project))!.AsObject();
        var clipNode = node["Tracks"]![0]!["Clips"]![0]!.AsObject();
        foreach (var name in new[]
                 {
                     "AudioFadeInSeconds", "AudioFadeOutSeconds", "DuckingRole",
                     "DuckingAmountDb", "DuckingAttackSeconds", "DuckingReleaseSeconds", "DuckingThresholdDb"
                 })
            Assert.True(clipNode.Remove(name), name);

        var loaded = ProjectStore.LoadFromJson(node.ToJsonString());

        var c = loaded.Tracks[0].Clips[0];
        Assert.Equal(0.0, c.AudioFadeInSeconds);
        Assert.Equal(0.0, c.AudioFadeOutSeconds);
        Assert.Equal(DuckingRole.None, c.DuckingRole);
        Assert.Equal(12.0, c.DuckingAmountDb);
        Assert.Equal(0.10, c.DuckingAttackSeconds);
        Assert.Equal(0.30, c.DuckingReleaseSeconds);
        Assert.Equal(-40.0, c.DuckingThresholdDb);
    }

    [Fact]
    public void Clip_Clone_PreservesFadeAndDucking()
    {
        var clip = MakeTarget();
        clip.AudioFadeInSeconds = 0.5;
        clip.DuckingAmountDb = 20;
        var copy = clip.Clone();
        Assert.Equal(0.5, copy.AudioFadeInSeconds);
        Assert.Equal(DuckingRole.Target, copy.DuckingRole);
        Assert.Equal(20.0, copy.DuckingAmountDb);
    }
}
