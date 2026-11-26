// MainViewModel の分割ファイル（partial）。プロジェクトのセッション委譲窓口（Undo/Redo・保存／読込・テンプレート・自動保存）とホスト実装。

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
    /// <summary>元に戻せるか（アンドゥスタックが空でないか）。メニューの有効化に使う。</summary>
    public bool CanUndo => _session.CanUndo;

    /// <summary>やり直せるか（リドゥスタックが空でないか）。メニューの有効化に使う。</summary>
    public bool CanRedo => _session.CanRedo;

    /// <summary>
    /// 変更操作の直前に呼ぶ。変更前のプロジェクト全体を JSON でスナップショットし、
    /// アンドゥスタックに積む。新しい編集なのでリドゥスタックは空になる。
    /// ドラッグのような連続操作では、操作の開始時に1回だけ呼ぶこと。
    /// </summary>
    public void BeginEdit() => _session.BeginEdit();

    /// <summary>1つ前の状態に戻す。スタックが空なら何もしない。</summary>
    public void Undo() => _session.Undo();

    /// <summary>元に戻した操作をやり直す。スタックが空なら何もしない。</summary>
    public void Redo() => _session.Redo();

    /// <summary>プロジェクトの読み込み時に両スタックを空にする。</summary>
    public void ClearUndoHistory() => _session.ClearUndoHistory();

    /// <summary>プロジェクトをファイルへ保存する（名前を付けて保存にも使う）。</summary>
    public void SaveProject(string path) => _session.SaveProject(path);

    /// <summary>プロジェクトファイルを読み込む。自動保存ファイルがあれば復旧待ちにする。</summary>
    public void LoadProject(string path) => _session.LoadProject(path);

    // ---- プロジェクトテンプレート ----

    /// <summary>現在のプロジェクトをテンプレートとして保存する（同名があれば上書き。保存先は既定のテンプレートフォルダ）。</summary>
    public SaveTemplateResult SaveAsTemplate(string name) => TemplateStore.Save(name, Project);

    /// <summary>同名（大文字・小文字を区別しない）のテンプレートがあるか（上書き確認に使う）。</summary>
    public bool TemplateExists(string name) => TemplateStore.TemplateExists(name);

    /// <summary>テンプレートの一覧を取得する（名前順。壊れたファイルも「読み込めません」として含む）。</summary>
    public IReadOnlyList<TemplateInfo> ListTemplates() => TemplateStore.List();

    /// <summary>
    /// テンプレートから新しいプロジェクトを作る（未保存扱い）。
    /// 参照メディアが見つからないクリップは配置だけ残し、見つからなかったファイルの一覧を結果に含める。
    /// テンプレートファイルが壊れている場合は例外を投げる（現在のプロジェクトは変わらない）。
    /// </summary>
    public TemplateCreateResult NewProjectFromTemplate(string path)
    {
        var result = TemplateStore.CreateProject(path);
        _session.NewProjectFromTemplate(result.Project);
        return result;
    }

    /// <summary>本ファイルより新しい自動保存ファイルの内容でプロジェクトを復旧する。</summary>
    public void RecoverFromAutosave() => _session.RecoverFromAutosave();

    /// <summary>自動保存ファイルを破棄する（復旧しないと決めたとき）。</summary>
    public void DiscardAutosave() => _session.DiscardAutosave();

    // ---- 自動保存 ----

    /// <summary>アプリ設定（自動保存の ON/OFF と間隔）。%APPDATA% の settings.json に保存される。</summary>
    public AppSettings Settings => _session.Settings;

    /// <summary>現在開いているプロジェクトファイルのパス。未保存（新規）の場合は null（自動保存はしない）。</summary>
    public string? CurrentProjectPath => _session.CurrentProjectPath;

    /// <summary>プロジェクトファイルのパスが決まっているか（保存済み・開いたプロジェクトか）。</summary>
    public bool HasProjectPath => _session.HasProjectPath;

    /// <summary>自動保存する（既定はオン）。変えると設定ファイルに保存される（保存に失敗しても切り替え自体は有効）。</summary>
    public bool AutosaveEnabled
    {
        get => _session.AutosaveEnabled;
        set => _session.AutosaveEnabled = value;
    }

    /// <summary>自動保存の間隔（分、1〜60）。</summary>
    public int AutosaveIntervalMinutes => _session.AutosaveIntervalMinutes;

    /// <summary>自動保存の間隔を変える（1〜60分に丸める）。変えると設定ファイルに保存され、タイマーの間隔も変わる（保存に失敗しても変更自体は有効）。</summary>
    public void SetAutosaveIntervalMinutes(int minutes) => _session.SetAutosaveIntervalMinutes(minutes);

    /// <summary>自動保存の状態表示（ステータスバーに出す）。</summary>
    public string AutosaveStatus => _session.AutosaveStatus;

    /// <summary>本ファイルより新しい自動保存ファイルがあるか（「自動保存から復旧」メニューの有効化に使う）。</summary>
    public bool HasRecoveryAutosave => _session.HasRecoveryAutosave;

    /// <summary>自動保存ファイルについての説明文（例：更新日時と復旧方法）。無ければ空。</summary>
    public string AutosaveInfo => _session.AutosaveInfo;

    /// <summary>
    /// 今のプロジェクトを自動保存ファイルへ保存する（タイマー・終了時に呼ぶ）。
    /// 本ファイルは触らない。内容が前回の自動保存（または明示保存）と同じなら書き直さない。
    /// 復旧待ち・未保存・自動保存オフのときは何もしない。失敗しても例外は出さない。
    /// </summary>
    public void SaveAutosaveNow() => _session.SaveAutosaveNow();

    // ---- IProjectSessionHost / IDetectionHost の実装（セッション・検出からの委譲先） ----

    void IProjectSessionHost.SetProject(EditorProject project) => Project = project;

    void IProjectSessionHost.SetStatusMessage(string message) => StatusMessage = message;

    void IDetectionHost.SetStatusMessage(string message) => StatusMessage = message;

    void IProjectSessionHost.OnProjectPathChanged() => UpdateProjectCacheDirectories();

    /// <summary>
    /// 解析キャッシュの保存先を今のプロジェクトに合わせる。
    /// プロジェクトファイルの隣の「プロジェクト名.cache」フォルダ。
    /// 未保存の場合は null（メモリのみ・永続化しない）。
    /// </summary>
    private void UpdateProjectCacheDirectories()
    {
        var cacheDirectory = _session.CurrentProjectPath is null
            ? null
            : AudioAnalysisCache.GetCacheDirectory(_session.CurrentProjectPath);
        _bandProvider.CacheDirectory = cacheDirectory;
        _waveformCache.CacheDirectory = cacheDirectory;
    }
}
