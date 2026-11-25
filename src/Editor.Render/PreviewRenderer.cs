// プレビュー用レンダラー。FrameRenderer.cs から切り出した。

using Editor.Core;
using SkiaSharp;

namespace Editor.Render;

/// <summary>
/// プレビュー用レンダラー。指定した解像度で描き、直近の結果をキャッシュする。
/// 返すビットマップはこのクラスが所有する（呼び出し側で破棄しないこと）。
/// maxDisplaySize を渡すと、画面に表示される大きさ以上には描かない。
/// フル解像度でも画面が小さければ軽くなる。
/// </summary>
public sealed class PreviewRenderer : IDisposable
{
    private readonly IFrameRenderer _inner;
    private SKBitmap? _lastBitmap;
    private (double TimeSeconds, PreviewResolution Resolution, int Width, int Height)? _lastKey;

    public PreviewRenderer(IFrameRenderer inner)
    {
        _inner = inner;
    }

    public async Task<SKBitmap> RenderPreviewAsync(
        EditorProject project, double timeSeconds, PreviewResolution resolution,
        (int Width, int Height)? maxDisplaySize = null,
        CancellationToken ct = default)
    {
        var (width, height) = resolution.ApplyTo(project.Width, project.Height);
        if (maxDisplaySize.HasValue)
        {
            var scale = Math.Min(1.0, Math.Min(
                maxDisplaySize.Value.Width / (double)width,
                maxDisplaySize.Value.Height / (double)height));
            if (scale < 1.0)
            {
                width = Math.Max(2, (int)(width * scale));
                height = Math.Max(2, (int)(height * scale));
            }
        }

        var key = (timeSeconds, resolution, width, height);
        if (_lastKey == key && _lastBitmap is not null)
            return _lastBitmap;

        var bitmap = await _inner.RenderFrameAsync(project, timeSeconds, width, height, ct)
            .ConfigureAwait(false);

        _lastBitmap?.Dispose();
        _lastBitmap = bitmap;
        _lastKey = key;
        return bitmap;
    }

    /// <summary>
    /// キャッシュを捨てる。プロジェクトが変わったときに呼ぶ（同じ時刻でも描き直すため）。
    /// ビットマップ自体は残す。表示中のコントロールがまだ参照しているため、
    /// ここで破棄すると描画時に破棄済みオブジェクトを触る恐れがある。
    /// 次の描画で置き換えるときに破棄される。
    /// </summary>
    public void Invalidate()
    {
        _lastKey = null;
    }

    public void Dispose()
    {
        _lastBitmap?.Dispose();
        _lastBitmap = null;
    }
}
