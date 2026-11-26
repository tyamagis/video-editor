// MainViewModel の分割ファイル（partial）。マーカーの一覧・追加・削除・ジャンプ。

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
    // ---- マーカー（MV作業で曲の区切り・サビ・ドロップに印を付ける） ----

    /// <summary>マーカー一覧の表示・編集用行（時刻順）。</summary>
    public ObservableCollection<MarkerRow> MarkerRows { get; } = new();

    private MarkerRow? _selectedMarker;

    /// <summary>選択中のマーカー行（ルーラーでクリックしたもの・一覧で選んだもの）。null は未選択。</summary>
    public MarkerRow? SelectedMarker
    {
        get => _selectedMarker;
        set { _selectedMarker = value; OnPropertyChanged(); }
    }

    /// <summary>マーカー行を作り直す（時刻順）。選択中のマーカーが残っていれば選択を引き継ぐ。</summary>
    public void RefreshMarkerRows()
    {
        var selected = _selectedMarker?.Marker;
        MarkerRows.Clear();
        foreach (var marker in Project.Markers)
            MarkerRows.Add(new MarkerRow(this, marker));
        SelectedMarker = selected is null
            ? null
            : MarkerRows.FirstOrDefault(r => r.Marker == selected);
    }

    /// <summary>再生ヘッド位置にマーカーを追加する。名前は「マーカー N」（未使用の最小の番号）。</summary>
    public void AddMarkerAtPlayhead()
    {
        BeginEdit();
        var marker = Project.AddMarker(PlayheadSeconds, NextDefaultMarkerName());
        RefreshMarkerRows();
        SelectedMarker = MarkerRows.FirstOrDefault(r => r.Marker == marker);
        NotifyTimelineChanged();
        StatusMessage = $"マーカーを追加しました：{marker.Name}（{marker.TimeSeconds:F2}s）";
    }

    /// <summary>未使用の最小の番号で「マーカー N」を作る（削除で空いた番号は再利用する）。</summary>
    private string NextDefaultMarkerName()
    {
        var n = 1;
        while (Project.Markers.Any(m => m.Name == $"マーカー {n}"))
            n++;
        return $"マーカー {n}";
    }

    /// <summary>マーカーを削除する。</summary>
    public void DeleteMarker(MarkerRow row)
    {
        if (!Project.Markers.Contains(row.Marker))
            return;
        BeginEdit();
        var name = row.Marker.Name;
        Project.RemoveMarker(row.Marker);
        RefreshMarkerRows();
        NotifyTimelineChanged();
        StatusMessage = $"マーカーを削除しました：{name}";
    }

    /// <summary>選択中のマーカーを削除する。未選択なら何もしない。</summary>
    public void DeleteSelectedMarker()
    {
        if (SelectedMarker is not null)
            DeleteMarker(SelectedMarker);
    }

    /// <summary>指定マーカーの行を選択する（ルーラーでクリックしたとき）。行が無ければ何もしない。</summary>
    public void SelectMarker(Marker marker)
    {
        var row = MarkerRows.FirstOrDefault(r => r.Marker == marker);
        if (row is not null)
            SelectedMarker = row;
    }

    /// <summary>再生ヘッドを次のマーカーへ移動する。次のマーカーが無ければ何もしない。</summary>
    public void JumpToNextMarker()
    {
        var next = Project.NextMarkerAfter(PlayheadSeconds);
        if (next is null)
        {
            StatusMessage = "次のマーカーはありません。";
            return;
        }
        PlayheadSeconds = next.TimeSeconds;
        SelectMarker(next);
        StatusMessage = $"マーカー「{next.Name}」へ移動しました（{PlayheadSeconds:F2}s）。";
    }

    /// <summary>再生ヘッドを前のマーカーへ移動する。前のマーカーが無ければ何もしない。</summary>
    public void JumpToPreviousMarker()
    {
        var prev = Project.PreviousMarkerBefore(PlayheadSeconds);
        if (prev is null)
        {
            StatusMessage = "前のマーカーはありません。";
            return;
        }
        PlayheadSeconds = prev.TimeSeconds;
        SelectMarker(prev);
        StatusMessage = $"マーカー「{prev.Name}」へ移動しました（{PlayheadSeconds:F2}s）。";
    }
}
