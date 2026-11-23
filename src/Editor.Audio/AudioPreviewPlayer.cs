// プレビュー再生時の音声出力（NAudio）。
// 再生ヘッド位置にアクティブな音声つきクリップがあれば、
// その素材の音声を再生ヘッド相当の位置から出す。映像の再生とは
// 独立したクロックで鳴るため、長時間再生すると映像と少し
// ずれる場合がある（フェーズ1の割り切り）。

using NAudio.Wave;

namespace Editor.Audio;

/// <summary>プレビュー用の簡易オーディオプレーヤー。1音源だけ鳴らす。</summary>
public sealed class AudioPreviewPlayer : IDisposable
{
    private IWavePlayer? _output;
    private WaveStream? _reader;

    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;

    /// <summary>指定ファイルの音声を startSeconds から再生する。既存の再生は止める。</summary>
    public void Play(string path, double startSeconds)
    {
        Stop();

        var reader = new AudioFileReader(path);
        var clamped = Math.Clamp(startSeconds, 0, Math.Max(0, reader.TotalTime.TotalSeconds - 0.05));
        reader.CurrentTime = TimeSpan.FromSeconds(clamped);

        var output = new WaveOutEvent();
        try
        {
            output.Init(reader);
            output.Play();
        }
        catch
        {
            output.Dispose();
            reader.Dispose();
            throw;
        }

        _reader = reader;
        _output = output;
    }

    public void Stop()
    {
        try { _output?.Stop(); } catch { /* 終了時の状態は問わない */ }
        _output?.Dispose();
        _output = null;
        _reader?.Dispose();
        _reader = null;
    }

    public void Dispose() => Stop();
}
