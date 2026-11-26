// MainViewModel の分割ファイル（partial）。素材の読み込み、タイムラインへの配置、テキストクリップ追加、歌詞インポート。

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Threading;
using Editor.Audio;
using Editor.Core;
using Editor.Media;
using Editor.Render;
using SkiaSharp;

namespace Editor.App;

public sealed partial class MainViewModel
{
    /// <summary>素材を読み込み、プールに追加する。音声つき素材は波形も作る。</summary>
    public async Task AddMediaAsync(string path)
    {
        var info = await _mediaProbe.ProbeAsync(path).ConfigureAwait(true);
        var item = new MediaItem { SourcePath = path, Info = info };
        MediaItems.Add(item);

        if (info.HasAudio)
        {
            // 波形もキャッシュ対象。プロジェクトのキャッシュフォルダが決まっていれば
            // ディスクキャッシュを使い、無ければ読み直す（2回目の読み込みが速くなる）。
            var waveform = await Task.Run(() => _waveformCache.GetOrRead(path)).ConfigureAwait(true);
            Waveforms[path] = waveform;
        }

        StatusMessage = $"読み込みました：{item.Name}";
    }

    /// <summary>選択中の素材をタイムラインに配置する。開始時刻を省略すると末尾に置く。</summary>
    public void AddMediaToTimeline(MediaItem item) => AddMediaToTimeline(item, null);

    public void AddMediaToTimeline(MediaItem item, double? startSeconds)
    {
        BeginEdit();
        var kind = item.Info.HasVideo ? TrackKind.Video : TrackKind.Audio;
        var track = Project.Tracks.FirstOrDefault(t => t.Kind == kind)
            ?? Project.AddTrack(kind, kind == TrackKind.Video ? "ビデオ 1" : "オーディオ 1");

        var duration = item.Info.Duration.TotalSeconds;
        if (duration <= 0)
            duration = 5; // 長さが取れなかった素材は5秒で置く（幅0で見えなくなるのを防ぐ）

        var clip = new Clip
        {
            Name = item.Name,
            Kind = item.Info.HasVideo ? ClipKind.Video : ClipKind.Audio,
            SourcePath = item.SourcePath,
            StartSeconds = startSeconds ?? track.Clips.Select(c => c.EndSeconds).DefaultIfEmpty(0).Max(),
            DurationSeconds = duration,
            InPointSeconds = 0
        };
        track.Clips.Add(clip);
        SelectedClip = clip;
        NotifyTimelineChanged();
        StatusMessage = $"タイムラインに追加しました：{item.Name}";
    }

    /// <summary>テキストクリップを再生ヘッド位置に追加する。</summary>
    public void AddTextClip(string text)
    {
        BeginEdit();
        var track = Project.Tracks.FirstOrDefault(t => t.Kind == TrackKind.Text)
            ?? Project.AddTrack(TrackKind.Text, "テキスト 1");

        var clip = new Clip
        {
            Name = text,
            Kind = ClipKind.Text,
            Text = text,
            StartSeconds = PlayheadSeconds,
            DurationSeconds = 5
        };
        track.Clips.Add(clip);
        SelectedClip = clip;
        NotifyTimelineChanged();
    }

    /// <summary>
    /// 歌詞ファイル（1行1フレーズ）を読み、テキストクリップとして一括で配置する。
    /// 読み込みに失敗した場合は LyricsImportException（何も配置しない）。
    /// 配置は1回の操作としてアンドゥできる（BeginEdit を一度だけ呼ぶ）。
    /// 追加するクリップは手動のテキスト追加と同じ既定値（フォント・サイズ・色）になる。
    /// </summary>
    /// <returns>配置したクリップの数。</returns>
    public int ImportLyrics(string path, LyricsImportOptions options)
    {
        // 読み込み・検証を先に済ませる。失敗したときはスナップショットを積まない。
        var phrases = LyricsFileReader.ReadPhrases(path, options);
        var plan = LyricsImport.PlanClips(phrases, options);

        BeginEdit();
        var added = LyricsImport.ImportInto(Project, plan);
        SelectedClip = added.FirstOrDefault();
        NotifyTimelineChanged();
        StatusMessage = $"歌詞を {added.Count} 行流し込みました：{Path.GetFileName(path)}";
        return added.Count;
    }
}
