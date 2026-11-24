// マーカー1行の表示・編集用ラッパー。KeyframeRow と同じ流儀。
// モデルの Marker を直接書き換える。

using System.ComponentModel;
using System.Runtime.CompilerServices;
using Editor.Core;

namespace Editor.App;

/// <summary>マーカー1行の表示・編集用ラッパー。モデルの Marker を直接書き換える。</summary>
public sealed class MarkerRow : INotifyPropertyChanged
{
    private readonly MainViewModel _owner;

    public MarkerRow(MainViewModel owner, Marker marker)
    {
        _owner = owner;
        Marker = marker;
    }

    public Marker Marker { get; }

    /// <summary>タイムライン上の時刻（秒）。0 未満は 0 に丸める。</summary>
    public double TimeSeconds
    {
        get => Marker.TimeSeconds;
        set
        {
            var clamped = Math.Max(0, value);
            if (Math.Abs(Marker.TimeSeconds - clamped) < 1e-9)
                return;
            Marker.TimeSeconds = clamped;
            OnPropertyChanged();
            // 時刻の書き換えで順序が崩れることがあるため、並べ替え直す
            // （EditorProject.Markers は時刻順を不変条件とする）。
            _owner.Project.SortMarkers();
            _owner.NotifyTimelineChanged();
        }
    }

    /// <summary>マーカーの名前（例：「サビ」「ドロップ」）。</summary>
    public string Name
    {
        get => Marker.Name;
        set
        {
            value ??= string.Empty;
            if (Marker.Name == value)
                return;
            Marker.Name = value;
            OnPropertyChanged();
            _owner.NotifyTimelineChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
