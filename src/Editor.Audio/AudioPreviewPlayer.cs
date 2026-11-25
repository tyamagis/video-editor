// プレビュー再生時の音声出力（NAudio）。
// 再生ヘッド位置にアクティブな音声つきクリップがあれば、
// それら全ての音声を再生ヘッド相当の位置から同時に鳴らす（簡易ミックス）。
// 映像の再生とは独立したクロックで鳴るため、長時間再生すると映像と少し
// ずれる場合がある（フェーズ1の割り切り）。
// 逆再生クリップは、使う区間をデコードして逆向きに読むストリームで鳴らす
// （ReversedAudioStream。WaveOut に逆再生の仕組みは無いため）。
// 速度変更クリップは、順再生は TempoSampleProvider（リサンプリング）、
// 逆再生は ReversedAudioStream のステップ幅で速度を変えて鳴らす。
// どちらもピッチが変わる（書き出しの atempo はピッチ保持。プレビューは確認用）。

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
/// <param name="Speed">再生速度（倍率、1.0 が等速）。プレビューではリサンプリングで速度を変えるためピッチも変わる（書き出しは atempo でピッチ保持。プレビューは確認用で書き出しが正）。</param>
public sealed record AudioPreviewSource(
    string Path,
    double StartSeconds,
    bool IsReversed = false,
    double SourceStartSeconds = 0,
    double SourceEndSeconds = 0,
    double Gain = 1.0,
    double Speed = 1.0,
    IReadOnlyList<GainBreakpoint>? GainEnvelope = null,
    double EnvelopeStartLocalSeconds = 0);

/// <summary>プレビュー用の簡易オーディオプレーヤー。複数音源を同時に鳴らす。</summary>
public sealed class AudioPreviewPlayer : IDisposable
{
    private readonly List<(IWavePlayer Output, IDisposable Reader)> _playing = new();

    public bool IsPlaying => _playing.Any(p => p.Output.PlaybackState == PlaybackState.Playing);

    /// <summary>指定した音源を全て再生ヘッド相当の位置から同時に再生する。既存の再生は止める。</summary>
    public void Play(IReadOnlyList<AudioPreviewSource> sources)
    {
        Stop();

        foreach (var source in sources)
        {
            IWaveProvider? provider;
            IDisposable? disposable;
            try
            {
                if (source.IsReversed)
                {
                    var reader = CreateReversedReader(source);
                    provider = reader;
                    disposable = reader;
                }
                else if (Math.Abs(Clip.ClampSpeed(source.Speed) - 1.0) > 1e-9)
                {
                    (provider, disposable) = CreateSpeededReader(source);
                }
                else if (source.GainEnvelope is not null)
                {
                    (provider, disposable) = CreateEnvelopedReader(source);
                }
                else
                {
                    var reader = CreateForwardReader(source);
                    provider = reader;
                    disposable = reader;
                }
                if (provider is null || disposable is null)
                    continue;
            }
            catch
            {
                continue; // 読めない素材は飛ばして他を鳴らす。
            }

            var output = new WaveOutEvent();
            try
            {
                output.Init(provider);
                output.Play();
                _playing.Add((output, disposable));
            }
            catch
            {
                output.Dispose();
                disposable.Dispose();
            }
        }
    }

    /// <summary>
    /// 速度変更ありの順再生用リーダー。AudioFileReader を TempoSampleProvider
    /// （線形補間のリサンプリング。ピッチも変わる）で包んで返す。
    /// ゲインはプロバイダ側で掛ける（書き出しの volume フィルタと同じ倍率）。
    /// </summary>
    internal static (IWaveProvider Provider, AudioFileReader Reader) CreateSpeededReader(
        AudioPreviewSource source)
    {
        var reader = new AudioFileReader(source.Path);
        var clamped = Math.Clamp(source.StartSeconds, 0, Math.Max(0, reader.TotalTime.TotalSeconds - 0.05));
        reader.CurrentTime = TimeSpan.FromSeconds(clamped);
        var provider = new TempoSampleProvider(
            reader.ToSampleProvider(), Clip.ClampSpeed(source.Speed), source.Gain,
            source.GainEnvelope, source.EnvelopeStartLocalSeconds);
        return (new FloatWaveProvider(provider), reader);
    }

    /// <summary>
    /// 包絡ありの等速順再生用リーダー。AudioFileReader.Volume（静的）ではなく
    /// EnvelopeSampleProvider で時刻変化するゲインを掛ける（静的ゲインも合成）。
    /// </summary>
    internal static (IWaveProvider Provider, AudioFileReader Reader) CreateEnvelopedReader(
        AudioPreviewSource source)
    {
        var reader = new AudioFileReader(source.Path);
        var clamped = Math.Clamp(source.StartSeconds, 0, Math.Max(0, reader.TotalTime.TotalSeconds - 0.05));
        reader.CurrentTime = TimeSpan.FromSeconds(clamped);
        var provider = new EnvelopeSampleProvider(
            reader.ToSampleProvider(), source.GainEnvelope!, source.EnvelopeStartLocalSeconds, source.Gain);
        return (new FloatWaveProvider(provider), reader);
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
        // 速度変更はステップ幅で対応する（素材フレームを速度分だけ飛ばし／重ねて読む）。
        // 包絡はストリーム側で出力フレーム位置（タイムライン時刻）ごとに掛ける。
        return new ReversedAudioStream(format, samples, startFrame, Clip.ClampSpeed(source.Speed),
            source.GainEnvelope, source.EnvelopeStartLocalSeconds);
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
