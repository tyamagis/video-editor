// プレビュー再生時のフェード／ダッキング包絡の適用（NAudio）。
// 包絡そのものは Core の AudioGainEnvelope がブレークポイント列として持ち、
// ここでは「出力フレーム数 → クリップ相対時刻（タイムライン秒）」を数えて
// サンプルに掛けるだけにする。再生はタイムライン方向に進むため、順再生・
// 速度変更・逆再生のどの経路でも、出力フレーム k のクリップ相対時刻は
// 再生開始位置の相対時刻 + k / サンプルレート で一致する（素材の向きや
// 速度には依存しない。これがフェード長をタイムライン秒で解釈する利点）。
// 静的ゲイン（ノーマライズ）も同時に掛ける（書き出しの volume と同じ倍率）。

using Editor.Core;
using NAudio.Wave;

namespace Editor.Audio;

/// <summary>
/// ISampleProvider に包絡ゲイン（ブレークポイント列）を掛けるプロバイダ。
/// 順再生（等速）で AudioFileReader.Volume の代わりに使う。
/// </summary>
public sealed class EnvelopeSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly IReadOnlyList<GainBreakpoint> _breakpoints;
    private readonly double _startLocalSeconds;
    private readonly float _gain;
    private readonly int _channels;
    private long _framesRead;

    /// <param name="source">入力（包絡を掛ける前の音声）。</param>
    /// <param name="breakpoints">包絡のブレークポイント列（クリップ相対秒）。</param>
    /// <param name="startLocalSeconds">再生開始位置のクリップ相対時刻（秒）。</param>
    /// <param name="gain">静的ゲイン（線形倍率、1.0 が等倍。ノーマライズ用）。</param>
    public EnvelopeSampleProvider(
        ISampleProvider source, IReadOnlyList<GainBreakpoint> breakpoints,
        double startLocalSeconds, double gain = 1.0)
    {
        _source = source;
        _breakpoints = breakpoints;
        _startLocalSeconds = startLocalSeconds;
        _gain = (float)Math.Clamp(gain, 0, AudioNormalizer.MaxGainLinear);
        _channels = Math.Max(1, source.WaveFormat.Channels);
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        var read = _source.Read(buffer, offset, count);
        var sampleRate = Math.Max(1, WaveFormat.SampleRate);
        var frames = read / _channels;
        for (var f = 0; f < frames; f++)
        {
            var local = _startLocalSeconds + (double)(_framesRead + f) / sampleRate;
            var g = _gain * (float)AudioGainEnvelope.EvaluateBreakpoints(_breakpoints, local);
            if (Math.Abs(g - 1.0f) <= 1e-6f)
                continue;
            var baseIndex = offset + f * _channels;
            for (var ch = 0; ch < _channels; ch++)
                buffer[baseIndex + ch] *= g;
        }
        _framesRead += frames;
        return read;
    }
}
