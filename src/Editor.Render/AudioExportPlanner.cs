// タイムライン書き出しの音声ミックス計画（設計プラン 5章）。
// 書き出し範囲に入る音声つきクリップを集め、ffmpeg のフィルタ
// （atrim / adelay / amix、ノイズ除去は afftdn）で1本にミックスする計画を作る。
// フィルタグラフの文字列はここだけが持つ。FrameSequenceExporter は
// この計画を受け取り、ffmpeg の起動とフレームのパイプだけを行う。

using Editor.Core;

namespace Editor.Render;

/// <summary>書き出し範囲の音声ミックス計画を作る。読めない素材は音声なしで進む。</summary>
internal sealed class AudioExportPlanner
{
    private readonly IMediaProbe _probe;
    private readonly IAudioPeakProvider? _peakProvider;
    private readonly IAudioActivityProvider? _activityProvider;

    public AudioExportPlanner(
        IMediaProbe probe, IAudioPeakProvider? peakProvider = null,
        IAudioActivityProvider? activityProvider = null)
    {
        _probe = probe;
        _peakProvider = peakProvider;
        _activityProvider = activityProvider;
    }

    /// <summary>書き出し範囲に入る音声つきクリップの入力計画を作る。音声が無ければ MapLabel は null。</summary>
    public async Task<(List<AudioInput> Inputs, string? MapLabel)> BuildAudioPlanAsync(
        EditorProject project, double startSeconds, double endSeconds, CancellationToken ct)
    {
        var inputs = new List<AudioInput>();

        var clips = project.Tracks
            .SelectMany(t => t.Clips)
            .Where(c => !string.IsNullOrEmpty(c.SourcePath)
                        && c.EndSeconds > startSeconds && c.StartSeconds < endSeconds)
            .OrderBy(c => c.StartSeconds)
            .ToList();

        var index = 1; // 0 は映像（標準入力）なので音声は 1 から。
        foreach (var clip in clips)
        {
            ct.ThrowIfCancellationRequested();
            MediaInfo? info;
            try
            {
                info = await _probe.ProbeAsync(clip.SourcePath, ct).ConfigureAwait(false);
            }
            catch
            {
                continue; // 読めない素材は音声なしで進む。
            }
            if (info is null || !info.HasAudio)
                continue;

            var overlapStart = Math.Max(clip.StartSeconds, startSeconds);
            var overlapEnd = Math.Min(clip.EndSeconds, endSeconds);
            if (overlapEnd <= overlapStart)
                continue;

            // 書き出し範囲と重なる部分の素材範囲。素材時刻の写像はプレビューと同じ
            // Clip.GetSourceTimeSeconds（速度・逆再生を含む）を使う。
            // 逆再生では開始側と終了側が入れ替わるため、小さい方を開始にする。
            var mappedStart = clip.GetSourceTimeSeconds(overlapStart);
            var mappedEnd = clip.GetSourceTimeSeconds(overlapEnd);
            var sourceStart = Math.Min(mappedStart, mappedEnd);
            var sourceEnd = Math.Max(mappedStart, mappedEnd);
            var delayMs = (long)Math.Round((overlapStart - startSeconds) * 1000);
            var label = $"a{index}";
            // ノーマライズが有効なクリップは、クリップの使用範囲のピークから
            // ゲインを計算する（プレビューと同じ計算・同じ提供口。ピークは
            // 書き出し範囲ではなくクリップの使用範囲で測るため、書き出し範囲を
            // 変えてもゲインは変わらない）。ピークが測れなければ等倍で書き出す。
            var normalizeGain = 1.0;
            if (clip.NormalizeAudioEnabled && _peakProvider is not null)
            {
                try
                {
                    var peak = await _peakProvider
                        .GetPeakAsync(clip.SourcePath, clip.InPointSeconds, clip.SourceDurationSeconds, ct)
                        .ConfigureAwait(false);
                    if (peak.Available)
                        normalizeGain = AudioNormalizer.ComputeGain(peak.PeakLinear, clip.NormalizeTargetDb);
                }
                catch
                {
                    // ピーク解析の失敗で書き出しを止めない（元の音量で書き出す）。
                }
            }
            // フェード／ダッキング包絡。ダッキング対象はトリガークリップの
            // 鳴っている区間を解析してタイムラインへ写す（プレビューと同じ提供口・
            // 同じ Core 計算）。測れなければ区間は空になり、フェードだけが効く。
            IReadOnlyList<GainBreakpoint>? envelope = null;
            if (AudioGainEnvelope.HasEnvelope(clip, Array.Empty<AudioSegment>())
                || clip.DuckingRole == DuckingRole.Target)
            {
                var triggerIntervals = await CollectTriggerIntervalsAsync(project, clip, ct)
                    .ConfigureAwait(false);
                if (AudioGainEnvelope.HasEnvelope(clip, triggerIntervals))
                    envelope = AudioGainEnvelope.BuildBreakpoints(clip, triggerIntervals);
            }
            // フィルタ（ノイズ除去・ノーマライズ・切り出し・逆再生・遅延）は BuildClipFilter が作る。
            var filter = BuildClipFilter(index, clip, sourceStart, sourceEnd, delayMs, label, normalizeGain,
                envelope, overlapStart - clip.StartSeconds);
            inputs.Add(new AudioInput(clip.SourcePath, filter, label));
            index++;
        }

        if (inputs.Count == 0)
            return (inputs, null);

        var mapLabel = inputs.Count == 1 ? inputs[0].Label : "aout";
        return (inputs, mapLabel);
    }

    /// <summary>
    /// 対象クリップに効くトリガー区間（タイムライン絶対秒）を集める。
    /// トリガーごとに鳴っている区間を解析し、Core の収集関数で写像・マージする。
    /// 解析できないトリガーは区間なしとして扱い、書き出しは止めない。
    /// </summary>
    private async Task<List<AudioSegment>> CollectTriggerIntervalsAsync(
        EditorProject project, Clip target, CancellationToken ct)
    {
        if (target.DuckingRole != DuckingRole.Target || _activityProvider is null)
            return new List<AudioSegment>();

        var segmentsByTrigger = new Dictionary<string, IReadOnlyList<AudioSegment>>();
        foreach (var trigger in project.Tracks.SelectMany(t => t.Clips)
                     .Where(c => c.DuckingRole == DuckingRole.Trigger && c.Id != target.Id
                                 && !string.IsNullOrEmpty(c.SourcePath)))
        {
            try
            {
                segmentsByTrigger[trigger.Id] = await _activityProvider
                    .GetActiveSegmentsAsync(trigger.SourcePath, trigger.InPointSeconds,
                        trigger.SourceDurationSeconds, target.DuckingThresholdDb, ct)
                    .ConfigureAwait(false);
            }
            catch
            {
                segmentsByTrigger[trigger.Id] = Array.Empty<AudioSegment>();
            }
        }
        return AudioGainEnvelope.CollectTriggerIntervals(
            project, target,
            trigger => segmentsByTrigger.TryGetValue(trigger.Id, out var s) ? s : Array.Empty<AudioSegment>());
    }

    /// <summary>
    /// 音声クリップ1つのフィルタを作る。書き出し範囲に入る部分だけ切り出し、
    /// 逆再生なら切り出した区間を逆向きにしてから、速度変更（atempo）を掛け、
    /// 範囲内の位置まで遅らせる。
    /// areverse は atrim の後に掛ける（切り出した区間だけを逆向きにするため。
    /// atrim の前に掛けると切り出し位置の対応が逆転してしまう）。
    /// atempo は areverse の後に掛ける（逆向きになった後の音の長さを変えるため。
    /// 順序を入れ替えると結果は同じ長さになるが、ピッチ保持の処理単位が変わり
    /// 音質が安定しない）。atempo はピッチを保ったまま長さだけを変える。
    /// areverse / atempo の後に asetpts で PTS を振り直す（両フィルタが PTS を
    /// どう扱うかに依存せず、遅延の基準をそろえるため）。
    /// normalizeGain はノーマライズの線形倍率（1.0 が等倍）。volume フィルタは
    /// atrim の前に掛ける（ゲインは線形のため、atrim の前後で結果は同じ）。
    /// </summary>
    internal static string BuildClipFilter(
        int index, Clip clip, double sourceStart, double sourceEnd, long delayMs, string label,
        double normalizeGain = 1.0,
        IReadOnlyList<GainBreakpoint>? envelope = null, double envelopeStartLocalSeconds = 0)
    {
        // ノイズ除去が有効なクリップは afftdn で背景ノイズを減らす
        // （設計プラン 5章で FFmpeg のフィルタが第一候補。プレビューでは反映されない）。
        // atrim の前に掛けることで、ファイル全体からノイズ特性を取る。
        var denoise = clip.NoiseReductionEnabled
            ? FormattableString.Invariant($"afftdn=nr={NoiseReductionDb(clip.NoiseReductionStrength):F1}:nf=-50,")
            : string.Empty;
        var normalize = VolumeFilter(normalizeGain);
        var reverse = clip.IsReversed ? "areverse," : string.Empty;
        var tempo = AtempoChain(clip.Speed);
        // フェード／ダッキング包絡は asetpts の直後・adelay の前に掛ける。
        // この時点のストリーム時刻 t は「書き出し範囲と重なる部分の先頭から
        // タイムライン方向に進んだ秒数」になっており、包絡のブレークポイント
        // （クリップ相対・タイムライン秒）から envelopeStartLocalSeconds を
        // 引いたものと一致する。atrim の前では逆再生・速度変更で対応が壊れ、
        // adelay の後では書き出し範囲の開始位置に依存してしまうため、この位置。
        var envelopeFilter = EnvelopeVolumeFilter(envelope, envelopeStartLocalSeconds);
        return FormattableString.Invariant($"[{index}:a]{denoise}{normalize}atrim=start={sourceStart:F3}:end={sourceEnd:F3},") +
               $"{reverse}{tempo}asetpts=PTS-STARTPTS,{envelopeFilter}adelay={delayMs}:all=1[{label}]";
    }

    /// <summary>
    /// 包絡のブレークポイント列を ffmpeg の volume フィルタ（eval=frame）にする。
    /// 変換の本体は Core の AudioGainEnvelope.BuildVolumeFilter（単一クリップ書き出しと
    /// 同じ変換を読み、両経路の結果を一致させる）。ここではフィルタチェーンの
    /// 途中に埋めるための末尾カンマだけを付ける。t はストリーム時刻（asetpts 後）で、
    /// ブレークポイントの時刻から envelopeStartLocalSeconds（重なり部分の
    /// クリップ相対開始秒）を引いてそろえる。包絡が無い・全て等倍なら空文字。
    /// </summary>
    internal static string EnvelopeVolumeFilter(
        IReadOnlyList<GainBreakpoint>? envelope, double envelopeStartLocalSeconds = 0)
    {
        var filter = AudioGainEnvelope.BuildVolumeFilter(envelope, envelopeStartLocalSeconds);
        return filter.Length == 0 ? string.Empty : filter + ",";
    }

    /// <summary>
    /// 速度（倍率）を ffmpeg の atempo の連鎖にする。atempo 1つは 0.5〜2.0 の範囲しか
    /// 受け付けないため、範囲外は 2.0 / 0.5 を先に掛けて残りを最後に掛ける
    /// （例：4.0 → atempo=2.000,atempo=2.000、0.1 → atempo=0.500,atempo=0.500,atempo=0.500,atempo=0.800）。
    /// 等速（1.0）のときは空文字（フィルタを付けない）。末尾に "," を含む。
    /// </summary>
    internal static string AtempoChain(double speed)
    {
        var clamped = Clip.ClampSpeed(speed);
        if (Math.Abs(clamped - 1.0) <= 1e-9)
            return string.Empty;
        var parts = new List<string>();
        var remaining = clamped;
        while (remaining > 2.0)
        {
            parts.Add("atempo=2.000");
            remaining /= 2.0;
        }
        while (remaining < 0.5)
        {
            parts.Add("atempo=0.500");
            remaining /= 0.5;
        }
        parts.Add(FormattableString.Invariant($"atempo={remaining:F3}"));
        return string.Join(",", parts) + ",";
    }

    /// <summary>ノイズ除去の強さ（0.0〜1.0）を afftdn の nr（dB、1〜30）に写す。</summary>
    private static double NoiseReductionDb(double strength)
        => 1.0 + Math.Clamp(strength, 0, 1) * 29.0;

    /// <summary>ノーマライズのゲイン（線形倍率）を ffmpeg の volume フィルタにする。等倍（1.0）のときは空文字（フィルタを付けない）。</summary>
    internal static string VolumeFilter(double gain)
        => Math.Abs(gain - 1.0) <= 1e-6 ? string.Empty : FormattableString.Invariant($"volume={gain:F4},");


}

/// <summary>音声入力1つ（ファイル・フィルタ・マップラベル）。</summary>
internal sealed record AudioInput(string Path, string Filter, string Label);
