// エコー（残像）用の過去フレーム履歴。
// FrameRenderer.cs から切り出した、TimelineFrameRenderer が所有する状態。
// ClipCompositor と共有する（履歴の更新は描画時に行う）。

using Editor.Core;
using SkiaSharp;

namespace Editor.Render;

/// <summary>エコー（残像）用の過去フレーム履歴。クリップ Id ごとに、縮小コピーを数枚だけ持つ。</summary>
internal sealed class EchoHistory
{
    private readonly Dictionary<string, EchoState> _states = new();

    /// <summary>履歴を持つクリップの数。</summary>
    internal int Count => _states.Count;

    /// <summary>1クリップ分のエコー履歴。</summary>
    private sealed class EchoState
    {
        public Queue<SKBitmap> Frames { get; } = new();
        public double LastTimeSeconds { get; set; } = double.NaN;
    }

    /// <summary>
    /// エコーの残像を返す（古い順）。ついでに現在のフレームを履歴に入れる。
    /// 履歴は縮小コピー（幅最大 960px）で、残像の数だけ持つ。
    /// 再生ヘッドを前に戻したときやエコーを切ったときは履歴を捨てる。
    /// </summary>
    internal IReadOnlyList<(SKBitmap Bitmap, double Alpha)> GetGhosts(
        Clip clip, SKBitmap currentFrame, ClipEffect? echo, EvaluatedClipState state)
    {
        if (echo is null || echo.Amount <= 0)
        {
            if (_states.TryGetValue(clip.Id, out var stale))
            {
                foreach (var old in stale.Frames)
                    old.Dispose();
                _states.Remove(clip.Id);
            }
            return Array.Empty<(SKBitmap, double)>();
        }

        var count = (int)Math.Clamp(Math.Round(echo.Radius), 1, 8);
        if (!_states.TryGetValue(clip.Id, out var echoState))
        {
            echoState = new EchoState();
            _states[clip.Id] = echoState;
        }

        // 再生ヘッドを前に戻したり、大きく前に飛ばしたりしたら履歴は無効（古い残像を出さない）。
        // 通常の再生・書き出しではフレーム間隔は 1/30 秒程度なので、0.5 秒の猶予を持たせる。
        if (!double.IsNaN(echoState.LastTimeSeconds)
            && (state.TimeSeconds < echoState.LastTimeSeconds - 1e-6
                || state.TimeSeconds > echoState.LastTimeSeconds + 0.5))
        {
            foreach (var old in echoState.Frames)
                old.Dispose();
            echoState.Frames.Clear();
        }
        echoState.LastTimeSeconds = state.TimeSeconds;

        // 履歴は古い順に並ぶ。新しい残像ほど濃くする（1フレーム前が Amount、2フレーム前が Amount^2、…）。
        var frames = echoState.Frames.ToArray();
        var ghosts = new List<(SKBitmap, double)>();
        for (var i = 0; i < frames.Length; i++)
        {
            var age = frames.Length - i; // 最も新しい履歴は age=1。
            ghosts.Add((frames[i], state.Opacity * Math.Pow(echo.Amount, age)));
        }

        // 現在のフレームを履歴に入れる（次フレーム以降の残像になる）。
        echoState.Frames.Enqueue(DownscaleForEcho(currentFrame));
        while (echoState.Frames.Count > count)
            echoState.Frames.Dequeue().Dispose();

        return ghosts;
    }

    /// <summary>エコー履歴用にフレームを縮小コピーする（幅最大 960px）。呼び出し側が破棄する元画像とは別物にする。</summary>
    private static SKBitmap DownscaleForEcho(SKBitmap src)
    {
        const int maxWidth = 960;
        if (src.Width <= maxWidth)
            return src.Copy();

        var width = maxWidth;
        var height = Math.Max(1, (int)Math.Round(src.Height * (double)maxWidth / src.Width));
        var dst = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(dst);
        canvas.DrawBitmap(src, new SKRect(0, 0, width, height));
        return dst;
    }

    /// <summary>
    /// このフレームで描かれなかったクリップの履歴を捨てる
    /// （クリップが途切れた後の再登場で古い残像が出ないようにする）。
    /// </summary>
    internal void PruneInactive(IEnumerable<string> activeClipIds)
    {
        var active = new HashSet<string>(activeClipIds);
        foreach (var id in _states.Keys.Where(k => !active.Contains(k)).ToList())
        {
            foreach (var old in _states[id].Frames)
                old.Dispose();
            _states.Remove(id);
        }
    }
}
