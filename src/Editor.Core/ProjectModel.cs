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
    private double _lowHz = 20;
    private double _highHz = 16000;
    private double _gain = 1.0;
    private double _lineWidth = 2.0;
    private double _windowSeconds = 0.1;
    private double _trailSeconds = 0.25;

    /// <summary>スペクトルの対象帯域の下限（Hz、1〜20000）。非有限値は既定値に戻す（UIのテキスト入力で "NaN" 等が入っても壊れないように）。</summary>
    public double LowHz
    {
        get => _lowHz;
        set => _lowHz = double.IsFinite(value) ? value : 20;
    }

    /// <summary>スペクトルの対象帯域の上限（Hz、1〜20000）。非有限値は既定値に戻す。</summary>
    public double HighHz
    {
        get => _highHz;
        set => _highHz = double.IsFinite(value) ? value : 16000;
    }

    /// <summary>スペクトルの本数（4〜128）。</summary>
    public int BandCount { get; set; } = 32;

    /// <summary>スペクトルの帯域を対数間隔で分けるか（false なら線形間隔）。</summary>
    public bool Logarithmic { get; set; } = true;

    // --- 全種共通・波形・リサジュー用 ---
    /// <summary>高さ・振幅のゲイン（0.1〜4。1 が等倍）。スペクトルは棒の高さ、波形・リサジューは振幅に掛ける。非有限値は既定値に戻す。</summary>
    public double Gain
    {
        get => _gain;
        set => _gain = double.IsFinite(value) ? value : 1.0;
    }

    /// <summary>線の太さ（px、0.5〜10）。波形・リサジューで使う。非有限値は既定値に戻す。</summary>
    public double LineWidth
    {
        get => _lineWidth;
        set => _lineWidth = double.IsFinite(value) ? value : 2.0;
    }

    /// <summary>波形の表示区間の長さ（秒、0.02〜1）。直前この長さ分の波形を描く。非有限値は既定値に戻す。</summary>
    public double WindowSeconds
    {
        get => _windowSeconds;
        set => _windowSeconds = double.IsFinite(value) ? value : 0.1;
    }

    /// <summary>リサジューの残像の長さ（秒、0〜1）。0 で残像なし。直前この長さ分の軌跡を、古いほど薄く描く。非有限値は既定値に戻す。</summary>
    public double TrailSeconds
    {
        get => _trailSeconds;
        set => _trailSeconds = double.IsFinite(value) ? value : 0.25;
    }

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

    // Math.Clamp は NaN を素通しするため、NaN は各項目の既定値に戻す（Clip.ClampSpeed と同じ流儀）。
    public static double ClampGain(double value) => double.IsNaN(value) ? 1.0 : Math.Clamp(value, 0.1, 4);
    public static double ClampLineWidth(double value) => double.IsNaN(value) ? 2.0 : Math.Clamp(value, 0.5, 10);
    public static double ClampWindowSeconds(double value) => double.IsNaN(value) ? 0.1 : Math.Clamp(value, 0.02, 1);
    public static double ClampTrailSeconds(double value) => double.IsNaN(value) ? 0.25 : Math.Clamp(value, 0, 1);

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
    ToneCurve,
    Bloom,
    LightLeak,
    PixelSort,
    Wave,
    Mirror,
    Twirl,
    Displacement,
    Bulge,
    WaterRipple,
    ColorTemp,
    Levels,
    SplitTone,
    GradientMap,
    OldFilm,
    Letterbox,
    Scanlines,
    Halftone,
    EdgeDetect,
    Solarize,
    Kuwahara,
    TiltShift,
    Sharpen,
    Emboss,
    Lut
}

/// <summary>
/// クリップに付けるエフェクト1つ。
/// Amount / Radius / Midtones はキーフレーム化できる（クリップの KeyframeTracks に
/// パラメータ名 "Effect:{種類}.Amount" / "Effect:{種類}.Radius" / "Effect:{種類}.Midtones" のトラックを置く。
/// トラックが無ければこの固定値が使われるため、旧プロジェクトはそのまま動く）。
/// Midtones はトーンカーブ専用（他の種類では使わない）。
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
/// <item>ToneCurve：トーンカーブ（シャドウ・中間調・ハイライトの3点を独立調整）。Amount = シャドウ（-1〜1。+で暗部を持ち上げ、-で締める）、Midtones = 中間調（-1〜1。+で中間を持ち上げ、-で沈める。黒点・白点は動かない）、Radius = ハイライト（-1〜1。+で明部を持ち上げ、-で抑える）。3つとも 0 で変化なし。旧プロジェクト（Midtones が保存されていない・中間調のトラックが無いもの）は Midtones = 0 として読み込み、Midtones = 0 のときの描画式は旧2点版と同一のため、旧プロジェクトの見た目は変わらない。</item>
/// <item>Bloom：Amount = にじみの強さ（0〜1。0 で効果なし）、Radius = ぼかし半径（px、1〜100）。明るい部分だけを抜いてぼかし、加算で重ねる。</item>
/// <item>LightLeak：Amount = 光漏れの強さ（0〜1。0 で効果なし）、Radius = 漏れの広がり（0.5〜2.0。1.0 が既定の広がり。大きいほど画面の奥まで届く）。画面の端（主に左上）から暖かい光が漏れた色かぶり。</item>
/// <item>PixelSort：Amount = ソートする明るさのしきい値（0〜1。この明るさ以上の画素をソート対象にする。Amount 0 はソートなし＝変化なし）、Radius = 最大ソート長（px、8〜256。1回のソートで流れる最大の長さ）。</item>
/// <item>Wave：Amount = 波の振幅（px、0〜100）、Radius = 波長（px、20〜1000）。x のサインで Y 方向へずらすうねり（Warp が y のサインで X 方向へずらすのに対し、こちらは垂直な方向にずらす）。</item>
/// <item>Mirror：Amount = ミラーの位置（0〜1。分割線の位置。0.5 で中央）、Radius = 方式（0〜2 を四捨五入して使う。0 = 左右反転、1 = 左右分割ミラー、2 = 上下分割ミラー）。</item>
/// <item>Twirl：Amount = ねじれ角（度、-360〜360。+で時計回り）、Radius = 影響半径（画面の半対角線に対する割合、0.2〜1.0。中心からこの半径の外は歪まない）。</item>
/// <item>Displacement：Amount = 歪みの強さ（px、0〜100。0 で歪みなし）、Radius = 歪みパターンの倍率（0.5〜4.0。大きいほど細かい歪み。手続き的なノイズで歪ませる）。</item>
/// <item>Bulge：Amount = 膨らみ（0〜1。0 で膨らみなし、1 で強く膨らむ）、Radius = 影響半径（画面の半対角線に対する割合、0.2〜1.0）。</item>
/// <item>WaterRipple：Amount = 波の振幅（px、0〜50）、Radius = 波長（px、10〜200）。波は時刻で動く（静止画でも揺れる）。</item>
/// <item>ColorTemp：Amount = 色温度（-1〜1。-1 で冷たい青方向、+1 で暖かい橙方向、0 で変化なし）。Radius は使わない。</item>
/// <item>Levels：レベル補正（黒点・白点・ガンマ）。Amount = 黒点（0〜0.5。この明るさ以下を黒に潰す）、Radius = 白点（0.5〜1.0。この明るさ以上を白に飛ばす）、Midtones = ガンマ（0.2〜5.0。1.0 で変化なし。&lt;1 で明るく、&gt;1 で暗く）。</item>
/// <item>SplitTone：スプリットトーン（ハイライトとシャドウに別々の色）。Amount = ハイライトの色相（度、0〜360。既定45 = 暖かい橙）、Radius = シャドウの色相（度、0〜360。既定220 = 冷たい青）、Midtones = 着色の強さ（0〜1。0 で着色なし）。</item>
/// <item>GradientMap：グラデーションマップ（明暗に色を割り当てる）。Amount = 暗部の色相（度、0〜360。既定220 = 青）、Radius = 明部の色相（度、0〜360。既定45 = 橙）。暗い画素ほど暗部色、明るい画素ほど明部色に寄る。</item>
/// <item>OldFilm：Amount = 劣化の強さ（0〜1。0 で劣化なし。傷・ほこり・ちらつきがまとめて強くなる）、Radius = 傷の密度（0〜1。0 で傷なし。多いほど傷・ほこりが増える）。傷とちらつきは時刻で変わる。</item>
/// <item>Letterbox：Amount = 上下の黒帯の高さ（画面高さに対する割合、0〜0.25。既定0.12 = 上下各12%。0 で帯なし）。Radius は使わない。</item>
/// <item>Scanlines：Amount = 線の濃さ（0〜1。0 で線なし、1 で真っ黒な線）、Radius = 線の間隔（px、2〜8。小さいほど細かい走査線）。</item>
/// <item>Halftone：Amount = ドットの最大サイズ（px、2〜16。明るいほど小さいドット。大きいほど粗い）、Radius = スクリーン角度（度、0〜90。既定45）。</item>
/// <item>EdgeDetect：Amount = エッジの強さ（0〜1。0 で効果なし。エッジを白く浮かせ、他を暗くする）、Radius = エッジと判定する勾配のしきい値（0〜1。小さいほど細かいエッジも拾う）。</item>
/// <item>Solarize：Amount = 反転のしきい値（0〜1。この明るさ以上の画素の色を反転する。既定0.5）。Radius は使わない。</item>
/// <item>Kuwahara：Amount = カーネル半径（px、1〜8。大きいほど絵画的だが重い）、Radius = 強さ（0〜1。0 で元の画像、1 で完全な油絵風）。画素ループが重いため 4K では注意。</item>
/// <item>TiltShift：Amount = ぼかしの強さ（0〜1。0 でぼかしなし）、Radius = ピントの合う帯の幅（画面高さに対する割合、0.1〜0.8。既定0.3。帯の中心は画面中央）。</item>
/// <item>Sharpen：Amount = 輪郭強調の強さ（0〜2。0 で変化なし、既定1）。アンシャープマスク（元画像 − ぼかし画像 の差分を Amount 倍して加算）。Radius = ぼかし半径（px、1〜10。既定2。大きいほど太い輪郭が強調される）。</item>
/// <item>Emboss：Amount = 浮き彫りの強さ（0〜1。0 で変化なし、既定0.8。1 で完全なグレースケールの浮き彫り）。Radius = 浮き彫りの距離（px、1〜4。既定1。エンボスカーネルのサンプル間隔＝彫りの深さ）。</item>
/// <item>Lut：3D LUT（.cube）による色変換。Amount = 強さ（0〜1。0 で元の色、1 で完全適用、既定1）。Radius は使わない。LUTファイルは LutPath でパス参照する（プロジェクトには埋め込まない。素材ファイルと同じ扱い）。ファイルが見つからない・読み込めない場合は無効化され、元の色のまま描かれる。</item>
/// </list>
/// </summary>
public sealed class ClipEffect
{
    public EffectKind Kind { get; set; }

    private double _amount;
    private double _radius;
    private double _midtones;

    /// <summary>主パラメータ。非有限値は 0 に戻す（UIのテキスト入力で "NaN" 等が入っても描画へ伝播しないように）。</summary>
    public double Amount
    {
        get => _amount;
        set => _amount = double.IsFinite(value) ? value : 0;
    }

    /// <summary>副パラメータ。非有限値は 0 に戻す。</summary>
    public double Radius
    {
        get => _radius;
        set => _radius = double.IsFinite(value) ? value : 0;
    }

    /// <summary>
    /// 第3パラメータ（-1〜1 などが種類ごとに決まる。0 が変化なし）。
    /// トーンカーブの中間調・レベル補正のガンマ・スプリットトーンの着色の強さが使う。
    /// 他の種類では使わない。
    /// 旧プロジェクトの JSON にはこの項目が無いが、読み込み時に 0 になる
    /// （System.Text.Json は欠けた項目を既定値で補う）ため、そのまま開ける。
    /// </summary>
    public double Midtones
    {
        get => _midtones;
        set => _midtones = double.IsFinite(value) ? value : 0;
    }

    public string KeyColorHex { get; set; } = "#00FF00";

    /// <summary>
    /// LUTエフェクト（EffectKind.Lut）で使う .cube ファイルへのパス。他の種類では使わない。
    /// 素材ファイルと同じくパス参照のみで、プロジェクトファイルには埋め込まない
    /// （LUTは数百KB〜数MBになり得て、プロジェクトJSONが肥大化するため）。
    /// 旧プロジェクトの JSON にはこの項目が無いが、読み込み時に空文字列になる
    /// （System.Text.Json は欠けた項目を既定値で補う）ため、そのまま開ける。
    /// 空・ファイルが無い・パースできない場合は LUT は無効化され、元の色のまま描かれる。
    /// メモリ上では常に絶対パスで持つ。プロジェクトファイルへの保存時だけ、
    /// プロジェクトフォルダ以下にある LUT は相対パスで書かれ、読み込み時に
    /// プロジェクトファイルの場所を基準に絶対パスへ戻される（ProjectStore.Save / Load）。
    /// フォルダの外の LUT と、絶対パスで保存された旧プロジェクトは絶対パスのまま扱う。
    /// </summary>
    public string LutPath { get; set; } = string.Empty;

    public ClipEffect Clone() => (ClipEffect)MemberwiseClone();

    /// <summary>エフェクトの Amount / Radius に対応するキーフレームのパラメータ名（"Effect:{種類}.Amount" など）。</summary>
    public static string KeyframeParameterName(EffectKind kind, bool isAmount)
        => KeyframeParameterName(kind, isAmount ? "Amount" : "Radius");

    /// <summary>エフェクトのパラメータに対応するキーフレームのパラメータ名（"Effect:{種類}.{項目}"。項目は "Amount" / "Radius" / "Midtones"）。</summary>
    public static string KeyframeParameterName(EffectKind kind, string field)
        => $"Effect:{kind}.{field}";

    /// <summary>種類ごとの Amount の範囲に丸める（描画・評価側の最終防衛。UI側でも同じ範囲で丸める）。範囲は EffectDefinitions の1テーブルが持つ。</summary>
    public static double ClampAmount(EffectKind kind, double value)
    {
        var definition = EffectDefinitions.Get(kind);
        // Math.Clamp は NaN を素通しするため、NaN は種類の既定値に戻す。
        return definition is null ? value
            : double.IsNaN(value) ? definition.DefaultAmount
            : Math.Clamp(value, definition.AmountMin, definition.AmountMax);
    }

    /// <summary>種類ごとの Radius の範囲に丸める（Radius を使わない種類はそのまま返す）。範囲は EffectDefinitions の1テーブルが持つ。</summary>
    public static double ClampRadius(EffectKind kind, double value)
    {
        var definition = EffectDefinitions.Get(kind);
        return definition?.RadiusMin is null
            ? value
            : double.IsNaN(value) ? definition.DefaultRadius
            : Math.Clamp(value, definition.RadiusMin.Value, definition.RadiusMax!.Value);
    }

    /// <summary>種類ごとの第3パラメータ（トーンカーブの中間調）の範囲に丸める（第3パラメータを使わない種類はそのまま返す）。範囲は EffectDefinitions の1テーブルが持つ。</summary>
    public static double ClampMidtones(EffectKind kind, double value)
    {
        var definition = EffectDefinitions.Get(kind);
        return definition?.ThirdMin is null
            ? value
            : double.IsNaN(value) ? definition.DefaultThird
            : Math.Clamp(value, definition.ThirdMin.Value, definition.ThirdMax!.Value);
    }

    /// <summary>Radius を使う種類か（インスペクタの副パラメータ欄の表示に使う）。判定は EffectDefinitions の1テーブルが持つ。</summary>
    public static bool UsesRadius(EffectKind kind)
        => EffectDefinitions.Get(kind)?.UsesRadius ?? false;

    /// <summary>第3パラメータ（トーンカーブの中間調）を使う種類か（インスペクタの中間調欄・キーフレーム選択肢の表示に使う）。判定は EffectDefinitions の1テーブルが持つ。</summary>
    public static bool UsesThird(EffectKind kind)
        => EffectDefinitions.Get(kind)?.UsesThird ?? false;
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
    /// <summary>動かすパラメータ。"Opacity" / "Scale" / "Rotation" / "PositionX" / "PositionY"、またはエフェクト量 "Effect:{種類}.Amount" / "Effect:{種類}.Radius" / "Effect:{種類}.Midtones"（ClipEffect.KeyframeParameterName と同じ名前。エフェクトが付いていない種類へのバインドは無視される）。</summary>
    public string ParameterName { get; set; } = "Scale";

    /// <summary>音源にするクリップの Id（プロジェクト内の音声つきクリップ）。</summary>
    public string SourceClipId { get; set; } = string.Empty;

    private double _lowHz = 20;
    private double _highHz = 80;
    private double _minValue = 1.0;
    private double _maxValue = 1.12;
    private double _gain = 1.0;
    private double _threshold = 0.0;

    /// <summary>周波数帯の下限（Hz）。例：20。非有限値は既定値に戻す（UIのテキスト入力で "NaN" 等が入っても MapValue へ伝播しないように）。</summary>
    public double LowHz
    {
        get => _lowHz;
        set => _lowHz = double.IsFinite(value) ? value : 20;
    }

    /// <summary>周波数帯の上限（Hz）。例：80。非有限値は既定値に戻す。</summary>
    public double HighHz
    {
        get => _highHz;
        set => _highHz = double.IsFinite(value) ? value : 80;
    }

    /// <summary>帯域エネルギー 0.0 のときの出力値。非有限値は既定値に戻す。</summary>
    public double MinValue
    {
        get => _minValue;
        set => _minValue = double.IsFinite(value) ? value : 1.0;
    }

    /// <summary>帯域エネルギー 1.0 のときの出力値。非有限値は既定値に戻す。</summary>
    public double MaxValue
    {
        get => _maxValue;
        set => _maxValue = double.IsFinite(value) ? value : 1.12;
    }

    /// <summary>感度。帯域エネルギーに掛ける倍率（0〜10）。1 が等倍。非有限値は既定値に戻す。</summary>
    public double Gain
    {
        get => _gain;
        set => _gain = double.IsFinite(value) ? value : 1.0;
    }

    /// <summary>しきい値。これ以下の帯域エネルギーは 0 とみなす（0〜1）。非有限値は既定値に戻す。</summary>
    public double Threshold
    {
        get => _threshold;
        set => _threshold = double.IsFinite(value) ? value : 0.0;
    }

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
    private double _timeSeconds;
    private double _value;

    /// <summary>クリップ先頭からの相対秒。非有限値は 0 に戻す（並べ替え・補間が NaN で壊れないように）。</summary>
    public double TimeSeconds
    {
        get => _timeSeconds;
        set => _timeSeconds = double.IsFinite(value) ? value : 0;
    }

    /// <summary>値。非有限値は 0 に戻す。</summary>
    public double Value
    {
        get => _value;
        set => _value = double.IsFinite(value) ? value : 0;
    }
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
/// またはエフェクト用 "Effect:{EffectKind}.Amount" / "Effect:{EffectKind}.Radius" / "Effect:{EffectKind}.Midtones"（中間調はトーンカーブのみ）。
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

    /// <summary>
    /// グループの Id（グループ化されたクリップ同士で同じ値。グループ化は ClipGroups.GroupClips が割り当てる）。
    /// null または空は「グループに属さない」。グループはトラックをまたげる
    /// （グループ操作は ClipGroups がプロジェクト全体から同じ Id のクリップを集めて行う）。
    /// 旧プロジェクト（JSON に項目無し）は null として開ける
    /// （System.Text.Json は欠けた項目を既定値で補う）。
    /// 分割（TimelineEditor.SplitClip）では Clone がこの値をコピーするため、
    /// 分割した両片は同じグループに残る（意図した規則。テストで担保する）。
    /// </summary>
    public string? GroupId { get; set; }

    /// <summary>素材ファイルへのパス。テキストクリップでは空。</summary>
    public string SourcePath { get; set; } = string.Empty;

    private double _startSeconds;
    private double _durationSeconds;
    private double _inPointSeconds;
    private double _transitionDurationSeconds = 1.0;
    private double _fontSize = 64;
    private double _brightness;
    private double _contrast = 1.0;
    private double _noiseReductionStrength = 0.5;
    private double _normalizeTargetDb = AudioNormalizer.DefaultTargetDb;

    /// <summary>タイムライン上の開始時刻（秒）。非有限値は 0 に戻す（インスペクタのテキスト欄はこのプロパティへ直接バインドされるため、"NaN" 等が入ってもプロジェクト全体の長さ計算へ伝播しないように）。</summary>
    public double StartSeconds
    {
        get => _startSeconds;
        set => _startSeconds = double.IsFinite(value) ? value : 0;
    }

    /// <summary>タイムライン上での長さ（秒）。非有限値は 0 に戻す。</summary>
    public double DurationSeconds
    {
        get => _durationSeconds;
        set => _durationSeconds = double.IsFinite(value) ? value : 0;
    }

    /// <summary>素材内での開始位置（イン点、秒）。トリミングで変わる。非有限値は 0 に戻す。</summary>
    public double InPointSeconds
    {
        get => _inPointSeconds;
        set => _inPointSeconds = double.IsFinite(value) ? value : 0;
    }

    /// <summary>開始側のトランジション種別（前のクリップとの境界）。既定はなし。</summary>
    public TransitionKind TransitionKind { get; set; } = TransitionKind.None;

    /// <summary>開始側トランジションの長さ（秒）。境界を中心に前後半分ずつ広がる。既定 1.0 秒。非有限値は既定値に戻す（0 以下は評価側で「トランジション無し」として扱う既存の意味を保つ）。</summary>
    public double TransitionDurationSeconds
    {
        get => _transitionDurationSeconds;
        set => _transitionDurationSeconds = double.IsFinite(value) ? value : 1.0;
    }

    // --- テキストクリップ用 ---
    public string Text { get; set; } = string.Empty;
    public string FontFamily { get; set; } = "Yu Gothic UI";
    /// <summary>フォントサイズ。非有限値は既定値 64 に戻す（描画側の TextSize が NaN にならないように）。</summary>
    public double FontSize
    {
        get => _fontSize;
        set => _fontSize = double.IsFinite(value) ? value : 64;
    }
    public string FontColorHex { get; set; } = "#FFFFFFFF";

    /// <summary>テキストクリップのインアニメーション。Kind=None（既定）で無し。旧プロジェクト（JSON に項目無し）は Kind=None として開ける。</summary>
    public TextAnimation AnimationIn { get; set; } = new();

    /// <summary>テキストクリップのアウトアニメーション。Kind=None（既定）で無し。旧プロジェクト（JSON に項目無し）は Kind=None として開ける。</summary>
    public TextAnimation AnimationOut { get; set; } = new();

    // --- 色調補正（フェーズ1：キーフレームなしの固定値。キーフレーム化は後段で） ---
    /// <summary>明るさ（-1.0〜1.0）。非有限値は 0 に戻す。</summary>
    public double Brightness
    {
        get => _brightness;
        set => _brightness = double.IsFinite(value) ? value : 0;
    }

    /// <summary>コントラスト（0.0〜2.0）。非有限値は 1.0 に戻す。</summary>
    public double Contrast
    {
        get => _contrast;
        set => _contrast = double.IsFinite(value) ? value : 1.0;
    }

    /// <summary>ブレンドモード（上のトラックのクリップが下とどう合成されるか）。既定は通常。</summary>
    public BlendMode BlendMode { get; set; } = BlendMode.Normal;

    /// <summary>クリップに付けるエフェクトの一覧。Amount / Radius / Midtones（トーンカーブのみ）はキーフレーム化できる（パラメータ名は ClipEffect.KeyframeParameterName を参照）。</summary>
    public List<ClipEffect> Effects { get; set; } = new();

    /// <summary>周波数帯駆動バインドの一覧。音声クリップの帯域エネルギーで数値パラメータを動かす（フェーズ2）。</summary>
    public List<AudioBinding> AudioBindings { get; set; } = new();

    /// <summary>音の可視化クリップの設定。Kind が Visualization のクリップだけが持つ（それ以外は null）。</summary>
    public VisualizationSettings? Visualization { get; set; }

    /// <summary>ノイズ除去を書き出し時に適用するか（フェーズ3）。プレビューでは反映されない。</summary>
    public bool NoiseReductionEnabled { get; set; }

    /// <summary>ノイズ除去の強さ（0.0〜1.0）。書き出し時の afftdn の nr（dB）に写す。既定 0.5。非有限値は既定値に戻す。</summary>
    public double NoiseReductionStrength
    {
        get => _noiseReductionStrength;
        set => _noiseReductionStrength = double.IsFinite(value) ? value : 0.5;
    }

    /// <summary>音声ノーマライズを適用するか（プレビュー・書き出し共通）。既定は無し。旧プロジェクト（JSON に項目無し）は false として開ける。</summary>
    public bool NormalizeAudioEnabled { get; set; }

    /// <summary>ノーマライズの目標ピークレベル（dBFS、-30〜0）。既定 -1 dBFS。AudioNormalizer.ClampTargetDb で丸める。非有限値は既定値に戻す。</summary>
    public double NormalizeTargetDb
    {
        get => _normalizeTargetDb;
        set => _normalizeTargetDb = double.IsFinite(value) ? value : AudioNormalizer.DefaultTargetDb;
    }

    private double _audioFadeInSeconds;
    private double _audioFadeOutSeconds;

    /// <summary>
    /// 音声フェードインの長さ（タイムライン秒、0〜60。0 で無し）。
    /// タイムライン秒で解釈する（素材秒ではない）ため、速度変更しても
    /// 「タイムラインで何秒かけて聞こえ始めるか」は変わらない。逆再生でも
    /// 向きはタイムライン基準（クリップ先頭がフェードイン）。
    /// 代入時に AudioGainEnvelope.ClampFadeSeconds で丸める。旧プロジェクト
    /// （JSON に項目無し）は 0（フェード無し）として開ける。
    /// </summary>
    public double AudioFadeInSeconds
    {
        get => _audioFadeInSeconds;
        set => _audioFadeInSeconds = AudioGainEnvelope.ClampFadeSeconds(value);
    }

    /// <summary>音声フェードアウトの長さ（タイムライン秒、0〜60。0 で無し）。解釈は AudioFadeInSeconds と同じ。</summary>
    public double AudioFadeOutSeconds
    {
        get => _audioFadeOutSeconds;
        set => _audioFadeOutSeconds = AudioGainEnvelope.ClampFadeSeconds(value);
    }

    /// <summary>
    /// ダッキングにおける役割（なし／対象／トリガー）。1クリップ1役割。
    /// 対象クリップは、プロジェクト内のトリガークリップが鳴っている区間で
    /// 音量が自動で下がる。旧プロジェクト（JSON に項目無し）は None として開ける。
    /// </summary>
    public DuckingRole DuckingRole { get; set; } = DuckingRole.None;

    private double _duckingAmountDb = AudioGainEnvelope.DefaultDuckAmountDb;
    private double _duckingAttackSeconds = AudioGainEnvelope.DefaultAttackSeconds;
    private double _duckingReleaseSeconds = AudioGainEnvelope.DefaultReleaseSeconds;
    private double _duckingThresholdDb = AudioGainEnvelope.DefaultThresholdDb;

    /// <summary>ダッキングで下げる量（dB、0〜30）。対象クリップだけが使う。既定 12 dB。代入時に丸める。</summary>
    public double DuckingAmountDb
    {
        get => _duckingAmountDb;
        set => _duckingAmountDb = AudioGainEnvelope.ClampDuckAmountDb(value);
    }

    /// <summary>ダッキングのアタック（秒、0.005〜2）。トリガーが鳴り始めてから下がりきるまでの速さ。対象クリップだけが使う。既定 0.10 秒。</summary>
    public double DuckingAttackSeconds
    {
        get => _duckingAttackSeconds;
        set => _duckingAttackSeconds = AudioGainEnvelope.ClampAttackSeconds(value);
    }

    /// <summary>ダッキングのリリース（秒、0.005〜5）。トリガーが止んでから戻りきるまでの速さ。対象クリップだけが使う。既定 0.30 秒。</summary>
    public double DuckingReleaseSeconds
    {
        get => _duckingReleaseSeconds;
        set => _duckingReleaseSeconds = AudioGainEnvelope.ClampReleaseSeconds(value);
    }

    /// <summary>トリガー検出のしきい値（dBFS、-90〜0）。この RMS 以上を「トリガーが鳴っている」とみなす。対象クリップ側の設定（トリガーが複数対象を下げる場合、対象ごとに変えられる）。既定 -40 dBFS。</summary>
    public double DuckingThresholdDb
    {
        get => _duckingThresholdDb;
        set => _duckingThresholdDb = AudioGainEnvelope.ClampThresholdDb(value);
    }

    /// <summary>
    /// 逆再生するか。ON のとき、クリップ全体を逆向きに再生する（映像はフレームを逆向きに、
    /// 音声も逆向きに）。旧プロジェクト（JSON に項目無し）は false として開ける
    /// （System.Text.Json は欠けた項目を既定値で補う）。
    /// テキスト・画像・可視化クリップでは意味を持たない（映像・音声クリップが対象）。
    /// </summary>
    public bool IsReversed { get; set; }

    /// <summary>再生速度の最小値（倍率）。これより遅くはしない。</summary>
    public const double MinSpeed = 0.1;

    /// <summary>再生速度の最大値（倍率）。これより速くはしない。</summary>
    public const double MaxSpeed = 4.0;

    private double _speed = 1.0;

    /// <summary>
    /// 再生速度（倍率。1.0 が等速、0.5 がスロー、2.0 が2倍速）。
    /// タイムライン上の長さ（DurationSeconds）は「素材の使用範囲 ÷ 速度」で決まるため、
    /// 速度を変えると長さも変わる（変更は TimelineEditor.ChangeSpeed が素材範囲を保って行う。
    /// このプロパティへの直接代入は長さを変えない）。
    /// 代入時に [MinSpeed, MaxSpeed] へ丸める（NaN は 1.0）。JSON 読み込みもセッターを通るため、
    /// 範囲外の値が保存されていても安全な範囲で開ける。旧プロジェクト（JSON に項目無し）は
    /// 1.0（等速）として開ける（System.Text.Json は欠けた項目を既定値で補う）。
    /// テキスト・画像・可視化クリップでは意味を持たない（映像・音声クリップが対象）。
    /// </summary>
    public double Speed
    {
        get => _speed;
        set => _speed = ClampSpeed(value);
    }

    /// <summary>速度の値を [MinSpeed, MaxSpeed] に丸める（NaN は等速 1.0）。</summary>
    public static double ClampSpeed(double value)
        => double.IsNaN(value) ? 1.0 : Math.Clamp(value, MinSpeed, MaxSpeed);

    /// <summary>素材の使用範囲の長さ（秒）＝ タイムライン上の長さ × 速度。ピーク解析の範囲・書き出しの切り出し範囲など、素材側の長さが要る場所で使う。</summary>
    public double SourceDurationSeconds => DurationSeconds * Speed;

    public List<KeyframeTrack> KeyframeTracks { get; set; } = new();

    public double EndSeconds => StartSeconds + DurationSeconds;

    public bool IsActiveAt(double timeSeconds)
        => timeSeconds >= StartSeconds && timeSeconds < EndSeconds;

    /// <summary>
    /// タイムライン時刻に対応する素材内時刻。経過時間に速度を掛けた分だけ素材が進む。
    /// 順再生なら InPoint + 経過時間×速度、逆再生なら InPoint + 使用範囲 − 経過時間×速度
    /// （クリップ先頭では素材の末尾、クリップ末尾では素材の先頭になる）。
    /// 経過時間は [0, Duration] に丸めるため、トランジション区間などクリップ範囲外の
    /// 時刻でも範囲内の素材時刻を返す。
    /// 呼び出し側でタイムライン時刻がクリップ範囲内かは別に確認すること。
    /// プレビュー描画・書き出し・音声バインド・可視化はすべてこの写像を通るため、
    /// 速度変更と逆再生は両方で同じ結果になる。
    /// </summary>
    public double GetSourceTimeSeconds(double timeSeconds)
    {
        var local = Math.Clamp(timeSeconds - StartSeconds, 0, DurationSeconds);
        return IsReversed
            ? InPointSeconds + SourceDurationSeconds - local * Speed
            : InPointSeconds + local * Speed;
    }

    /// <summary>
    /// 素材内時刻に対応するタイムライン時刻（GetSourceTimeSeconds の逆写像）。
    /// 素材時刻は使用範囲 [InPoint, InPoint + 使用範囲] に丸めてから変換する。
    /// BPM検出の拍・無音区間など、素材側で検出した時刻をタイムラインへ写すのに使う。
    /// </summary>
    public double GetTimelineTimeSeconds(double sourceTimeSeconds)
    {
        var sourceOffset = Math.Clamp(sourceTimeSeconds - InPointSeconds, 0, SourceDurationSeconds);
        return IsReversed
            ? StartSeconds + DurationSeconds - sourceOffset / Speed
            : StartSeconds + sourceOffset / Speed;
    }

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
        copy.AnimationIn = AnimationIn?.Clone() ?? new TextAnimation();
        copy.AnimationOut = AnimationOut?.Clone() ?? new TextAnimation();
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
/// タイムライン上の目印。時刻（秒）と名前を持つ。MV作業で曲の区切り
/// （サビ・ドロップなど）に印を付ける。名前付きであることが用途の要点。
/// </summary>
public sealed class Marker
{
    private double _timeSeconds;

    /// <summary>タイムライン上の時刻（秒）。非有限値は 0 に戻す（並べ替えが NaN で壊れないように）。</summary>
    public double TimeSeconds
    {
        get => _timeSeconds;
        set => _timeSeconds = double.IsFinite(value) ? value : 0;
    }

    /// <summary>マーカーの名前（例：「サビ」「ドロップ」）。</summary>
    public string Name { get; set; } = string.Empty;

    public Marker Clone() => (Marker)MemberwiseClone();
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

    private double _bpm = 120.0;
    private double _exportInSeconds;
    private double _exportOutSeconds;
    private double _beatOffsetSeconds;

    /// <summary>BPM（手動設定。既定120）。拍グリッドの基準になる。非有限値は既定値に戻す（拍グリッド計算 BeatGridCalculator は非有限の BPM で例外になるため）。</summary>
    public double Bpm
    {
        get => _bpm;
        set => _bpm = double.IsFinite(value) ? value : 120.0;
    }

    /// <summary>書き出し範囲の開始（秒）。0 は先頭。非有限値は 0 に戻す。</summary>
    public double ExportInSeconds
    {
        get => _exportInSeconds;
        set => _exportInSeconds = double.IsFinite(value) ? value : 0;
    }

    /// <summary>書き出し範囲の終了（秒）。0 または開始以下は末尾まで。非有限値は 0（末尾まで）に戻す。</summary>
    public double ExportOutSeconds
    {
        get => _exportOutSeconds;
        set => _exportOutSeconds = double.IsFinite(value) ? value : 0;
    }

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

    /// <summary>最初の拍の時刻（秒）。BPMと合わせて拍位置を決める。非有限値は 0 に戻す。</summary>
    public double BeatOffsetSeconds
    {
        get => _beatOffsetSeconds;
        set => _beatOffsetSeconds = double.IsFinite(value) ? value : 0;
    }

    public GridMode GridMode { get; set; } = GridMode.Frame;

    public List<Track> Tracks { get; set; } = new();

    private List<Marker> _markers = new();

    /// <summary>
    /// タイムライン上のマーカー一覧（常に時刻の昇順）。MV作業で曲の区切り
    /// （サビ・ドロップなど）に印を付ける。
    /// 不変条件：マーカーは常に時刻の昇順に保つ。追加は AddMarker（挿入位置を維持）、
    /// 削除は RemoveMarker、時刻の直接書き換えの後は SortMarkers を呼ぶ。
    /// 代入（JSON読み込みなど）の際は並べ替えで正規化する。
    /// 旧プロジェクト（Markers フィールド無し）は空リストとして開ける
    /// （欠けた項目は既定値で補われる）。JSON に null が入っていても空にする。
    /// </summary>
    public List<Marker> Markers
    {
        get => _markers;
        set => _markers = (value ?? new List<Marker>()).OrderBy(m => m.TimeSeconds).ToList();
    }

    /// <summary>
    /// マーカーを時刻順の正しい位置に挿入する（安定：同じ時刻があればその後ろに入れる）。
    /// 負の時刻は 0 に丸める。
    /// </summary>
    public Marker AddMarker(double timeSeconds, string name)
    {
        var marker = new Marker { TimeSeconds = Math.Max(0, timeSeconds), Name = name ?? string.Empty };
        var lo = 0;
        var hi = _markers.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (_markers[mid].TimeSeconds <= marker.TimeSeconds)
                lo = mid + 1;
            else
                hi = mid;
        }
        _markers.Insert(lo, marker);
        return marker;
    }

    /// <summary>マーカーを削除する（削除だけでは順序は崩れない）。</summary>
    public bool RemoveMarker(Marker marker) => _markers.Remove(marker);

    /// <summary>
    /// マーカー列を時刻順に並べ替える（安定ソート）。マーカーの時刻を
    /// 直接書き換えた後など、順序が崩れたときに呼ぶ。
    /// </summary>
    public void SortMarkers()
    {
        if (_markers.Count < 2)
            return;
        var sorted = _markers.OrderBy(m => m.TimeSeconds).ToList();
        _markers.Clear();
        _markers.AddRange(sorted);
    }

    /// <summary>指定時刻より後ろにある最初のマーカー。無ければ null（ちょうど同じ時刻のマーカーは「次」には含めない）。</summary>
    public Marker? NextMarkerAfter(double timeSeconds)
        => _markers.FirstOrDefault(m => m.TimeSeconds > timeSeconds + 1e-6);

    /// <summary>指定時刻より前にある最後のマーカー。無ければ null（ちょうど同じ時刻のマーカーは「前」には含めない）。</summary>
    public Marker? PreviousMarkerBefore(double timeSeconds)
        => _markers.LastOrDefault(m => m.TimeSeconds < timeSeconds - 1e-6);

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
