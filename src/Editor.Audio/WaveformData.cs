// 波形データ（設計プラン 4章）。
// タイムラインの波形表示は、区間ごとの最大・最小振幅だけを使う。
// 全サンプルを保持しないため、長時間素材でも表示が重くならない。

using NAudio.Wave;

namespace Editor.Audio;

/// <summary>波形表示用のピーク列。1バケット＝表示上の1区間。</summary>
public sealed class WaveformData
{
    public required string SourcePath { get; init; }
    public required int SampleRate { get; init; }
    public required double DurationSeconds { get; init; }
    public required float[] MinPeaks { get; init; }
    public required float[] MaxPeaks { get; init; }

    public int BucketCount => MinPeaks.Length;
}

/// <summary>NAudio で音声を読み、バケットごとの最大・最小振幅を作る。</summary>
public sealed class WaveformReader
{
    /// <summary>
    /// 音声ファイルから波形ピークを読む。ストリーミングで処理するため
    /// 長時間素材でも全サンプルをメモリに載せない。
    /// </summary>
    public WaveformData Read(string path, int bucketCount = 2000)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("音声ファイルが見つかりません。", path);
        if (bucketCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(bucketCount));

        using var reader = new AudioFileReader(path);
        var channels = reader.WaveFormat.Channels;
        var sampleRate = reader.WaveFormat.SampleRate;

        // AudioFileReader は32bit floatに変換済み。Lengthはバイト数。
        var totalSamples = reader.Length / 4 / channels;
        var min = new float[bucketCount];
        var max = new float[bucketCount];
        Array.Fill(min, 1f);
        Array.Fill(max, -1f);

        var buffer = new float[4096 * channels];
        long sampleIndex = 0;
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            var frames = read / channels;
            for (var f = 0; f < frames; f++)
            {
                float mono = 0;
                for (var c = 0; c < channels; c++)
                    mono += buffer[f * channels + c];
                mono /= channels;

                var bucket = totalSamples <= 0
                    ? 0
                    : (int)(sampleIndex * bucketCount / totalSamples);
                if (bucket >= bucketCount)
                    bucket = bucketCount - 1;

                if (mono < min[bucket]) min[bucket] = mono;
                if (mono > max[bucket]) max[bucket] = mono;
                sampleIndex++;
            }
        }

        return new WaveformData
        {
            SourcePath = path,
            SampleRate = sampleRate,
            DurationSeconds = sampleRate > 0 ? (double)totalSamples / sampleRate : 0,
            MinPeaks = min,
            MaxPeaks = max
        };
    }
}
