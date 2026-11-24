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
    Text,
    /// <summary>音の可視化クリップ（スペクトル・波形・リサジュー）。素材ファイルを持たず、Clip.Visualization の設定で描く。</summary>
    Visualization
}

/// <summary>音の可視化の種類（設計プラン 4章）。タイムライン上の可視化クリップが描く内容。</summary>
public enum VisualizationKind
{
    Spectrum,
    Waveform,
    Lissajous
}

/// <summary>
/// 音の可視化クリップの設定（設計プラン 4章）。
/// クリップの Visualization に入り、ClipKind.Visualization のクリップが持つ。
/// 音源は音声クリップを Id で指定し、描画データはバインドと同じ事前解析キャッシュから読む
/// （解析の重複はない）。Gain / LineWidth / WindowSeconds / TrailSeconds は
/// キーフレーム化できる（パラメータ名 "Visualization:Gain" など。トラックが無ければこの固定値）。
/// </summary>
public sealed class VisualizationSettings
{
    public VisualizationKind Kind { get; set; } = VisualizationKind.Spectrum;

    /// <summary>音源にするクリップの Id（プロジェクト内の音声つきクリップ）。</summary>
    public string SourceClipId { get; set; } = string.Empty;

    /// <summary>描画色（"#22D3EE" など）。</summary>
    public string ColorHex { get; set; } = "#22D3EE";

    // --- スペクトル用 ---
    /// <summary>スペクトルの対象帯域の下限（Hz、1〜20000）。</summary>
    public double LowHz { get; set; } = 20;

    /// <summary>スペクトルの対象帯域の上限（Hz、1〜20000）。</summary>
    public double HighHz { get; set; } = 16000;

    /// <summary>スペクトルの本数（4〜128）。</summary>
    public int BandCount { get; set; } = 32;

    /// <summary>スペクトルの帯域を対数間隔で分けるか（false なら線形間隔）。</summary>
    public bool Logarithmic { get; set; } = true;

    // --- 全種共通・波形・リサジュー用 ---
    /// <summary>高さ・振幅のゲイン（0.1〜4。1 が等倍）。スペクトルは棒の高さ、波形・リサジューは振幅に掛ける。</summary>
    public double Gain { get; set; } = 1.0;

    /// <summary>線の太さ（px、0.5〜10）。波形・リサジューで使う。</summary>
    public double LineWidth { get; set; } = 2.0;

    /// <summary>波形の表示区間の長さ（秒、0.02〜1）。直前この長さ分の波形を描く。</summary>
    public double WindowSeconds { get; set; } = 0.1;

    /// <summary>リサジューの残像の長さ（秒、0〜1）。0 で残像なし。直前この長さ分の軌跡を、古いほど薄く描く。</summary>
    public double TrailSeconds { get; set; } = 0.25;

    public VisualizationSettings Clone() => (VisualizationSettings)MemberwiseClone();

    /// <summary>キーフレーム化できる項目のパラメータ名（"Visualization:Gain" など）。</summary>
    public static string KeyframeParameterName(string field) => $"Visualization:{field}";

    /// <summary>キーフレームのパラメータ名 "Visualization:{項目}" を分解する。可視化用でなければ false。</summary>
    public static bool TryParseKeyframeParameter(string parameterName, out string field)
    {
        field = string.Empty;
        const string prefix = "Visualization:";
        if (!parameterName.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        field = parameterName[prefix.Length..];
        return field is "Gain" or "LineWidth" or "WindowSeconds" or "TrailSeconds";
    }

    public static double ClampGain(double value) => Math.Clamp(value, 0.1, 4);
    public static double ClampLineWidth(double value) => Math.Clamp(value, 0.5, 10);
    public static double ClampWindowSeconds(double value) => Math.Clamp(value, 0.02, 1);
    public static double ClampTrailSeconds(double value) => Math.Clamp(value, 0, 1);

    /// <summary>項目ごとのキーフレーム値の範囲に丸める（描画・評価側の最終防衛。UI側でも同じ範囲で丸める）。</summary>
    public static double ClampKeyframeValue(string field, double value) => field switch
    {
        "Gain" => ClampGain(value),
        "LineWidth" => ClampLineWidth(value),
        "WindowSeconds" => ClampWindowSeconds(value),
        "TrailSeconds" => ClampTrailSeconds(value),
        _ => value
    };
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

/// <summary>クリップに付けるエフェクトの種類。数値は .veproj に保存されるため、既存の値の順序は変えず末尾に追加すること。</summary>
public enum EffectKind
{
    Blur,
    Glow,
    RgbShift,
    Slice,
    Warp,
    ChromaKey,
    Mosaic,
    Vignette,
    Echo,
    Invert,
    HueShift,
    Saturation,
    Monochrome,
    Sepia,
    Posterize,
    Kaleidoscope,
    Noise,
    ChromaticAberration,
    Fisheye,
    VhsGlitch,
    DropShadow,
    FilmGrain,
    ZoomBlur,
    MotionBlur,
    LuminanceKey,
    ToneCurve
}

/// <summary>
/// クリップに付けるエフェクト1つ。
/// Amount / Radius はキーフレーム化できる（クリップの KeyframeTracks に
/// パラメータ名 "Effect:{種類}.Amount" / "Effect:{種類}.Radius" のトラックを置く。
/// トラックが無ければこの固定値が使われるため、旧プロジェクトはそのまま動く）。
/// パラメータの意味は種類ごとに決まる。
/// <list type="bullet">
/// <item>Blur：Amount = ぼかし半径（px、0〜100）。Radius・KeyColorHex は使わない。</item>
/// <item>Glow：Amount = 強さ（0〜1）、Radius = ぼかし半径（px、1〜100）。</item>
/// <item>RgbShift：Amount = チャンネルのずれ（px、0〜50）。</item>
/// <item>Slice：Amount = スライスのずれ幅（px、0〜100）。</item>
/// <item>Warp：Amount = 波の振幅（px、0〜100）、Radius = 波長（px、20〜1000）。</item>
/// <item>ChromaKey：Amount = しきい値（0〜1）、Radius = なめらかさ（0〜1）、KeyColorHex = 抜く色。</item>
/// <item>Mosaic：Amount = モザイクのブロックサイズ（px、2〜64。大きいほど粗い）。Radius は使わない。</item>
/// <item>Vignette：Amount = 周囲を暗くする強さ（0〜1。0 で効果なし）、Radius = ビネットの広がり（0.5〜2.0。1.0 で四隅がちょうど暗くなり始める目安。大きいほど明るい中心が広い）。</item>
/// <item>Echo：Amount = 残像の残りやすさ（0〜1。0 で残像なし、1 に近いほど長く残る。1フレームごとの減衰率）、Radius = 残像の数（1〜8。過去フレームを何枚重ねるか）。</item>
/// <item>Invert：Amount = 色反転の量（0〜1。0 で元の色、1 で完全反転）。Radius は使わない。</item>
/// <item>HueShift：Amount = 色相の回転（度、-180〜180。+60 で赤→黄方向へ回る）。Radius は使わない。</item>
/// <item>Saturation：Amount = 彩度の倍率（0〜3。0 でモノクロ、1 で元のまま、2以上で派手な色）。Radius は使わない。</item>
/// <item>Monochrome：Amount = モノクロ化の強さ（0〜1。0 で元の色、1 で完全モノクロ）。Radius は使わない。</item>
/// <item>Sepia：Amount = セピア化の強さ（0〜1。0 で元の色、1 で完全セピア）。Radius は使わない。</item>
/// <item>Posterize：Amount = 階調数（2〜16。チャンネルごとにこの段数に量子化する。小さいほど荒いポスター調）。Radius は使わない。</item>
/// <item>Kaleidoscope：Amount = 中心のズーム（0.25〜2。1 で等倍、大きいほど中心を拡大）、Radius = 分割数（3〜12。円を何分割して鏡像を並べるか）。</item>
/// <item>Noise：Amount = ノイズの強さ（0〜1。0 でノイズなし）、Radius = ノイズの粒の大きさ（px、1〜8。大きいほど粗い粒）。ノイズは時刻で変わる（フィルムグレイン風）。</item>
/// <item>ChromaticAberration：Amount = 画面端でのRGBのずれ（px、0〜20。中心からの距離に比例して広がる放射状のずれ。RGBシフトの一律ずれとは別物）、Radius = ずれが出ない中心の半径（半対角線に対する割合、0〜0.5。0 で中心からずれる）。</item>
/// <item>Fisheye：Amount = 魚眼の歪み（0〜1。0 で歪みなし、1 で強く膨らむ）、Radius = 拡大率（1〜2。歪みで見切れる隅を隠すためのズーム。1 で等倍）。</item>
/// <item>VhsGlitch：Amount = VHS風の劣化の強さ（0〜1。0 で劣化なし。トラッキングずれ・走査線・色のにじみがまとめて強くなる）、Radius = 走査線の間隔（px、2〜8。小さいほど細かい走査線）。</item>
/// <item>DropShadow：Amount = 影の不透明度（0〜1。0 で影なし）、Radius = 影のぼかし半径（px、1〜50）。影は右下へ落ちる（ずれ量は 4 + ぼかし半径×0.3 px、素材上の座標）。</item>
/// <item>FilmGrain：Amount = 粒状感の強さ（0〜1。0 で粒なし。ノイズより弱く上品な振幅）、Radius = 粒の大きさ（px、1〜8。大きいほど粗い粒）。粒は時刻で変わる。</item>
/// <item>ZoomBlur：Amount = 中心から放射状にぼかす強さ（0〜1。0 でぼかしなし）、Radius = サンプル数（4〜16。多いほど滑らかだが重い）。</item>
/// <item>MotionBlur：Amount = 残像方向のぼかしの長さ（px、0〜64。0 でぼかしなし）、Radius = ぼかす方向（度、0〜360。0 で右向き・水平）。</item>
/// <item>LuminanceKey：Amount = 明るさのしきい値（0〜1。しきい値より明るい画素を抜く。白背景の除去など。クロマキーの明暗版）、Radius = なめらかさ（0〜1。しきい値と完全不透明の間の遷移幅）。</item>
/// <item>ToneCurve：簡易版トーンカーブ（中間調はシャドウ・ハイライトから自動補間）。Amount = シャドウ（-1〜1。+で暗部を持ち上げ、-で締める）、Radius = ハイライト（-1〜1。+で明部を持ち上げ、-で抑える）。両方 0 で変化なし。</item>
/// </list>
/// </summary>
public sealed class ClipEffect
{
    public EffectKind Kind { get; set; }
    public double Amount { get; set; }
    public double Radius { get; set; }
    public string KeyColorHex { get; set; } = "#00FF00";

    public ClipEffect Clone() => (ClipEffect)MemberwiseClone();

    /// <summary>エフェクトの Amount / Radius に対応するキーフレームのパラメータ名（"Effect:{種類}.Amount" など）。</summary>
    public static string KeyframeParameterName(EffectKind kind, bool isAmount)
        => $"Effect:{kind}.{(isAmount ? "Amount" : "Radius")}";

    /// <summary>種類ごとの Amount の範囲に丸める（描画・評価側の最終防衛。UI側でも同じ範囲で丸める）。範囲は EffectDefinitions の1テーブルが持つ。</summary>
    public static double ClampAmount(EffectKind kind, double value)
    {
        var definition = EffectDefinitions.Get(kind);
        return definition is null ? value : Math.Clamp(value, definition.AmountMin, definition.AmountMax);
    }

    /// <summary>種類ごとの Radius の範囲に丸める（Radius を使わない種類はそのまま返す）。範囲は EffectDefinitions の1テーブルが持つ。</summary>
    public static double ClampRadius(EffectKind kind, double value)
    {
        var definition = EffectDefinitions.Get(kind);
        return definition?.RadiusMin is null
            ? value
            : Math.Clamp(value, definition.RadiusMin.Value, definition.RadiusMax!.Value);
    }

    /// <summary>Radius を使う種類か（インスペクタの副パラメータ欄の表示に使う）。判定は EffectDefinitions の1テーブルが持つ。</summary>
    public static bool UsesRadius(EffectKind kind)
        => EffectDefinitions.Get(kind)?.UsesRadius ?? false;
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
    /// <summary>動かすパラメータ。"Opacity" / "Scale" / "Rotation" / "PositionX" / "PositionY"、またはエフェクト量 "Effect:{種類}.Amount" / "Effect:{種類}.Radius"（ClipEffect.KeyframeParameterName と同じ名前。エフェクトが付いていない種類へのバインドは無視される）。</summary>
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
/// パラメータ名は "Opacity" / "Scale" / "Rotation" / "PositionX" / "PositionY"、
/// またはエフェクト用 "Effect:{EffectKind}.Amount" / "Effect:{EffectKind}.Radius"。
/// 不変条件：キーフレームは常に時刻の昇順に保つ。追加は AddKeyframe（挿入位置を維持）、
/// 削除は RemoveKeyframe、時刻の直接書き換えの後は SortKeyframes を呼ぶ。
/// 代入（JSON読み込み・分割処理など）の際は並べ替えで正規化する。
/// Evaluate はこの順序を前提とし、呼び出しのたびにソートしない。
/// </summary>
public sealed class KeyframeTrack
{
    public string ParameterName { get; set; } = string.Empty;

    private List<Keyframe> _keyframes = new();

    /// <summary>
    /// キーフレーム列（常に時刻の昇順）。代入時は時刻順に並べ替えて正規化する。
    /// 直接の Add / AddRange は順序を崩すため使わず、AddKeyframe を使うこと。
    /// </summary>
    public List<Keyframe> Keyframes
    {
        get => _keyframes;
        set => _keyframes = (value ?? new List<Keyframe>()).OrderBy(k => k.TimeSeconds).ToList();
    }

    /// <summary>
    /// キーフレームを時刻順の正しい位置に挿入する（安定：同じ時刻があればその後ろに入れる。
    /// 従来 Evaluate 内の OrderBy（安定ソート）と同じ順序になる）。
    /// </summary>
    public void AddKeyframe(Keyframe keyframe)
    {
        var lo = 0;
        var hi = _keyframes.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (_keyframes[mid].TimeSeconds <= keyframe.TimeSeconds)
                lo = mid + 1;
            else
                hi = mid;
        }
        _keyframes.Insert(lo, keyframe);
    }

    /// <summary>キーフレームを削除する（削除だけでは順序は崩れない）。</summary>
    public bool RemoveKeyframe(Keyframe keyframe) => _keyframes.Remove(keyframe);

    /// <summary>
    /// キーフレーム列を時刻順に並べ替える（安定ソート）。キーフレームの時刻を
    /// 直接書き換えた後など、順序が崩れたときに呼ぶ。
    /// </summary>
    public void SortKeyframes()
    {
        if (_keyframes.Count < 2)
            return;
        var sorted = _keyframes.OrderBy(k => k.TimeSeconds).ToList();
        _keyframes.Clear();
        _keyframes.AddRange(sorted);
    }

    /// <summary>クリップ相対時刻における値を補間して返す。キーフレームが時刻順であることを前提とする。</summary>
    public double Evaluate(double timeSeconds, double defaultValue)
    {
        if (Keyframes.Count == 0)
            return defaultValue;

        if (timeSeconds <= Keyframes[0].TimeSeconds)
            return Keyframes[0].Value;

        var last = Keyframes[^1];
        if (timeSeconds >= last.TimeSeconds)
            return last.Value;

        for (var i = 0; i < Keyframes.Count - 1; i++)
        {
            var a = Keyframes[i];
            var b = Keyframes[i + 1];
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

    /// <summary>クリップに付けるエフェクトの一覧。Amount / Radius はキーフレーム化できる（パラメータ名は ClipEffect.KeyframeParameterName を参照）。</summary>
    public List<ClipEffect> Effects { get; set; } = new();

    /// <summary>周波数帯駆動バインドの一覧。音声クリップの帯域エネルギーで数値パラメータを動かす（フェーズ2）。</summary>
    public List<AudioBinding> AudioBindings { get; set; } = new();

    /// <summary>音の可視化クリップの設定。Kind が Visualization のクリップだけが持つ（それ以外は null）。</summary>
    public VisualizationSettings? Visualization { get; set; }

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

    /// <summary>分割用に複製する。キーフレームは新しいIdでコピーされる。エフェクトもコピーする（エフェクトのキーフレームはクリップの KeyframeTracks にあるため、TimelineEditing の分割処理で時刻が調整される）。</summary>
    public Clip Clone()
    {
        var copy = (Clip)MemberwiseClone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Effects = Effects.Select(e => e.Clone()).ToList();
        copy.AudioBindings = AudioBindings.Select(b => b.Clone()).ToList();
        copy.Visualization = Visualization?.Clone();
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

    /// <summary>
    /// Version だけを指定の値にした浅いコピー。シリアライズ用（トラックやクリップの
    /// リストは共有するが、シリアライズは読み取り専用のため安全）。元のプロジェクトは変えない。
    /// ProjectStore.Save / Serialize が Version を書き込むのに使う。
    /// </summary>
    public EditorProject WithVersion(int version)
    {
        var copy = (EditorProject)MemberwiseClone();
        copy.Version = version;
        return copy;
    }
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
