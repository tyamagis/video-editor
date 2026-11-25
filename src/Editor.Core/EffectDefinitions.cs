// エフェクト種類ごとの定義（1か所に集約）。
// 表示名・ラベル・既定値・範囲・ヘルプ説明はここだけが持つ。新しい種類を追加するときは
// このテーブルに1行足すだけで、範囲の丸め（ClipEffect.Clamp*）・既定値・
// 表示名・ラベル・ヘルプ説明が揃い、XAML の選択肢一覧と説明文もこのテーブルから
// 自動生成される（ComboBox の ItemsSource と HelpText）。描画本体（ClipCompositor）のみ
// 別途追加が必要なため、追加時のチェックリストは設計プランの監査まとめを参照。
// EffectKind の enum の順序・数値は .veproj 互換のため変えないこと。
// （数値は旧実装の switch から1文字も変えずに移したもの。）
// 第3パラメータ（Third*）は Amount / Radius だけでは足りない種類用。
// トーンカーブの中間調・レベル補正のガンマ・スプリットトーンの着色の強さが使う。
// 使う種類は ThirdMin / ThirdMax を両方指定し、ラベル・既定値・範囲をここに書く。
// AppliesToText はテキストクリップに意味があるか。キー系（背景抜き）は
// テキストの透明背景には意味が無く文字色を消すだけなので false にする。
// false の種類はテキストには付けられず（UIで断る）、描画でも無視される。

namespace Editor.Core;

/// <summary>エフェクト1種類の定義。Amount / Radius / 第3パラメータの意味・範囲・既定値・表示名・ヘルプ説明を1行に集約する。</summary>
public sealed record EffectDefinition(
    EffectKind Kind,
    string DisplayName,
    string AmountLabel,
    string RadiusLabel,
    double DefaultAmount,
    double DefaultRadius,
    double AmountMin,
    double AmountMax,
    double? RadiusMin,
    double? RadiusMax,
    string Description,
    string ThirdLabel = "",
    double DefaultThird = 0,
    double? ThirdMin = null,
    double? ThirdMax = null,
    bool AppliesToText = true)
{
    /// <summary>Radius を使う種類か（副パラメータ欄の表示に使う）。</summary>
    public bool UsesRadius => RadiusMin is not null;

    /// <summary>第3パラメータを使う種類か（トーンカーブの中間調だけ）。</summary>
    public bool UsesThird => ThirdMin is not null;
}

/// <summary>全エフェクト種類の定義表。種類ごとの知識の正本。</summary>
public static class EffectDefinitions
{
    private static readonly EffectDefinition[] All =
    {
        new(EffectKind.Blur, "ブラー", "半径", string.Empty, 8, 0, 0, 100, null, null, "半径(px)"),
        new(EffectKind.Glow, "グロー", "強さ", "ぼかし半径", 0.5, 16, 0, 1, 1, 100, "強さ(0-1)・ぼかし半径(px)"),
        new(EffectKind.RgbShift, "RGBシフト", "ずれ", string.Empty, 6, 0, 0, 50, null, null, "ずれ(px)"),
        new(EffectKind.Slice, "スライス", "ずれ幅", string.Empty, 24, 0, 0, 100, null, null, "ずれ幅(px)"),
        new(EffectKind.Warp, "ワープ（波）", "振幅", "波長", 20, 240, 0, 100, 20, 1000, "振幅(px)・波長(px)"),
        new(EffectKind.ChromaKey, "クロマキー", "しきい値", "なめらかさ", 0.30, 0.15, 0, 1, 0, 1, "しきい値(0-1)・なめらかさ・キー色", AppliesToText: false),
        new(EffectKind.Mosaic, "モザイク", "ブロック", string.Empty, 16, 0, 2, 64, null, null, "ブロック(px)"),
        new(EffectKind.Vignette, "ビネット", "強さ", "広がり", 0.6, 1.0, 0, 1, 0.5, 2, "強さ(0-1)・広がり"),
        new(EffectKind.Echo, "エコー（残像）", "残りやすさ", "残像の数", 0.6, 4, 0, 1, 1, 8, "残りやすさ(0-1)・残像の数"),
        new(EffectKind.Invert, "色反転", "反転量", string.Empty, 1, 0, 0, 1, null, null, "強さ(0-1)"),
        new(EffectKind.HueShift, "色相", "回転（度）", string.Empty, 60, 0, -180, 180, null, null, "回転(度)"),
        new(EffectKind.Saturation, "彩度", "倍率", string.Empty, 1.5, 0, 0, 3, null, null, "倍率"),
        new(EffectKind.Monochrome, "モノクロ", "強さ", string.Empty, 1, 0, 0, 1, null, null, "強さ(0-1)"),
        new(EffectKind.Sepia, "セピア", "強さ", string.Empty, 0.8, 0, 0, 1, null, null, "強さ(0-1)"),
        new(EffectKind.Posterize, "ポスタリゼーション", "階調数", string.Empty, 4, 0, 2, 16, null, null, "階調数"),
        new(EffectKind.Kaleidoscope, "万華鏡", "ズーム", "分割数", 1, 6, 0.25, 2, 3, 12, "ズーム・分割数"),
        new(EffectKind.Noise, "ノイズ", "強さ", "粒の大きさ", 0.3, 2, 0, 1, 1, 8, "強さ(0-1)・粒の大きさ(px)"),
        new(EffectKind.ChromaticAberration, "色収差", "ずれ", "中心の無影響半径", 4, 0, 0, 20, 0, 0.5, "ずれ(px)・中心の無影響半径"),
        new(EffectKind.Fisheye, "魚眼", "歪み", "拡大率", 0.5, 1.15, 0, 1, 1, 2, "歪み(0-1)・拡大率"),
        new(EffectKind.VhsGlitch, "VHSグリッチ", "強さ", "走査線の間隔", 0.5, 3, 0, 1, 2, 8, "強さ(0-1)・走査線の間隔(px)"),
        new(EffectKind.DropShadow, "ドロップシャドウ", "不透明度", "ぼかし半径", 0.5, 12, 0, 1, 1, 50, "不透明度(0-1)・ぼかし半径(px)"),
        new(EffectKind.FilmGrain, "フィルムグレイン", "強さ", "粒の大きさ", 0.15, 1, 0, 1, 1, 8, "強さ(0-1)・粒の大きさ(px)"),
        new(EffectKind.ZoomBlur, "ズームブラー", "強さ", "サンプル数", 0.5, 8, 0, 1, 4, 16, "強さ(0-1)・サンプル数"),
        new(EffectKind.MotionBlur, "モーションブラー", "長さ", "方向（度）", 16, 0, 0, 64, 0, 360, "長さ(px)・方向(度)"),
        new(EffectKind.LuminanceKey, "輝度キー", "しきい値", "なめらかさ", 0.5, 0.15, 0, 1, 0, 1, "しきい値(0-1)・なめらかさ", AppliesToText: false),
        new(EffectKind.ToneCurve, "トーンカーブ", "シャドウ", "ハイライト", 0, 0, -1, 1, -1, 1, "シャドウ・中間調・ハイライト(-1〜1)", "中間調", 0, -1, 1),
        new(EffectKind.Bloom, "ブルーム", "強さ", "ぼかし半径", 0.5, 16, 0, 1, 1, 100, "強さ(0-1)・ぼかし半径(px)"),
        new(EffectKind.LightLeak, "ライトリーク", "強さ", "広がり", 0.5, 1.0, 0, 1, 0.5, 2, "強さ(0-1)・広がり"),
        new(EffectKind.PixelSort, "ピクセルソート", "しきい値", "最大ソート長", 0.5, 64, 0, 1, 8, 256, "しきい値(0-1)・最大ソート長(px)"),
        new(EffectKind.Wave, "ウェーブ", "振幅", "波長", 20, 240, 0, 100, 20, 1000, "振幅(px)・波長(px)"),
        new(EffectKind.Mirror, "ミラー", "位置", "方式", 0.5, 1, 0, 1, 0, 2, "位置(0-1)・方式(0=反転・1=左右分割・2=上下分割)"),
        new(EffectKind.Twirl, "トゥワール", "ねじれ角", "影響半径", 180, 0.6, -360, 360, 0.2, 1, "ねじれ角(度)・影響半径"),
        new(EffectKind.Displacement, "ディスプレイスメント", "強さ", "パターン倍率", 30, 1, 0, 100, 0.5, 4, "強さ(px)・パターン倍率"),
        new(EffectKind.Bulge, "バルジ", "膨らみ", "影響半径", 0.5, 0.6, 0, 1, 0.2, 1, "膨らみ(0-1)・影響半径"),
        new(EffectKind.WaterRipple, "ウォーターリップル", "振幅", "波長", 8, 40, 0, 50, 10, 200, "振幅(px)・波長(px)"),
        new(EffectKind.ColorTemp, "色温度", "温度", string.Empty, 0, 0, -1, 1, null, null, "温度(-1=冷色・+1=暖色)"),
        new(EffectKind.Levels, "レベル補正", "黒点", "白点", 0, 1, 0, 0.5, 0.5, 1, "黒点・白点・ガンマ", "ガンマ", 1, 0.2, 5),
        new(EffectKind.SplitTone, "スプリットトーン", "ハイライト色相", "シャドウ色相", 45, 220, 0, 360, 0, 360, "ハイライト色相・シャドウ色相・強さ", "強さ", 0.5, 0, 1),
        new(EffectKind.GradientMap, "グラデーションマップ", "暗部色相", "明部色相", 220, 45, 0, 360, 0, 360, "暗部色相・明部色相"),
        new(EffectKind.OldFilm, "オールドフィルム", "強さ", "傷の密度", 0.5, 0.5, 0, 1, 0, 1, "強さ(0-1)・傷の密度(0-1)"),
        new(EffectKind.Letterbox, "レターボックス", "帯の高さ", string.Empty, 0.12, 0, 0, 0.25, null, null, "帯の高さ(画面高さに対する割合)"),
        new(EffectKind.Scanlines, "スキャンライン", "線の濃さ", "線の間隔", 0.5, 3, 0, 1, 2, 8, "線の濃さ(0-1)・間隔(px)"),
        new(EffectKind.Halftone, "ハーフトーン", "ドットサイズ", "角度", 6, 45, 2, 16, 0, 90, "ドットサイズ(px)・角度(度)"),
        new(EffectKind.EdgeDetect, "エッジ検出", "強さ", "しきい値", 0.8, 0.15, 0, 1, 0, 1, "強さ(0-1)・しきい値(0-1)"),
        new(EffectKind.Solarize, "ソラリゼーション", "しきい値", string.Empty, 0.5, 0, 0, 1, null, null, "しきい値(0-1)"),
        new(EffectKind.Kuwahara, "油絵風", "カーネル半径", "強さ", 3, 1, 1, 8, 0, 1, "カーネル半径(px)・強さ(0-1)"),
        new(EffectKind.TiltShift, "ティルトシフト", "ぼかし強さ", "合焦帯の幅", 0.5, 0.3, 0, 1, 0.1, 0.8, "ぼかし強さ(0-1)・合焦帯の幅"),
    };

    /// <summary>全種類の定義。XAML のエフェクト選択 ComboBox の ItemsSource に使う（表示順＝この配列の順＝EffectKind の順）。</summary>
    public static IReadOnlyList<EffectDefinition> AllDefinitions => All;

    /// <summary>インスペクタのエフェクト説明文。「表示名：説明」を全種類分つないだもの。表示名・説明ともこのテーブルから生成する（XAML に直書きしない）。</summary>
    public static string HelpText { get; } = BuildHelpText();

    private static string BuildHelpText()
        => string.Join(" ／ ", All.Select(definition => $"{definition.DisplayName}：{definition.Description}"))
            + "。エフェクトの量はキーフレーム欄から時刻で動かせます（エフェクトを付けると選択肢に増えます）。";

    /// <summary>種類の定義を返す。未登録の種類は null（呼び出し側は旧来の既定動作に落とす）。</summary>
    public static EffectDefinition? Get(EffectKind kind)
    {
        foreach (var definition in All)
        {
            if (definition.Kind == kind)
                return definition;
        }
        return null;
    }

    /// <summary>その種類がテキストクリップに意味があるか（未登録の種類は true 扱い）。</summary>
    public static bool AppliesToText(EffectKind kind)
        => Get(kind)?.AppliesToText ?? true;
}
