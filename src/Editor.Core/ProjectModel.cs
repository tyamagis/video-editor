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

/// <summary>クリップ境界のトランジション種別。開始側（前のクリップとの境界）に付ける。</summary>
public enum TransitionKind
{
    None,
    Dissolve
}

/// <summary>クリップのブレンドモード（上のトラックのクリップが下とどう合成されるか）。</summary>
public enum BlendMode
{
    Normal,
    Add,
    Screen,
    Multiply,
    Overlay
}

/// <summary>クリップに付けるエフェクトの種類。</summary>
public enum EffectKind
{
    Blur,
    Glow,
    RgbShift,
    Slice,
    Warp,
    ChromaKey
}

/// <summary>
/// クリップに付けるエフェクト1つ。パラメータは固定値（キーフレーム化は後段で）。
/// パラメータの意味は種類ごとに決まる。
/// <list type="bullet">
/// <item>Blur：Amount = ぼかし半径（px、0〜100）。Radius・KeyColorHex は使わない。</item>
/// <item>Glow：Amount = 強さ（0〜1）、Radius = ぼかし半径（px、1〜100）。</item>
/// <item>RgbShift：Amount = チャンネルのずれ（px、0〜50）。</item>
/// <item>Slice：Amount = スライスのずれ幅（px、0〜100）。</item>
/// <item>Warp：Amount = 波の振幅（px、0〜100）、Radius = 波長（px、20〜1000）。</item>
/// <item>ChromaKey：Amount = しきい値（0〜1）、Radius = なめらかさ（0〜1）、KeyColorHex = 抜く色。</item>
/// </list>
/// </summary>
public sealed class ClipEffect
{
    public EffectKind Kind { get; set; }
    public double Amount { get; set; }
    public double Radius { get; set; }
    public string KeyColorHex { get; set; } = "#00FF00";

    public ClipEffect Clone() => (ClipEffect)MemberwiseClone();
}

/// <summary>バインドの合成方法（キーフレーム値と音連動値の合成）。</summary>
public enum AudioBindCombine
{
    /// <summary>キーフレーム値に音連動値の変化分を加算する（無音時はキーフレーム値のまま）。</summary>
    Add,
    /// <summary>キーフレーム値に音連動値を掛ける（出力範囲が倍率として働く）。</summary>
    Multiply,
    /// <summary>キーフレーム値を音連動値で置き換える。</summary>
    Override
}

/// <summary>
/// 周波数帯駆動バインド1つ。指定した音声クリップの指定周波数帯の音量で、
/// このクリップの数値パラメータを動かす（設計プラン 4章・フェーズ2）。
/// 例：20-80Hz の音量で拡大率を 1.00〜1.12 の範囲で動かす。
/// </summary>
public sealed class AudioBinding
{
    /// <summary>動かすパラメータ。"Opacity" / "Scale" / "Rotation" / "PositionX" / "PositionY"。</summary>
    public string ParameterName { get; set; } = "Scale";

    /// <summary>音源にするクリップの Id（プロジェクト内の音声つきクリップ）。</summary>
    public string SourceClipId { get; set; } = string.Empty;

    /// <summary>周波数帯の下限（Hz）。例：20。</summary>
    public double LowHz { get; set; } = 20;

    /// <summary>周波数帯の上限（Hz）。例：80。</summary>
    public double HighHz { get; set; } = 80;

    /// <summary>帯域エネルギー 0.0 のときの出力値。</summary>
    public double MinValue { get; set; } = 1.0;

    /// <summary>帯域エネルギー 1.0 のときの出力値。</summary>
    public double MaxValue { get; set; } = 1.12;

    /// <summary>感度。帯域エネルギーに掛ける倍率（0〜10）。1 が等倍。</summary>
    public double Gain { get; set; } = 1.0;

    /// <summary>しきい値。これ以下の帯域エネルギーは 0 とみなす（0〜1）。</summary>
    public double Threshold { get; set; } = 0.0;

    /// <summary>反転する（音が大きいほど出力が小さくなる）。</summary>
    public bool Invert { get; set; }

    /// <summary>キーフレーム値との合成方法。</summary>
    public AudioBindCombine Combine { get; set; } = AudioBindCombine.Add;

    /// <summary>有効か。無効なバインドは無視される。</summary>
    public bool Enabled { get; set; } = true;

    public AudioBinding Clone() => (AudioBinding)MemberwiseClone();

    /// <summary>
    /// 帯域エネルギー（0.0〜1.0）から出力値を計算する。
    /// しきい値・ゲイン・反転を適用してから Min〜Max へ線形写像する。
    /// </summary>
    public double MapValue(double bandEnergy)
    {
        var x = Math.Clamp(bandEnergy, 0, 1);
        if (x < Threshold)
            x = 0;
        else if (Threshold > 0)
            x = (x - Threshold) / (1 - Threshold);
        x = Math.Clamp(x * Gain, 0, 1);
        if (Invert)
            x = 1 - x;
        return MinValue + (MaxValue - MinValue) * x;
    }

    /// <summary>バインドの出力値をキーフレーム値と合成する。</summary>
    public double CombineWith(double keyframeValue, double mappedValue) => Combine switch
    {
        AudioBindCombine.Override => mappedValue,
        AudioBindCombine.Multiply => keyframeValue * mappedValue,
        _ => keyframeValue + (mappedValue - MinValue) // Add：範囲の下限からの変化分だけ加算する。
    };
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

    /// <summary>開始側のトランジション種別（前のクリップとの境界）。既定はなし。</summary>
    public TransitionKind TransitionKind { get; set; } = TransitionKind.None;

    /// <summary>開始側トランジションの長さ（秒）。境界を中心に前後半分ずつ広がる。既定 1.0 秒。</summary>
    public double TransitionDurationSeconds { get; set; } = 1.0;

    // --- テキストクリップ用 ---
    public string Text { get; set; } = string.Empty;
    public string FontFamily { get; set; } = "Yu Gothic UI";
    public double FontSize { get; set; } = 64;
    public string FontColorHex { get; set; } = "#FFFFFFFF";

    // --- 色調補正（フェーズ1：キーフレームなしの固定値。キーフレーム化は後段で） ---
    public double Brightness { get; set; } // -1.0 .. 1.0
    public double Contrast { get; set; } = 1.0; // 0.0 .. 2.0

    /// <summary>ブレンドモード（上のトラックのクリップが下とどう合成されるか）。既定は通常。</summary>
    public BlendMode BlendMode { get; set; } = BlendMode.Normal;

    /// <summary>クリップに付けるエフェクトの一覧（固定値。キーフレーム化と音連動は後段で）。</summary>
    public List<ClipEffect> Effects { get; set; } = new();

    /// <summary>周波数帯駆動バインドの一覧。音声クリップの帯域エネルギーで数値パラメータを動かす（フェーズ2）。</summary>
    public List<AudioBinding> AudioBindings { get; set; } = new();

    /// <summary>ノイズ除去を書き出し時に適用するか（フェーズ3）。プレビューでは反映されない。</summary>
    public bool NoiseReductionEnabled { get; set; }

    /// <summary>ノイズ除去の強さ（0.0〜1.0）。書き出し時の afftdn の nr（dB）に写す。既定 0.5。</summary>
    public double NoiseReductionStrength { get; set; } = 0.5;

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

    /// <summary>分割用に複製する。キーフレームは新しいIdでコピーされる。エフェクトもコピーする（固定値なので分割位置での調整は不要）。</summary>
    public Clip Clone()
    {
        var copy = (Clip)MemberwiseClone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Effects = Effects.Select(e => e.Clone()).ToList();
        copy.AudioBindings = AudioBindings.Select(b => b.Clone()).ToList();
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
