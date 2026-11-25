// 評価エンジン（設計プラン 5章）。
// 時刻 t における各クリップの確定値を計算する。
// プレビュー描画と書き出しは同じ評価結果を使うため、
// ここが両者の表示一致の要になる。

namespace Editor.Core;

/// <summary>ある時刻におけるクリップ1つの確定状態。</summary>
public sealed class EvaluatedClipState
{
    public required Clip Clip { get; init; }

    /// <summary>評価した時刻（秒）。スライス・ワープなど時刻依存のエフェクトの乱数種に使う。</summary>
    public double TimeSeconds { get; init; }
    public double Opacity { get; init; } = 1.0;
    public double Scale { get; init; } = 1.0;
    public double RotationDegrees { get; init; }
    public double PositionX { get; init; }
    public double PositionY { get; init; }
    public double Brightness { get; init; }
    public double Contrast { get; init; } = 1.0;

    /// <summary>ブレンドモード（クリップの固定値）。</summary>
    public BlendMode BlendMode { get; init; } = BlendMode.Normal;

    /// <summary>クリップに付いているエフェクト（時刻 t で評価済みの値の一覧。キーフレームが無ければ固定値のまま）。</summary>
    public IReadOnlyList<ClipEffect> Effects { get; init; } = Array.Empty<ClipEffect>();

    /// <summary>可視化クリップの設定（時刻 t で評価済み。可視化クリップでなければ null）。</summary>
    public VisualizationSettings? Visualization { get; init; }

    /// <summary>テキストアニメーションの確定状態（テキストクリップのみ評価。テキスト以外は Identity）。</summary>
    public TextAnimationState TextAnimation { get; init; } = TextAnimationState.Identity;
}

/// <summary>キーフレームと固定プロパティから時刻 t の状態を計算する。</summary>
public static class TimelineEvaluator
{
    /// <summary>クリップ1つの、時刻 t における状態を計算する。transitionOpacity はトランジションによる不透明度の倍率（1.0 が通常）。audio があれば周波数帯駆動バインドも適用する。</summary>
    public static EvaluatedClipState EvaluateClip(
        Clip clip, double timeSeconds, double transitionOpacity = 1.0,
        EditorProject? project = null, IAudioBandEnergyProvider? audio = null)
    {
        var local = timeSeconds - clip.StartSeconds;
        var opacity = EvaluateBoundParameter(project, clip, "Opacity", timeSeconds, 1.0, audio) * transitionOpacity;
        return new EvaluatedClipState
        {
            Clip = clip,
            TimeSeconds = timeSeconds,
            Opacity = Math.Clamp(opacity, 0, 1.0),
            Scale = EvaluateBoundParameter(project, clip, "Scale", timeSeconds, 1.0, audio),
            RotationDegrees = EvaluateBoundParameter(project, clip, "Rotation", timeSeconds, 0.0, audio),
            PositionX = EvaluateBoundParameter(project, clip, "PositionX", timeSeconds, 0.0, audio),
            PositionY = EvaluateBoundParameter(project, clip, "PositionY", timeSeconds, 0.0, audio),
            Brightness = clip.Brightness,
            Contrast = clip.Contrast,
            BlendMode = clip.BlendMode,
            Effects = EvaluateEffects(clip, timeSeconds, project, audio),
            Visualization = EvaluateVisualization(clip, local),
            // テキストクリップだけアニメーションを評価する。文字数は
            // StringInfo のテキスト要素数（LengthInTextElements）で数える。
            // レンダラー側も同じ数え方をするため、ここで数え方を統一する。
            TextAnimation = clip.Kind == ClipKind.Text
                ? TextAnimation.Evaluate(
                    clip.AnimationIn, clip.AnimationOut, local, clip.DurationSeconds,
                    new System.Globalization.StringInfo(clip.Text ?? string.Empty).LengthInTextElements)
                : TextAnimationState.Identity
        };
    }

    /// <summary>
    /// 可視化設定のキーフレーム化できる項目（Gain / LineWidth / WindowSeconds / TrailSeconds）を
    /// 時刻 t（クリップ相対秒）で評価する。キーフレームトラック "Visualization:{項目}" が無ければ
    /// 固定値のままなので、旧プロジェクトはそのまま動く。
    /// キーフレームがある場合だけコピーを作り、無いものは元のインスタンスを返す。
    /// </summary>
    private static VisualizationSettings? EvaluateVisualization(Clip clip, double localTimeSeconds)
    {
        var settings = clip.Visualization;
        if (settings is null)
            return null;

        var gain = clip.EvaluateParameter(
            VisualizationSettings.KeyframeParameterName("Gain"), localTimeSeconds, settings.Gain);
        var lineWidth = clip.EvaluateParameter(
            VisualizationSettings.KeyframeParameterName("LineWidth"), localTimeSeconds, settings.LineWidth);
        var windowSeconds = clip.EvaluateParameter(
            VisualizationSettings.KeyframeParameterName("WindowSeconds"), localTimeSeconds, settings.WindowSeconds);
        var trailSeconds = clip.EvaluateParameter(
            VisualizationSettings.KeyframeParameterName("TrailSeconds"), localTimeSeconds, settings.TrailSeconds);
        if (Math.Abs(gain - settings.Gain) < 1e-9
            && Math.Abs(lineWidth - settings.LineWidth) < 1e-9
            && Math.Abs(windowSeconds - settings.WindowSeconds) < 1e-9
            && Math.Abs(trailSeconds - settings.TrailSeconds) < 1e-9)
        {
            return settings;
        }

        var copy = settings.Clone();
        copy.Gain = VisualizationSettings.ClampGain(gain);
        copy.LineWidth = VisualizationSettings.ClampLineWidth(lineWidth);
        copy.WindowSeconds = VisualizationSettings.ClampWindowSeconds(windowSeconds);
        copy.TrailSeconds = VisualizationSettings.ClampTrailSeconds(trailSeconds);
        return copy;
    }

    /// <summary>
    /// エフェクトの Amount / Radius / 第3パラメータ（トーンカーブの中間調）を時刻 t（タイムライン秒）で評価する。
    /// まずキーフレームトラック "Effect:{種類}.Amount" / "Effect:{種類}.Radius" / "Effect:{種類}.Midtones" で基本値を求め、
    /// そのパラメータの有効なバインドがあれば帯域エネルギーで動かす（基本パラメータと
    /// 同じ合成順序：キーフレーム → バインド → 種類の範囲に丸める）。
    /// キーフレームもバインドも無ければ固定値のままなので、旧プロジェクトはそのまま動く。
    /// 値が変わったエフェクトだけコピーを作り、変わらないものは元のインスタンスを返す。
    /// </summary>
    private static IReadOnlyList<ClipEffect> EvaluateEffects(
        Clip clip, double timeSeconds, EditorProject? project, IAudioBandEnergyProvider? audio)
    {
        if (clip.Effects.Count == 0)
            return Array.Empty<ClipEffect>();

        var local = timeSeconds - clip.StartSeconds;
        var result = new List<ClipEffect>(clip.Effects.Count);
        foreach (var effect in clip.Effects)
        {
            var amountParameter = ClipEffect.KeyframeParameterName(effect.Kind, isAmount: true);
            var amount = clip.EvaluateParameter(amountParameter, local, effect.Amount);
            amount = ApplyAudioBindings(project, clip, amountParameter, timeSeconds, amount, audio);
            var radiusParameter = ClipEffect.KeyframeParameterName(effect.Kind, isAmount: false);
            var radius = clip.EvaluateParameter(radiusParameter, local, effect.Radius);
            radius = ApplyAudioBindings(project, clip, radiusParameter, timeSeconds, radius, audio);
            var midtones = effect.Midtones;
            if (ClipEffect.UsesThird(effect.Kind))
            {
                var midtonesParameter = ClipEffect.KeyframeParameterName(effect.Kind, "Midtones");
                midtones = clip.EvaluateParameter(midtonesParameter, local, effect.Midtones);
                midtones = ApplyAudioBindings(project, clip, midtonesParameter, timeSeconds, midtones, audio);
            }
            if (Math.Abs(amount - effect.Amount) < 1e-9 && Math.Abs(radius - effect.Radius) < 1e-9
                && Math.Abs(midtones - effect.Midtones) < 1e-9)
            {
                result.Add(effect);
                continue;
            }

            var copy = effect.Clone();
            copy.Amount = ClipEffect.ClampAmount(effect.Kind, amount);
            copy.Radius = ClipEffect.ClampRadius(effect.Kind, radius);
            copy.Midtones = ClipEffect.ClampMidtones(effect.Kind, midtones);
            result.Add(copy);
        }
        return result;
    }

    /// <summary>
    /// パラメータ1つの時刻 t における値。まずキーフレームで基本値を求め、
    /// そのパラメータの有効なバインドがあれば帯域エネルギーで動かす。
    /// 音源クリップが見つからない・解析されていない・その時刻に流れていない場合は
    /// バインドは無視され、キーフレーム値のままになる。
    /// </summary>
    private static double EvaluateBoundParameter(
        EditorProject? project, Clip clip, string parameterName,
        double timeSeconds, double defaultValue, IAudioBandEnergyProvider? audio)
    {
        var local = timeSeconds - clip.StartSeconds;
        var value = clip.EvaluateParameter(parameterName, local, defaultValue);
        return ApplyAudioBindings(project, clip, parameterName, timeSeconds, value, audio);
    }

    /// <summary>
    /// 指定パラメータの有効なバインドをすべて適用する（キーフレーム値 → バインド合成値）。
    /// バインドの対象は基本パラメータ（"Opacity" など）とエフェクト量
    /// （"Effect:{種類}.Amount" / "Effect:{種類}.Radius" / "Effect:{種類}.Midtones"）のどれでもよい。
    /// 音源クリップが見つからない・解析されていない・その時刻に流れていないバインドは無視する。
    /// </summary>
    private static double ApplyAudioBindings(
        EditorProject? project, Clip clip, string parameterName,
        double timeSeconds, double value, IAudioBandEnergyProvider? audio)
    {
        if (audio is null || project is null || clip.AudioBindings.Count == 0)
            return value;

        foreach (var binding in clip.AudioBindings)
        {
            if (!binding.Enabled || binding.ParameterName != parameterName)
                continue;
            if (string.IsNullOrEmpty(binding.SourceClipId))
                continue;
            var source = project.Tracks
                .SelectMany(t => t.Clips)
                .FirstOrDefault(c => c.Id == binding.SourceClipId);
            if (source is null || string.IsNullOrEmpty(source.SourcePath))
                continue;
            // タイムライン時刻を音源クリップの素材内時刻へ変換する
            // （逆再生の音源なら、逆向きに進む位置のエネルギーになる）。
            if (timeSeconds < source.StartSeconds - 1e-6
                || timeSeconds > source.EndSeconds + 1e-6)
                continue; // 音源がその時刻に流れていない。
            var sourceTime = source.GetSourceTimeSeconds(timeSeconds);
            var energy = audio.GetBandEnergy(source.Id, sourceTime, binding.LowHz, binding.HighHz);
            value = binding.CombineWith(value, binding.MapValue(energy));
        }
        return value;
    }

    /// <summary>時刻 t で有効な全クリップの状態を返す。下のトラックから順に並ぶ。audio があればバインドも評価する。</summary>
    public static List<EvaluatedClipState> EvaluateProject(
        EditorProject project, double timeSeconds, IAudioBandEnergyProvider? audio = null)
    {
        var result = new List<EvaluatedClipState>();
        foreach (var track in project.Tracks)
        {
            if (!track.IsVisible)
                continue;
            var clips = track.Clips.OrderBy(c => c.StartSeconds).ToList();
            for (var i = 0; i < clips.Count; i++)
            {
                var clip = clips[i];
                var factor = GetTransitionFactor(clips, i, timeSeconds);
                // クリップが有効な時刻か、トランジション区間に入っているときだけ描く。
                // （factor は区間外では 1.0 になるので、factor > 0 では全クリップが
                // 常に含まれてしまい、範囲外のクリップまで描かれてしまう。）
                if (clip.IsActiveAt(timeSeconds) || IsInTransitionZone(clips, i, timeSeconds))
                    result.Add(EvaluateClip(clip, timeSeconds, factor, project, audio));
            }
        }
        return result;
    }

    /// <summary>
    /// クリップの開始側ディゾルブの区間（境界を中心に前後 D/2 ずつ）。
    /// ディゾルブがない・隣り合っていない・長さが 0 の場合は null。
    /// GetTransitionFactor と IsInTransitionZone の共通定義（区間の定義はここだけ）。
    /// </summary>
    private static (double Start, double End)? GetDissolveZone(IReadOnlyList<Clip> clips, int index)
    {
        if (index <= 0 || clips[index].TransitionKind != TransitionKind.Dissolve)
            return null;
        var duration = GetEffectiveTransitionDuration(clips[index - 1], clips[index]);
        if (duration <= 0)
            return null;
        var start = clips[index].StartSeconds - duration / 2;
        return (start, start + duration);
    }

    /// <summary>
    /// クリップのトランジションによる不透明度の倍率を返す（1.0 が通常）。
    /// 開始側にディゾルブがあるクリップは、境界を中心に前後 D/2 ずつの区間でフェードインする。
    /// 前のクリップは減光しない：不透明度で重ねる描画では、前のクリップを薄くすると
    /// 2枚の合成が暗く沈むため、前のクリップはそのまま描き、後のクリップが上から
    /// かぶさっていく形でクロスフェードにする。
    /// </summary>
    private static double GetTransitionFactor(IReadOnlyList<Clip> clips, int index, double timeSeconds)
    {
        var zone = GetDissolveZone(clips, index);
        if (zone is null)
            return 1.0;
        if (timeSeconds >= zone.Value.Start && timeSeconds <= zone.Value.End)
            return Math.Clamp((timeSeconds - zone.Value.Start) / (zone.Value.End - zone.Value.Start), 0, 1);
        return 1.0;
    }

    /// <summary>クリップがトランジション区間に入っているか（開始側のフェードイン区間、または次のクリップへのフェードアウト区間）。</summary>
    private static bool IsInTransitionZone(IReadOnlyList<Clip> clips, int index, double timeSeconds)
    {
        var zone = GetDissolveZone(clips, index);
        if (zone is not null && timeSeconds >= zone.Value.Start && timeSeconds <= zone.Value.End)
            return true;
        if (index + 1 < clips.Count)
        {
            var nextZone = GetDissolveZone(clips, index + 1);
            if (nextZone is not null && timeSeconds >= nextZone.Value.Start && timeSeconds <= nextZone.Value.End)
                return true;
        }
        return false;
    }

    /// <summary>
    /// 隣り合う2クリップ間のトランジションの実効長さ（秒）。
    /// 隣り合っていない（隙間がある・重なっている）場合は 0。
    /// 区間がクリップからはみ出さないよう、両クリップの長さの2倍までに丸める。
    /// </summary>
    public static double GetEffectiveTransitionDuration(Clip prev, Clip next)
    {
        if (Math.Abs(prev.EndSeconds - next.StartSeconds) > 1e-6)
            return 0; // 隣り合っていない。
        var duration = next.TransitionDurationSeconds;
        if (duration <= 0)
            return 0;
        return Math.Min(duration, Math.Min(prev.DurationSeconds, next.DurationSeconds) * 2);
    }
}
