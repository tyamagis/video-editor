// プレビュー再生時の逆再生音声（NAudio）。
// NAudio の WaveOut は前向き再生しかできないため、逆再生クリップは
// 使う区間だけデコードしてメモリに載せ、逆向きに読むストリームで鳴らす。
// 逆向きに読む＝再生ヘッド位置の素材時刻から、素材の先頭側へ戻りながら鳴らす。
// 長いクリップほどデコード量・メモリが増える（例：3分のステレオ 44.1kHz float で約 60MB）。
// そのためデコードするのはクリップの使用区間（イン点〜イン点＋長さ）だけにする。

using NAudio.Wave;

namespace Editor.Audio;

/// <summary>デコード済みサンプル（float・インターリーブ）を逆向きに読むストリーム。</summary>
public sealed class ReversedAudioStream : WaveStream
{
    private readonly float[] _samples;
    private readonly int _channels;
    private readonly int _startFrame; // 最初に返すフレーム（再生ヘッド位置の素材時刻に対応する区間内フレーム）。
    private int _nextFrame; // 次に返すフレーム（_startFrame から 0 へ向かって減っていく）。
    private long _positionBytes;

    public override WaveFormat WaveFormat { get; }

    /// <param name="waveFormat">デコード済みサンプルの形式（IEEE float）。</param>
    /// <param name="samples">区間内のサンプル（float・インターリーブ、順向きの並び）。</param>
    /// <param name="startFrame">最初に返すフレームの区間内インデックス（0〜総フレーム数-1）。範囲外は丸める。</param>
    public ReversedAudioStream(WaveFormat waveFormat, float[] samples, int startFrame)
    {
        WaveFormat = waveFormat;
        _samples = samples;
        _channels = Math.Max(1, waveFormat.Channels);
        var totalFrames = samples.Length / _channels;
        _startFrame = totalFrames <= 0 ? -1 : Math.Clamp(startFrame, 0, totalFrames - 1);
        _nextFrame = _startFrame;
        Length = _startFrame < 0 ? 0 : (long)(_startFrame + 1) * BlockAlign;
    }

    public override long Length { get; }

    public override long Position
    {
        get => _positionBytes;
        set
        {
            // バイト位置から読み進めたフレーム数を求め、次に返すフレームを決める。
            // WaveOut は順次読むだけだが、シークされても辻褄が合うようにする。
            var framesRead = (int)Math.Clamp(value / BlockAlign, 0, _startFrame + 1);
            _nextFrame = _startFrame - framesRead;
            _positionBytes = (long)framesRead * BlockAlign;
        }
    }

    /// <summary>逆向きにフレームを読む。読み切ったら 0 を返す。</summary>
    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_nextFrame < 0)
            return 0;
        var framesWanted = count / BlockAlign;
        var framesToRead = Math.Min(framesWanted, _nextFrame + 1);
        if (framesToRead <= 0)
            return 0;

        var span = buffer.AsSpan(offset, framesToRead * BlockAlign);
        for (var i = 0; i < framesToRead; i++)
        {
            var frame = _nextFrame - i;
            for (var ch = 0; ch < _channels; ch++)
            {
                var sample = _samples[frame * _channels + ch];
                // IEEE float（4バイト）として書き出す。
                BitConverter.TryWriteBytes(span.Slice((i * _channels + ch) * 4, 4), sample);
            }
        }

        _nextFrame -= framesToRead;
        var bytesRead = framesToRead * BlockAlign;
        _positionBytes += bytesRead;
        return bytesRead;
    }

    /// <summary>このストリームが最後まで読み切ったか（再生終了の目安）。</summary>
    public bool IsExhausted => _nextFrame < 0;
}

/// <summary>逆再生のための音声デコード。クリップの使用区間だけをデコードする。</summary>
public static class ReversedAudioDecoder
{
    /// <summary>
    /// 素材の [startSeconds, endSeconds) をデコードして float サンプルで返す。
    /// 形式は AudioFileReader の変換後（IEEE float、元のチャンネル数）。
    /// endSeconds が素材の長さを超えていれば末尾まで。区間が空なら空配列。
    /// </summary>
    public static (WaveFormat Format, float[] Samples) DecodeRange(
        string path, double startSeconds, double endSeconds)
    {
        using var reader = new AudioFileReader(path);
        var sampleProvider = reader.ToSampleProvider();
        var format = sampleProvider.WaveFormat;
        var sampleRate = format.SampleRate;
        var channels = format.Channels;

        var clampedStart = Math.Clamp(startSeconds, 0, reader.TotalTime.TotalSeconds);
        var clampedEnd = Math.Clamp(endSeconds, clampedStart, reader.TotalTime.TotalSeconds);
        var maxFrames = (int)Math.Max(0, Math.Round((clampedEnd - clampedStart) * sampleRate));
        if (maxFrames <= 0)
            return (format, Array.Empty<float>());

        reader.CurrentTime = TimeSpan.FromSeconds(clampedStart);
        var samples = new List<float>(maxFrames * channels);
        var buffer = new float[8192 * channels];
        var framesRead = 0;
        while (framesRead < maxFrames)
        {
            var framesWanted = Math.Min(buffer.Length / channels, maxFrames - framesRead);
            var read = sampleProvider.Read(buffer, 0, framesWanted * channels);
            if (read <= 0)
                break;
            samples.AddRange(buffer.AsSpan(0, read).ToArray());
            framesRead += read / channels;
        }
        return (format, samples.ToArray());
    }
}
