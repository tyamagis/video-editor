// プレビュー再生時の音声出力（NAudio）。
// 再生ヘッド位置にアクティブな音声つきクリップがあれば、
// それら全ての音声を再生ヘッド相当の位置から同時に鳴らす（簡易ミックス）。
// 映像の再生とは独立したクロックで鳴るため、長時間再生すると映像と少し
// ずれる場合がある（フェーズ1の割り切り）。

using NAudio.Wave;

namespace Editor.Audio;

/// <summary>プレビュー用の簡易オーディオプレーヤー。複数音源を同時に鳴らす。</summary>
public sealed class AudioPreviewPlayer : IDisposable
{
    private readonly List<(IWavePlayer Output, WaveStream Reader)> _playing = new();

    public bool IsPlaying => _playing.Any(p => p.Output.PlaybackState == PlaybackState.Playing);

    /// <summary>指定した音源を全て startSeconds から同時に再生する。既存の再生は止める。</summary>
    public void Play(IReadOnlyList<(string Path, double StartSeconds)> sources)
    {
        Stop();

        foreach (var (path, startSeconds) in sources)
        {
            AudioFileReader reader;
            try
            {
                reader = new AudioFileReader(path);
                var clamped = Math.Clamp(startSeconds, 0, Math.Max(0, reader.TotalTime.TotalSeconds - 0.05));
                reader.CurrentTime = TimeSpan.FromSeconds(clamped);
            }
            catch
            {
                continue; // 読めない素材は飛ばして他を鳴らす。
            }

            var output = new WaveOutEvent();
            try
            {
                output.Init(reader);
                output.Play();
                _playing.Add((output, reader));
            }
            catch
            {
                output.Dispose();
                reader.Dispose();
            }
        }
    }

    public void Stop()
    {
        foreach (var (output, reader) in _playing)
        {
            try { output.Stop(); } catch { /* 終了時の状態は問わない */ }
            output.Dispose();
            reader.Dispose();
        }
        _playing.Clear();
    }

    public void Dispose() => Stop();
}
