// エフェクト種類ごとの定義（1か所に集約）。
// 表示名・ラベル・既定値・範囲はここだけが持つ。新しい種類を追加するときは
// このテーブルに1行足すだけで、範囲の丸め（ClipEffect.Clamp*）・既定値・
// 表示名・ラベルが揃う。描画本体（FrameRenderer）と XAML の選択肢一覧は
// 別途追加が必要なため、追加時のチェックリストは設計プランの監査まとめを参照。
// EffectKind の enum の順序・数値は .veproj 互換のため変えないこと。
// （数値は旧実装の switch から1文字も変えずに移したもの。）

namespace Editor.Core;

/// <summary>エフェクト1種類の定義。Amount / Radius の意味・範囲・既定値・表示名を1行に集約する。</summary>
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
    double? RadiusMax)
{
    /// <summary>Radius を使う種類か（副パラメータ欄の表示に使う）。</summary>
    public bool UsesRadius => RadiusMin is not null;
}

/// <summary>全エフェクト種類の定義表。種類ごとの知識の正本。</summary>
public static class EffectDefinitions
{
    private static readonly EffectDefinition[] All =
    {
        new(EffectKind.Blur, "ブラー", "半径", string.Empty, 8, 0, 0, 100, null, null),
        new(EffectKind.Glow, "グロー", "強さ", "ぼかし半径", 0.5, 16, 0, 1, 1, 100),
        new(EffectKind.RgbShift, "RGBシフト", "ずれ", string.Empty, 6, 0, 0, 50, null, null),
        new(EffectKind.Slice, "スライス", "ずれ幅", string.Empty, 24, 0, 0, 100, null, null),
        new(EffectKind.Warp, "ワープ（波）", "振幅", "波長", 20, 240, 0, 100, 20, 1000),
        new(EffectKind.ChromaKey, "クロマキー", "しきい値", "なめらかさ", 0.30, 0.15, 0, 1, 0, 1),
        new(EffectKind.Mosaic, "モザイク", "ブロック", string.Empty, 16, 0, 2, 64, null, null),
        new(EffectKind.Vignette, "ビネット", "強さ", "広がり", 0.6, 1.0, 0, 1, 0.5, 2),
        new(EffectKind.Echo, "エコー（残像）", "残りやすさ", "残像の数", 0.6, 4, 0, 1, 1, 8),
        new(EffectKind.Invert, "色反転", "反転量", string.Empty, 1, 0, 0, 1, null, null),
        new(EffectKind.HueShift, "色相", "回転（度）", string.Empty, 60, 0, -180, 180, null, null),
        new(EffectKind.Saturation, "彩度", "倍率", string.Empty, 1.5, 0, 0, 3, null, null),
        new(EffectKind.Monochrome, "モノクロ", "強さ", string.Empty, 1, 0, 0, 1, null, null),
        new(EffectKind.Sepia, "セピア", "強さ", string.Empty, 0.8, 0, 0, 1, null, null),
        new(EffectKind.Posterize, "ポスタリゼーション", "階調数", string.Empty, 4, 0, 2, 16, null, null),
        new(EffectKind.Kaleidoscope, "万華鏡", "ズーム", "分割数", 1, 6, 0.25, 2, 3, 12),
        new(EffectKind.Noise, "ノイズ", "強さ", "粒の大きさ", 0.3, 2, 0, 1, 1, 8),
        new(EffectKind.ChromaticAberration, "色収差", "ずれ", "中心の無影響半径", 4, 0, 0, 20, 0, 0.5),
        new(EffectKind.Fisheye, "魚眼", "歪み", "拡大率", 0.5, 1.15, 0, 1, 1, 2),
        new(EffectKind.VhsGlitch, "VHSグリッチ", "強さ", "走査線の間隔", 0.5, 3, 0, 1, 2, 8),
        new(EffectKind.DropShadow, "ドロップシャドウ", "不透明度", "ぼかし半径", 0.5, 12, 0, 1, 1, 50),
        new(EffectKind.FilmGrain, "フィルムグレイン", "強さ", "粒の大きさ", 0.15, 1, 0, 1, 1, 8),
        new(EffectKind.ZoomBlur, "ズームブラー", "強さ", "サンプル数", 0.5, 8, 0, 1, 4, 16),
        new(EffectKind.MotionBlur, "モーションブラー", "長さ", "方向（度）", 16, 0, 0, 64, 0, 360),
        new(EffectKind.LuminanceKey, "輝度キー", "しきい値", "なめらかさ", 0.5, 0.15, 0, 1, 0, 1),
        new(EffectKind.ToneCurve, "トーンカーブ", "シャドウ", "ハイライト", 0, 0, -1, 1, -1, 1),
    };

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
}
