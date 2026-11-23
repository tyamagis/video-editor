// Editor.Core — 編集ドメインモデル（設計プラン 5章）。
// UI・FFmpeg・NAudio・SkiaSharpには依存しない純粋なドメインモデル。
// プレビュー描画と書き出しの両方が、このモデルの評価結果を読む。

namespace Editor.Core;

/// <summary>タイムラインのグリッド表示モード（設計プラン 4章・6章）。</summary>
public enum GridMode
{
    Frame,
    Second,
    Beat
}

/// <summary>トラック種別。</summary>
public enum TrackKind
{
    Video,
    Audio,
    Text
}

/// <summary>クリップ種別。</summary>
public enum ClipKind
{
    Video,
    Audio,
    Image,
    Text
}

/// <summary>キーフレーム間の補間方法。</summary>
public enum Interpolation
{
    Hold,
    Linear,
    EaseInOut
}

/// <summary>1つのキーフレーム。時刻はクリップ先頭からの相対秒。</summary>
public sealed class Keyframe
{
    public double TimeSeconds { get; set; }
    public double Value { get; set; }
    public Interpolation Interpolation { get; set; } = Interpolation.Linear;

    public Keyframe() { }

    public Keyframe(double timeSeconds, double value, Interpolation interpolation = Interpolation.Linear)
    {
        TimeSeconds = timeSeconds;
        Value = value;
        Interpolation = interpolation;
    }
}

/// <summary>
/// 1つのパラメータに対するキーフレーム列。
/// パラメータ名は "Opacity" / "Scale" / "Rotation" / "PositionX" / "PositionY"。
/// </summary>
public sealed class KeyframeTrack
{
    public string ParameterName { get; set; } = string.Empty;
    public List<Keyframe> Keyframes { get; set; } = new();

    /// <summary>クリップ相対時刻における値を補間して返す。</summary>
    public double Evaluate(double timeSeconds, double defaultValue)
    {
        if (Keyframes.Count == 0)
            return defaultValue;

        var sorted = Keyframes.OrderBy(k => k.TimeSeconds).ToList();
        if (timeSeconds <= sorted[0].TimeSeconds)
            return sorted[0].Value;

        var last = sorted[^1];
        if (timeSeconds >= last.TimeSeconds)
            return last.Value;

        for (var i = 0; i < sorted.Count - 1; i++)
        {
            var a = sorted[i];
            var b = sorted[i + 1];
            if (timeSeconds < a.TimeSeconds || timeSeconds > b.TimeSeconds)
                continue;

            if (a.Interpolation == Interpolation.Hold)
                return a.Value;

            var span = b.TimeSeconds - a.TimeSeconds;
            if (span <= 0)
                return b.Value;

            var t = (timeSeconds - a.TimeSeconds) / span;
            if (a.Interpolation == Interpolation.EaseInOut)
                t = t * t * (3 - 2 * t); // smoothstep

            return a.Value + (b.Value - a.Value) * t;
        }

        return defaultValue;
    }
}

/// <summary>タイムライン上の1単位。素材参照、配置、プロパティ、キーフレームを持つ。</summary>
public sealed class Clip
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public ClipKind Kind { get; set; } = ClipKind.Video;

    /// <summary>素材ファイルへのパス。テキストクリップでは空。</summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>タイムライン上の開始時刻（秒）。</summary>
    public double StartSeconds { get; set; }

    /// <summary>タイムライン上での長さ（秒）。</summary>
    public double DurationSeconds { get; set; }

    /// <summary>素材内での開始位置（イン点、秒）。トリミングで変わる。</summary>
    public double InPointSeconds { get; set; }

    // --- テキストクリップ用 ---
    public string Text { get; set; } = string.Empty;
    public string FontFamily { get; set; } = "Yu Gothic UI";
    public double FontSize { get; set; } = 64;
    public string FontColorHex { get; set; } = "#FFFFFFFF";

    // --- 色調補正（フェーズ1：キーフレームなしの固定値。キーフレーム化は後段で） ---
    public double Brightness { get; set; } // -1.0 .. 1.0
    public double Contrast { get; set; } = 1.0; // 0.0 .. 2.0

    public List<KeyframeTrack> KeyframeTracks { get; set; } = new();

    public double EndSeconds => StartSeconds + DurationSeconds;

    public bool IsActiveAt(double timeSeconds)
        => timeSeconds >= StartSeconds && timeSeconds < EndSeconds;

    public KeyframeTrack EnsureTrack(string parameterName)
    {
        var track = KeyframeTracks.FirstOrDefault(t => t.ParameterName == parameterName);
        if (track is null)
        {
            track = new KeyframeTrack { ParameterName = parameterName };
            KeyframeTracks.Add(track);
        }
        return track;
    }

    public double EvaluateParameter(string parameterName, double localTimeSeconds, double defaultValue)
    {
        var track = KeyframeTracks.FirstOrDefault(t => t.ParameterName == parameterName);
        return track?.Evaluate(localTimeSeconds, defaultValue) ?? defaultValue;
    }

    /// <summary>分割用に複製する。キーフレームは新しいIdでコピーされる。</summary>
    public Clip Clone()
    {
        var copy = (Clip)MemberwiseClone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.KeyframeTracks = KeyframeTracks
            .Select(t => new KeyframeTrack
            {
                ParameterName = t.ParameterName,
                Keyframes = t.Keyframes
                    .Select(k => new Keyframe(k.TimeSeconds, k.Value, k.Interpolation))
                    .ToList()
            })
            .ToList();
        return copy;
    }
}

/// <summary>1本のトラック。種別ごとにクリップを持つ。</summary>
public sealed class Track
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public TrackKind Kind { get; set; } = TrackKind.Video;
    public List<Clip> Clips { get; set; } = new();
    public bool IsVisible { get; set; } = true;
    public bool IsMuted { get; set; }
}

/// <summary>
/// プロジェクト全体。設計プラン 5章のデータモデル。
/// 素材はプロジェクトファイルに含めず、パス参照のみとする。
/// </summary>
public sealed class EditorProject
{
    /// <summary>プロジェクトファイル形式の版。読み込み時に移行判定に使う。</summary>
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public string Name { get; set; } = "Untitled";

    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public int FrameRate { get; set; } = 30;

    /// <summary>BPM（手動設定。既定120）。拍グリッドの基準になる。</summary>
    public double Bpm { get; set; } = 120.0;

    /// <summary>書き出し範囲の開始（秒）。0 は先頭。</summary>
    public double ExportInSeconds { get; set; }

    /// <summary>書き出し範囲の終了（秒）。0 または開始以下は末尾まで。</summary>
    public double ExportOutSeconds { get; set; }

    /// <summary>書き出し範囲の開始（0〜全体の長さに丸める）。</summary>
    public double EffectiveExportInSeconds => Math.Clamp(ExportInSeconds, 0, DurationSeconds);

    /// <summary>書き出し範囲の終了（0・開始以下なら末尾）。</summary>
    public double EffectiveExportOutSeconds
    {
        get
        {
            var end = ExportOutSeconds > EffectiveExportInSeconds ? ExportOutSeconds : DurationSeconds;
            return Math.Clamp(end, EffectiveExportInSeconds, DurationSeconds);
        }
    }

    /// <summary>最初の拍の時刻（秒）。BPMと合わせて拍位置を決める。</summary>
    public double BeatOffsetSeconds { get; set; }

    public GridMode GridMode { get; set; } = GridMode.Frame;

    public List<Track> Tracks { get; set; } = new();

    /// <summary>全トラックの末尾までの長さ（秒）。トラックが無ければ0。</summary>
    public double DurationSeconds
        => Tracks.SelectMany(t => t.Clips).Select(c => c.EndSeconds).DefaultIfEmpty(0).Max();

    public Track AddTrack(TrackKind kind, string name)
    {
        var track = new Track { Kind = kind, Name = name };
        Tracks.Add(track);
        return track;
    }
}
