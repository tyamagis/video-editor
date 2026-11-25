// プレビュー再生時の音声出力（NAudio）。
// 再生ヘッド位置にアクティブな音声つきクリップがあれば、
// それら全ての音声を再生ヘッド相当の位置から同時に鳴らす（簡易ミックス）。
// 映像の再生とは独立したクロックで鳴るため、長時間再生すると映像と少し
// ずれる場合がある（フェーズ1の割り切り）。
// 逆再生クリップは、使う区間をデコードして逆向きに読むストリームで鳴らす
// （ReversedAudioStream。WaveOut に逆再生の仕組みは無いため）。

using Editor.Core;
using NAudio.Wave;

namespace Editor.Audio;

/// <summary>プレビューで鳴らす音源1つ。StartSeconds は再生ヘッド位置の素材内時刻。</summary>
/// <param name="Path">素材ファイルへのパス。</param>
/// <param name="StartSeconds">再生ヘッド位置の素材内時刻（秒）。</param>
/// <param name="IsReversed">逆再生するか。</param>
/// <param name="SourceStartSeconds">逆再生時にデコードする区間の開始（イン点、秒）。順再生では使わない。</param>
/// <param name="SourceEndSeconds">逆再生時にデコードする区間の終了（イン点＋長さ、秒）。順再生では使わない。</param>
/// <param name="Gain">ノーマライズなどで決めた線形倍率（1.0 が等倍。書き出しの ffmpeg volume フィルタと同じ倍率）。</param>
public sealed record AudioPreviewSource(
    string Path,
    double StartSeconds,
    bool IsReversed = false,
    double SourceStartSeconds = 0,
    double SourceEndSeconds = 0,
    double Gain = 1.0);

/// <summary>プレビュー用の簡易オーディオプレーヤー。複数音源を同時に鳴らす。</summary>
public sealed class AudioPreviewPlayer : IDisposable
{
    private readonly List<(IWavePlayer Output, WaveStream Reader)> _playing = new();

    public bool IsPlaying => _playing.Any(p => p.Output.PlaybackState == PlaybackState.Playing);

    /// <summary>指定した音源を全て再生ヘッド相当の位置から同時に再生する。既存の再生は止める。</summary>
    public void Play(IReadOnlyList<AudioPreviewSource> sources)
    {
        Stop();

        foreach (var source in sources)
        {
            WaveStream? reader;
            try
            {
                reader = source.IsReversed
                    ? CreateReversedReader(source)
                    : CreateForwardReader(source);
                if (reader is null)
                    continue;
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

    /// <summary>順再生用のリーダー（従来どおり、指定した素材時刻から読む）。ゲインは AudioFileReader.Volume に渡す。</summary>
    private static AudioFileReader CreateForwardReader(AudioPreviewSource source)
    {
        var reader = new AudioFileReader(source.Path);
        var clamped = Math.Clamp(source.StartSeconds, 0, Math.Max(0, reader.TotalTime.TotalSeconds - 0.05));
        reader.CurrentTime = TimeSpan.FromSeconds(clamped);
        // ゲインは AudioNormalizer の上限（+40 dB = 100倍）までに丸める。
        reader.Volume = (float)Math.Clamp(source.Gain, 0, AudioNormalizer.MaxGainLinear);
        return reader;
    }

    /// <summary>
    /// 逆再生用のリーダー。クリップの使用区間だけデコードし、
    /// 再生ヘッド位置の素材時刻から逆向きに読むストリームを作る。
    /// ゲインはデコード済みのサンプルに直接掛ける（DecodeRange は毎回
    /// 新規の配列を返すため、書き換えても他の再生には影響しない）。
    /// 区間が空・デコードできない場合は null（そのクリップは鳴らさない）。
    /// </summary>
    internal static WaveStream? CreateReversedReader(AudioPreviewSource source)
    {
        var (format, samples) = ReversedAudioDecoder.DecodeRange(
            source.Path, source.SourceStartSeconds, source.SourceEndSeconds);
        if (samples.Length == 0)
            return null;
        var gain = Math.Clamp(source.Gain, 0, AudioNormalizer.MaxGainLinear);
        if (Math.Abs(gain - 1.0) > 1e-6)
        {
            var g = (float)gain;
            for (var i = 0; i < samples.Length; i++)
                samples[i] *= g;
        }
        var startFrame = (int)Math.Round(
            (source.StartSeconds - source.SourceStartSeconds) * format.SampleRate);
        return new ReversedAudioStream(format, samples, startFrame);
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
