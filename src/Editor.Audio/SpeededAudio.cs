// プレビュー再生時の速度変更音声（NAudio）。
// 書き出しは ffmpeg の atempo（ピッチ保持）で速度を変えるが、NAudio には
// ピッチ保持のテンポ変更が無い。そのためプレビューでは線形補間のリサンプリングで
// 速度を変える（ピッチも速度に応じて変わる。asetrate と同じ方向）。
// プレビューは確認用という位置づけで、書き出し（atempo・ピッチ保持）が正とする。
// 判断の詳細は Clip.Speed の設計（作業報告）に記録した。
// 逆再生クリップの速度変更は ReversedAudioStream のステップ幅で対応する
// （デコード済みサンプルを速度分だけ飛ばし／重ねて読む。こちらもピッチは変わる）。

using NAudio.Wave;

namespace Editor.Audio;

/// <summary>
/// ISampleProvider（IEEE float）を IWaveProvider として WaveOut へ渡すための薄い変換。
/// float サンプルをそのままバイト列にするだけ（形式変換はしない）。
/// </summary>
public sealed class FloatWaveProvider : IWaveProvider
{
    private readonly ISampleProvider _source;
    private float[] _floatBuffer = Array.Empty<float>();

    public FloatWaveProvider(ISampleProvider source) => _source = source;

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(byte[] buffer, int offset, int count)
    {
        var sampleCount = count / 4;
        if (_floatBuffer.Length < sampleCount)
            _floatBuffer = new float[sampleCount];
        var read = _source.Read(_floatBuffer, 0, sampleCount);
        var span = buffer.AsSpan(offset, read * 4);
        for (var i = 0; i < read; i++)
            BitConverter.TryWriteBytes(span.Slice(i * 4, 4), _floatBuffer[i]);
        return read * 4;
    }
}

/// <summary>
/// 順再生の音声をリサンプリングで速度変更するサンプルプロバイダ。
/// 出力フレーム n は入力フレーム n×速度 の位置を線形補間で読む。
/// ゲインも同時に掛ける（ノーマライズ用。AudioFileReader.Volume に頼らず
/// ここで掛けることで、速度変更時も書き出しと同じ倍率になる）。
/// </summary>
public sealed class TempoSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly double _speed;
    private readonly float _gain;
    private readonly int _channels;
    private readonly float[] _readBuffer;
    private readonly IReadOnlyList<Editor.Core.GainBreakpoint>? _envelope;
    private readonly double _envelopeStartLocalSeconds;
    private long _outputFrames;

    /// <summary>入力サンプル（インターリーブ）。先頭は _bufferStartFrame に対応する。</summary>
    private readonly List<float> _buffer = new();

    /// <summary>_buffer の先頭が入力の何フレーム目か（このプロバイダが読み始めた位置を 0 とする）。</summary>
    private long _bufferStartFrame;

    /// <summary>次に出力するフレームの入力側位置（フレーム単位・小数。速度ずつ進む）。</summary>
    private double _nextSourceFrame;

    private bool _sourceExhausted;

    /// <param name="source">入力（速度変更前の音声）。</param>
    /// <param name="speed">速度（倍率）。範囲外は Clip.ClampSpeed で丸める。</param>
    /// <param name="gain">線形倍率（1.0 が等倍）。</param>
    public TempoSampleProvider(
        ISampleProvider source, double speed, double gain = 1.0,
        IReadOnlyList<Editor.Core.GainBreakpoint>? envelope = null, double envelopeStartLocalSeconds = 0)
    {
        _source = source;
        _speed = Editor.Core.Clip.ClampSpeed(speed);
        _gain = (float)Math.Clamp(gain, 0, Editor.Core.AudioNormalizer.MaxGainLinear);
        _channels = Math.Max(1, source.WaveFormat.Channels);
        _readBuffer = new float[4096 * _channels];
        _envelope = envelope;
        _envelopeStartLocalSeconds = envelopeStartLocalSeconds;
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    private long BufferedFrames => _buffer.Count / _channels;

    /// <summary>速度変更したサンプルを書く。入力を読み切ったら書けた分だけ返す（0 で終了）。</summary>
    public int Read(float[] buffer, int offset, int count)
    {
        var framesWanted = count / _channels;
        var written = 0;
        while (written < framesWanted)
        {
            var frameIndex = (long)Math.Floor(_nextSourceFrame);
            // 補間に要る2フレーム（現在位置とその次）まで読み込む。
            EnsureBuffered(frameIndex + 1);
            var available = _bufferStartFrame + BufferedFrames;
            if (frameIndex >= available)
                break; // 入力を読み切った。

            var frac = (float)(_nextSourceFrame - frameIndex);
            var a = (int)(frameIndex - _bufferStartFrame) * _channels;
            var hasNext = frameIndex + 1 < available;
            var b = hasNext ? a + _channels : a;
            // 包絡は出力フレーム位置（タイムライン時刻）で評価する。速度で
            // 素材の進み方は変わっても、聞こえる時刻は出力側で数える。
            var frameGain = _gain;
            if (_envelope is not null)
            {
                var local = _envelopeStartLocalSeconds
                    + (double)(_outputFrames + written) / Math.Max(1, WaveFormat.SampleRate);
                frameGain *= (float)Editor.Core.AudioGainEnvelope.EvaluateBreakpoints(_envelope, local);
            }
            for (var ch = 0; ch < _channels; ch++)
            {
                var value = _buffer[a + ch] + (_buffer[b + ch] - _buffer[a + ch]) * frac;
                buffer[offset + written * _channels + ch] = value * frameGain;
            }
            _nextSourceFrame += _speed;
            written++;

            // 消費済みの入力フレームはときどき捨てる（長時間再生でバッファが育ち続けないように）。
            if (frameIndex - _bufferStartFrame > 8192)
                DropConsumed(frameIndex);
        }
        _outputFrames += written;
        return written * _channels;
    }

    /// <summary>入力フレーム lastFrameNeeded まで（無ければ読み切るまで）バッファへ読み込む。</summary>
    private void EnsureBuffered(long lastFrameNeeded)
    {
        while (!_sourceExhausted && _bufferStartFrame + BufferedFrames <= lastFrameNeeded)
        {
            var read = _source.Read(_readBuffer, 0, _readBuffer.Length);
            if (read <= 0)
            {
                _sourceExhausted = true;
                break;
            }
            _buffer.AddRange(_readBuffer.AsSpan(0, read).ToArray());
        }
    }

    /// <summary>upToFrame の手前までの入力フレームをバッファから捨てる。</summary>
    private void DropConsumed(long upToFrame)
    {
        var frames = (int)Math.Min(upToFrame - _bufferStartFrame, BufferedFrames);
        if (frames <= 0)
            return;
        _buffer.RemoveRange(0, frames * _channels);
        _bufferStartFrame += frames;
    }
}
