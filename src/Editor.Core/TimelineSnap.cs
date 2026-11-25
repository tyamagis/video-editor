// スナップ先の計算（クリップの移動・トリミング時の吸着）。
// UI（TimelineControl）はピクセル閾値を秒へ換算して渡し、ここは秒だけを扱う純粋ロジックにする
// （テスト可能にするため。閾値の換算は UI 側：SnapThresholdPixels / PixelsPerSecond）。
//
// スナップ先（候補）の内訳：
//   - タイムライン先頭（0秒）
//   - マーカー位置（プロジェクトの全マーカー）
//   - 再生ヘッド位置（呼び出し側が渡したとき）
//   - 他クリップの開始・終了（全トラック。ドラッグ中のクリップと、そのグループの
//     クリップは除外する＝自分自身の端には吸着しない）
//   - 拍グリッド（GridMode=Beat のときだけ、呼び出し側が拍時刻を候補に足す。
//     検出ビートマップがあればその時刻、BPM グリッドなら BeatGridCalculator.GetBeatTimes）

namespace Editor.Core;

/// <summary>スナップ候補の収集と吸着計算。</summary>
public static class SnapCalculator
{
    /// <summary>時刻の同一判定に使う許容誤差（秒）。</summary>
    public const double EpsilonSeconds = 1e-9;

    /// <summary>
    /// スナップ候補の時刻を集める（昇順・重複なし）。
    /// excludedClips に入れたクリップ（ドラッグ中のクリップとそのグループ全員）の端は含めない。
    /// playheadSeconds が null でなければ再生ヘッド位置も候補に入れる。
    /// </summary>
    public static List<double> CollectSnapTimes(
        EditorProject project,
        IEnumerable<Clip>? excludedClips = null,
        double? playheadSeconds = null)
    {
        var excludedIds = excludedClips is null
            ? new HashSet<string>()
            : new HashSet<string>(excludedClips.Select(c => c.Id));
        var times = new SortedSet<double> { 0.0 };
        foreach (var marker in project.Markers)
            times.Add(marker.TimeSeconds);
        if (playheadSeconds is { } playhead && playhead >= 0)
            times.Add(playhead);
        foreach (var track in project.Tracks)
        {
            foreach (var clip in track.Clips)
            {
                if (excludedIds.Contains(clip.Id))
                    continue;
                times.Add(clip.StartSeconds);
                times.Add(clip.EndSeconds);
            }
        }
        return times.ToList();
    }

    /// <summary>
    /// 拍時刻を候補へ足す（GridMode=Beat のときの UI 用。重複は除き、昇順を保つ）。
    /// 拍はプロジェクト全体に生成すると本数が膨らむため、対象時刻の前後
    /// toleranceSeconds 以内にある拍だけを足せば十分（吸着はその範囲でしか起きない）。
    /// </summary>
    public static List<double> WithNearbyBeatTimes(
        IReadOnlyList<double> candidates, IEnumerable<double> beatTimes, double nearTimeSeconds, double toleranceSeconds)
    {
        var merged = new SortedSet<double>(candidates);
        foreach (var beat in beatTimes)
        {
            if (Math.Abs(beat - nearTimeSeconds) <= toleranceSeconds)
                merged.Add(beat);
        }
        return merged.ToList();
    }

    /// <summary>最も近い候補へ吸着する。許容範囲（toleranceSeconds）より遠ければ null。</summary>
    public static double? SnapToNearest(double timeSeconds, IReadOnlyList<double> candidates, double toleranceSeconds)
    {
        if (toleranceSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(toleranceSeconds), "スナップ許容範囲は0以上でなければなりません。");
        double? best = null;
        var bestDist = double.MaxValue;
        foreach (var candidate in candidates)
        {
            var dist = Math.Abs(candidate - timeSeconds);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = candidate;
            }
        }
        return bestDist <= toleranceSeconds + EpsilonSeconds ? best : null;
    }

    /// <summary>
    /// クリップ移動のスナップ。開始端と終了端の両方を候補と比べ、
    /// 補正量が小さい方の端で吸着した開始時刻を返す。どちらも範囲外なら要求どおりの開始時刻。
    /// 同点の場合は開始端を優先する（開始位置を決める操作のため）。
    /// 結果は0未満にはしない（MoveClip と同じ規則）。
    /// </summary>
    public static double SnapMoveStart(
        double requestedStartSeconds, double durationSeconds, IReadOnlyList<double> candidates, double toleranceSeconds)
    {
        var snappedStart = SnapToNearest(requestedStartSeconds, candidates, toleranceSeconds);
        var snappedEnd = SnapToNearest(requestedStartSeconds + durationSeconds, candidates, toleranceSeconds);

        var startCorrection = snappedStart is { } s ? Math.Abs(s - requestedStartSeconds) : double.MaxValue;
        var endCorrection = snappedEnd is { } e ? Math.Abs(e - (requestedStartSeconds + durationSeconds)) : double.MaxValue;

        double result;
        if (startCorrection == double.MaxValue && endCorrection == double.MaxValue)
            result = requestedStartSeconds;
        else if (startCorrection <= endCorrection)
            result = snappedStart!.Value;
        else
            result = snappedEnd!.Value - durationSeconds;
        return Math.Max(0, result);
    }
}
