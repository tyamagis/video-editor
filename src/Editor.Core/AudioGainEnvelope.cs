// 音声フェードとダッキングのゲイン包絡計算（設計プラン 5章）。
// ノーマライズ（AudioNormalizer）と同じ思想で、計算は Core の純粋関数に置き、
// プレビュー（NAudio のサンプルプロバイダ）と書き出し（ffmpeg の volume フィルタ）が
// 同じブレークポイント列を読む。両経路が同じ包絡を使うため、プレビューと書き出しで
// フェード／ダッキングの効き方が一致する。
//
// フェード長の解釈：タイムライン秒。クリップのタイムライン上の長さ
// （DurationSeconds）に対して先頭からフェードイン、末尾からフェードアウトする。
// 素材秒にすると速度変更で聞こえる長さが変わってしまう（2倍速で素材2秒分＝
// タイムライン1秒）ため、「タイムラインで何秒かけて聞こえ始めるか」という
// 編集上の意味に合わせてタイムライン秒を採用する。逆再生でも向きは
// タイムライン基準（先頭がフェードイン）で、素材の向きには引きずられない。
//
// ダッキングの適用順序（書き出しフィルタ）：包絡は線形ゲインのため、
// ノーマライズ（固定ゲイン）と掛け算で合成でき、順序は結果に影響しない。
// ただし包絡は「タイムライン時刻の関数」なので、atrim / areverse / atempo で
// 時間軸がタイムライン方向にそろった後（asetpts の直後、adelay の前）に
// 1回だけ掛ける。atrim の前に掛けると逆再生・速度変更で時刻対応が壊れる。

namespace Editor.Core;

/// <summary>ダッキングにおけるクリップの役割。</summary>
public enum DuckingRole
{
    /// <summary>役割なし（既定。旧プロジェクトは全クリップこれとして開ける）。</summary>
    None,

    /// <summary>ダッキング対象（BGMなど。トリガーが鳴っている区間で音量が下がる）。</summary>
    Target,

    /// <summary>ダッキングのトリガー（ナレーションなど。無音でない区間が対象を下げる）。</summary>
    Trigger
}

/// <summary>音声の区間1つ（秒）。文脈により素材相対・タイムライン絶対のどちらかで使う。</summary>
public sealed record AudioSegment(double StartSeconds, double EndSeconds)
{
    public double DurationSeconds => EndSeconds - StartSeconds;
}

/// <summary>ゲイン包絡のブレークポイント1点。時刻はクリップ先頭からの相対秒（タイムライン秒）。</summary>
public sealed record GainBreakpoint(double TimeSeconds, double Gain);

/// <summary>フェードとダッキングのゲイン包絡計算。状態を持たない純粋関数。</summary>
public static class AudioGainEnvelope
{
    // --- フェード ---

    /// <summary>フェード長の上限（秒）。クリップ長を超える指定は評価時にクリップ長へ丸める。</summary>
    public const double MaxFadeSeconds = 60.0;

    /// <summary>フェード長を 0〜60 秒に丸める（NaN は 0＝フェード無し）。</summary>
    public static double ClampFadeSeconds(double value)
        => double.IsNaN(value) ? 0 : Math.Clamp(value, 0, MaxFadeSeconds);

    /// <summary>
    /// クリップ相対時刻（タイムライン秒）におけるフェードのゲイン（線形、0〜1）。
    /// フェードインは [0, fadeIn] で 0→1、フェードアウトは [duration-fadeOut, duration] で 1→0 の線形。
    /// 両者が重なる場合は小さい方（積ではなく min ではなく、両ランプの積でもなく、
    /// インとアウトを別々に計算して掛ける：重なり区間では in×out になる）。
    /// </summary>
    public static double FadeGainAt(double fadeInSeconds, double fadeOutSeconds, double durationSeconds, double localSeconds)
    {
        if (durationSeconds <= 0)
            return 1.0;
        var local = Math.Clamp(localSeconds, 0, durationSeconds);
        var gain = 1.0;
        var fadeIn = Math.Min(ClampFadeSeconds(fadeInSeconds), durationSeconds);
        if (fadeIn > 0 && local < fadeIn)
            gain *= local / fadeIn;
        var fadeOut = Math.Min(ClampFadeSeconds(fadeOutSeconds), durationSeconds);
        if (fadeOut > 0 && local > durationSeconds - fadeOut)
            gain *= (durationSeconds - local) / fadeOut;
        return Math.Clamp(gain, 0, 1);
    }

    // --- ダッキング設定の範囲と既定値 ---

    /// <summary>
    /// ダッキングで下げる量の既定値（dB）。-12 dB は BGM がナレーションの
    /// 下に沈みつつ消えはしない、放送・配信のダッキングで一般的な範囲
    /// （-10〜-15 dB）の中央。線形では約 0.251 倍。
    /// </summary>
    public const double DefaultDuckAmountDb = 12.0;

    /// <summary>下げる量の範囲（dB）。0 で無効、30 でほぼ聞こえなくなる（約 0.032 倍）。</summary>
    public const double MaxDuckAmountDb = 30.0;

    /// <summary>
    /// アタックの既定値（秒）。0.10 秒は、言葉の頭で BGM がスッと下がりきる速さ。
    /// これより速いとクリック感・ポンピングが出やすく、遅いと語頭が BGM に埋もれる。
    /// </summary>
    public const double DefaultAttackSeconds = 0.10;

    /// <summary>
    /// リリースの既定値（秒）。0.30 秒は、言葉の切れ目ごとに BGM が上下する
    /// ポンピングを抑えつつ、フレーズが終われば自然に戻る速さ。
    /// アタックより遅くするのがダッキングの定石（下げるのは速く、戻すのはゆっくり）。
    /// </summary>
    public const double DefaultReleaseSeconds = 0.30;

    /// <summary>アタック／リリースの指定範囲（秒）。</summary>
    public const double MinRampSeconds = 0.005;
    public const double MaxAttackSeconds = 2.0;
    public const double MaxReleaseSeconds = 5.0;

    /// <summary>
    /// トリガー検出のしきい値の既定値（dBFS）。無音検出の既定（-40 dB）と同じ。
    /// これ以上の RMS を「トリガーが鳴っている」とみなす。
    /// </summary>
    public const double DefaultThresholdDb = -40.0;
    public const double MinThresholdDb = -90.0;
    public const double MaxThresholdDb = 0.0;

    public static double ClampDuckAmountDb(double value)
        => double.IsNaN(value) ? DefaultDuckAmountDb : Math.Clamp(value, 0, MaxDuckAmountDb);

    public static double ClampAttackSeconds(double value)
        => double.IsNaN(value) ? DefaultAttackSeconds : Math.Clamp(value, MinRampSeconds, MaxAttackSeconds);

    public static double ClampReleaseSeconds(double value)
        => double.IsNaN(value) ? DefaultReleaseSeconds : Math.Clamp(value, MinRampSeconds, MaxReleaseSeconds);

    public static double ClampThresholdDb(double value)
        => double.IsNaN(value) ? DefaultThresholdDb : Math.Clamp(value, MinThresholdDb, MaxThresholdDb);

    /// <summary>下げる量（dB）を線形倍率にする（12 dB → 約 0.251）。</summary>
    public static double DuckGainLinear(double amountDb)
        => Math.Pow(10, -ClampDuckAmountDb(amountDb) / 20.0);

    // --- ダッキング包絡 ---

    /// <summary>
    /// 区間列を重なり・近接でマージする。gapSeconds 以下の間隔で離れた区間も
    /// 1つにつなぐ（ダッキングではアタック＋リリースより短い間隔でトリガーが
    /// 途切れても包絡を上下させないために使う）。結果は開始順。
    /// </summary>
    public static List<AudioSegment> MergeIntervals(IEnumerable<AudioSegment> intervals, double gapSeconds = 0)
    {
        var sorted = intervals
            .Where(s => s.EndSeconds > s.StartSeconds)
            .OrderBy(s => s.StartSeconds)
            .ToList();
        var merged = new List<AudioSegment>();
        foreach (var seg in sorted)
        {
            if (merged.Count > 0 && seg.StartSeconds <= merged[^1].EndSeconds + Math.Max(0, gapSeconds))
            {
                var last = merged[^1];
                merged[^1] = new AudioSegment(last.StartSeconds, Math.Max(last.EndSeconds, seg.EndSeconds));
            }
            else
            {
                merged.Add(seg);
            }
        }
        return merged;
    }

    /// <summary>
    /// タイムライン時刻におけるダッキングのゲイン（線形、duck〜1）。
    /// トリガー区間 [s, e] ごとに台形：s からアタック秒で 1→duck、e まで duck、
    /// e からリリース秒で duck→1。複数区間が効く時刻では最も下がる値（min）を取る。
    /// アタック＋リリースより短い間隔の区間は先にマージされ、連続した台形になる。
    /// </summary>
    public static double DuckGainAt(
        double timelineSeconds, IReadOnlyList<AudioSegment> triggerIntervals,
        double amountDb, double attackSeconds, double releaseSeconds)
    {
        if (triggerIntervals.Count == 0)
            return 1.0;
        var duck = DuckGainLinear(amountDb);
        if (duck >= 1.0)
            return 1.0;
        var attack = ClampAttackSeconds(attackSeconds);
        var release = ClampReleaseSeconds(releaseSeconds);
        var merged = MergeIntervals(triggerIntervals, attack + release);
        var gain = 1.0;
        foreach (var seg in merged)
        {
            double factor;
            if (timelineSeconds < seg.StartSeconds || timelineSeconds > seg.EndSeconds + release)
                continue;
            else if (timelineSeconds < seg.StartSeconds + attack)
                factor = 1.0 - (1.0 - duck) * ((timelineSeconds - seg.StartSeconds) / attack);
            else if (timelineSeconds <= seg.EndSeconds)
                factor = duck;
            else
                factor = duck + (1.0 - duck) * ((timelineSeconds - seg.EndSeconds) / release);
            if (factor < gain)
                gain = factor;
        }
        return Math.Clamp(gain, duck, 1.0);
    }

    // --- 合成包絡（フェード × ダッキング） ---

    /// <summary>
    /// タイムライン時刻における合成ゲイン（フェード × ダッキング、線形）。
    /// クリップ範囲外は 0。トリガー区間はタイムライン絶対秒で渡す。
    /// ダッキング対象でないクリップではダッキングは掛からない（フェードのみ）。
    /// </summary>
    public static double CombinedGainAt(Clip clip, double timelineSeconds, IReadOnlyList<AudioSegment> triggerIntervals)
    {
        if (timelineSeconds < clip.StartSeconds || timelineSeconds > clip.EndSeconds)
            return 0;
        var local = timelineSeconds - clip.StartSeconds;
        var gain = FadeGainAt(clip.AudioFadeInSeconds, clip.AudioFadeOutSeconds, clip.DurationSeconds, local);
        if (clip.DuckingRole == DuckingRole.Target && triggerIntervals.Count > 0)
            gain *= DuckGainAt(timelineSeconds, triggerIntervals,
                clip.DuckingAmountDb, clip.DuckingAttackSeconds, clip.DuckingReleaseSeconds);
        return gain;
    }

    /// <summary>このクリップに包絡（フェード、または効くダッキング）があるか。</summary>
    public static bool HasEnvelope(Clip clip, IReadOnlyList<AudioSegment> triggerIntervals)
    {
        if (clip.AudioFadeInSeconds > 0 || clip.AudioFadeOutSeconds > 0)
            return true;
        return clip.DuckingRole == DuckingRole.Target
            && ClampDuckAmountDb(clip.DuckingAmountDb) > 0
            && triggerIntervals.Any(s => s.EndSeconds > clip.StartSeconds && s.StartSeconds < clip.EndSeconds + ClampReleaseSeconds(clip.DuckingReleaseSeconds));
    }

    /// <summary>
    /// 合成包絡のブレークポイント列を作る（時刻はクリップ相対・タイムライン秒、
    /// [0, Duration] を覆う）。臨界点（フェードの両端、ダッキングの台形の角）で
    /// CombinedGainAt を評価した点列で、EvaluateBreakpoints の線形補間が
    /// CombinedGainAt を再現する（台形同士の重なりはマージで解消済み）。
    /// 例外はフェードとダッキングのランプが同時に効く区間で、そこでは積が
    /// 2次曲線になるため線形補間は近似になる。ただしプレビューと書き出しは
    /// どちらもこのブレークポイント列だけを読むため、両経路の結果は一致する。
    /// </summary>
    public static List<GainBreakpoint> BuildBreakpoints(Clip clip, IReadOnlyList<AudioSegment> triggerIntervals)
    {
        var duration = Math.Max(0, clip.DurationSeconds);
        var times = new SortedSet<double> { 0, duration };
        void AddLocal(double local)
        {
            if (local > 0 && local < duration)
                times.Add(local);
        }
        var fadeIn = Math.Min(ClampFadeSeconds(clip.AudioFadeInSeconds), duration);
        var fadeOut = Math.Min(ClampFadeSeconds(clip.AudioFadeOutSeconds), duration);
        AddLocal(fadeIn);
        AddLocal(duration - fadeOut);

        if (clip.DuckingRole == DuckingRole.Target && triggerIntervals.Count > 0)
        {
            var attack = ClampAttackSeconds(clip.DuckingAttackSeconds);
            var release = ClampReleaseSeconds(clip.DuckingReleaseSeconds);
            foreach (var seg in MergeIntervals(triggerIntervals, attack + release))
            {
                AddLocal(seg.StartSeconds - clip.StartSeconds);
                AddLocal(seg.StartSeconds + attack - clip.StartSeconds);
                AddLocal(seg.EndSeconds - clip.StartSeconds);
                AddLocal(seg.EndSeconds + release - clip.StartSeconds);
            }
        }

        return times
            .Select(t => new GainBreakpoint(t, CombinedGainAt(clip, clip.StartSeconds + t, triggerIntervals)))
            .ToList();
    }

    /// <summary>
    /// ブレークポイント列を ffmpeg の volume フィルタ（eval=frame）にする（末尾カンマなし）。
    /// 式はブレークポイント間の線形補間（EvaluateBreakpoints と同じ）をネストした if で表す。
    /// t はストリーム時刻で、ブレークポイントの時刻から streamStartLocalSeconds
    /// （ストリーム時刻 0 に対応するクリップ相対秒）を引いてそろえる。
    /// 包絡が無い・全て等倍なら空文字（フィルタを付けない）。
    /// 式中のカンマはフィルタ引数の引用符（'…'）で保護する。
    /// 数値書式は全て不変カルチャ。非有限のオフセットは 0 として扱い、
    /// 非有限の時刻・ゲインを持つ点は落とす（壊れた式を ffmpeg に渡さないため）。
    /// タイムライン書き出し（Editor.Render）と単一クリップ書き出し（Editor.Media）が
    /// 同じ変換を読むことで、両経路のフェード／ダッキングの効き方が一致する。
    /// </summary>
    public static string BuildVolumeFilter(
        IReadOnlyList<GainBreakpoint>? breakpoints, double streamStartLocalSeconds = 0)
    {
        if (breakpoints is null || breakpoints.Count == 0)
            return string.Empty;
        var offset = double.IsFinite(streamStartLocalSeconds) ? streamStartLocalSeconds : 0;
        var points = breakpoints
            .Where(b => double.IsFinite(b.TimeSeconds) && double.IsFinite(b.Gain))
            .Select(b => (Time: b.TimeSeconds - offset, b.Gain))
            .ToList();
        if (points.Count == 0 || points.All(p => Math.Abs(p.Gain - 1.0) <= 1e-9))
            return string.Empty;

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string F(double v) => v.ToString("F4", inv);

        string expression;
        if (points.Count == 1)
        {
            expression = F(points[0].Gain);
        }
        else
        {
            // 後ろから組み立て：最後の区間より後は末尾のゲインで一定。
            expression = F(points[^1].Gain);
            for (var i = points.Count - 2; i >= 0; i--)
            {
                var a = points[i];
                var b = points[i + 1];
                var span = b.Time - a.Time;
                var segment = span <= 1e-9
                    ? F(b.Gain)
                    : $"{F(a.Gain)}+({F(b.Gain)}-{F(a.Gain)})*(t-{F(a.Time)})/{F(span)}";
                expression = $"if(lt(t,{F(b.Time)}),{segment},{expression})";
            }
            // 最初の点より前は先頭のゲインで一定。
            expression = $"if(lt(t,{F(points[0].Time)}),{F(points[0].Gain)},{expression})";
        }
        return $"volume='{expression}':eval=frame";
    }

    /// <summary>ブレークポイント列を線形補間で評価する（時刻はクリップ相対秒）。列の外側は端点の値。</summary>
    public static double EvaluateBreakpoints(IReadOnlyList<GainBreakpoint> breakpoints, double localSeconds)
    {
        if (breakpoints.Count == 0)
            return 1.0;
        if (localSeconds <= breakpoints[0].TimeSeconds)
            return breakpoints[0].Gain;
        var last = breakpoints[^1];
        if (localSeconds >= last.TimeSeconds)
            return last.Gain;
        for (var i = 0; i < breakpoints.Count - 1; i++)
        {
            var a = breakpoints[i];
            var b = breakpoints[i + 1];
            if (localSeconds < a.TimeSeconds || localSeconds > b.TimeSeconds)
                continue;
            var span = b.TimeSeconds - a.TimeSeconds;
            if (span <= 0)
                return b.Gain;
            var t = (localSeconds - a.TimeSeconds) / span;
            return a.Gain + (b.Gain - a.Gain) * t;
        }
        return last.Gain;
    }

    // --- トリガー区間の収集（素材相対 → タイムライン） ---

    /// <summary>
    /// トリガークリップの「鳴っている区間」（素材相対秒。イン点を 0 とする）を
    /// タイムライン絶対秒へ写す。写像は Clip.GetTimelineTimeSeconds（速度・逆再生を
    /// 含む）を通るため、トリガー側の速度変更・逆再生でも対象側と時刻がそろう。
    /// 結果はトリガークリップのタイムライン範囲に丸め、重なりをマージする。
    /// </summary>
    public static List<AudioSegment> MapTriggerSegmentsToTimeline(Clip triggerClip, IEnumerable<AudioSegment> sourceRelativeSegments)
    {
        var mapped = new List<AudioSegment>();
        foreach (var seg in sourceRelativeSegments)
        {
            var a = triggerClip.GetTimelineTimeSeconds(triggerClip.InPointSeconds + seg.StartSeconds);
            var b = triggerClip.GetTimelineTimeSeconds(triggerClip.InPointSeconds + seg.EndSeconds);
            var start = Math.Max(Math.Min(a, b), triggerClip.StartSeconds);
            var end = Math.Min(Math.Max(a, b), triggerClip.EndSeconds);
            if (end > start)
                mapped.Add(new AudioSegment(start, end));
        }
        return MergeIntervals(mapped);
    }

    /// <summary>
    /// プロジェクト内の全トリガークリップについて、提供関数で得た素材相対の
    /// 鳴っている区間をタイムラインへ写し、対象クリップに効く区間列として返す。
    /// 対象自身がトリガーでもある場合は自分自身は数えない（役割は1つだけのため
    /// 通常は起きないが、対象＝トリガーの自己参照を避ける）。
    /// </summary>
    public static List<AudioSegment> CollectTriggerIntervals(
        EditorProject project, Clip target,
        Func<Clip, IReadOnlyList<AudioSegment>> activeSegmentsOf)
    {
        var all = new List<AudioSegment>();
        foreach (var trigger in project.Tracks.SelectMany(t => t.Clips))
        {
            if (trigger.DuckingRole != DuckingRole.Trigger || trigger.Id == target.Id)
                continue;
            if (string.IsNullOrEmpty(trigger.SourcePath))
                continue;
            all.AddRange(MapTriggerSegmentsToTimeline(trigger, activeSegmentsOf(trigger)));
        }
        return MergeIntervals(all);
    }
}
