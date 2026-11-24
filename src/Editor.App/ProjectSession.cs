// プロジェクトのセッション状態（アンドゥ／リドゥ・保存／読込・自動保存）。
// MainViewModel から 2026-11-24 に分離。振る舞い不変が最優先のため、
// ロジックは分離前の MainViewModel のものをそのまま移している。
// データ損失に直結する部分なので、公開API・呼び出し順・条件は変えていない。
// ViewModel 側の状態（プロジェクト・選択・再生ヘッド・再生状態）と
// UI への通知（ステータス文言・解析キャッシュの保存先・バインド解析）は
// IProjectSessionHost 経由で MainViewModel に委譲する。

using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using Editor.Core;

namespace Editor.App;

/// <summary>ProjectSession が MainViewModel に委譲する操作。</summary>
public interface IProjectSessionHost
{
    /// <summary>現在のプロジェクト。設定時は MainViewModel.Project の setter が走る（検出状態の破棄・画面更新を含む）。</summary>
    EditorProject Project { get; }

    /// <summary>プロジェクトを差し替える（MainViewModel.Project の setter 経由）。</summary>
    void SetProject(EditorProject project);

    /// <summary>再生中か。復元・読込時は false にして止める。</summary>
    bool IsPlaying { set; }

    /// <summary>選択中クリップ。復元・読込時は null にして選択を外す。</summary>
    Clip? SelectedClip { set; }

    /// <summary>再生ヘッド位置（秒）。復元時は復元後の長さで丸める。</summary>
    double PlayheadSeconds { get; set; }

    /// <summary>ステータスバーに出す文言を設定する。</summary>
    void SetStatusMessage(string message);

    /// <summary>現在パスが変わったときに呼ぶ（解析キャッシュの保存先を合わせる）。</summary>
    void OnProjectPathChanged();

    /// <summary>バインドの音源解析を裏で走らせる（読込・復旧後に呼ぶ）。</summary>
    Task EnsureBindingAnalysesAsync();
}

/// <summary>
/// プロジェクトのセッション状態を担う。
/// アンドゥ／リドゥのスナップショット履歴、現在パス、自動保存の
/// タイマー・復旧待ちフラグ・前回保存内容をここで持つ。
/// </summary>
public sealed class ProjectSession : INotifyPropertyChanged
{
    private readonly IProjectSessionHost _host;

    /// <summary>アンドゥ／リドゥの上限（スナップショット件数）。</summary>
    private const int MaxUndoSteps = 50;

    /// <summary>
    /// 現在開いているプロジェクトファイルのパス。未保存（新規）の場合は null。
    /// </summary>
    private string? _currentProjectPath;

    /// <summary>
    /// 最後に自動保存（または明示保存）したときのプロジェクトJSON。
    /// タイマーが発火したときに今の内容と比べ、変わっていなければ書き直さない。
    /// 復旧待ちの間は null（自動保存を止めるための目印も兼ねる）。
    /// </summary>
    private string? _lastAutosavedJson;

    /// <summary>
    /// 本ファイルより新しい自動保存ファイルがあり、復旧待ちの状態か。
    /// この間は自動保存を止める（今の内容で上書きすると復旧用のデータが消えるため）。
    /// 復旧・破棄・明示保存のどれかで解除される。
    /// </summary>
    private bool _recoveryPending;

    /// <summary>自動保存のタイマー（間隔は設定から。UIスレッドで動く）。</summary>
    private readonly DispatcherTimer _autosaveTimer;

    /// <summary>自動保存の状態表示（ステータスバーに出す。例：「自動保存：12:03:15」「自動保存：オフ」）。</summary>
    private string _autosaveStatus = string.Empty;

    /// <summary>本ファイルより新しい自動保存ファイルがあるか（「自動保存から復旧」メニューの有効化に使う）。</summary>
    private bool _hasRecoveryAutosave;

    /// <summary>自動保存ファイルについての説明文（例：更新日時と復旧方法）。無ければ空。</summary>
    private string _autosaveInfo = string.Empty;

    /// <summary>アンドゥ用スナップショット（プロジェクト全体のJSON）。末尾が最新の変更前状態。</summary>
    private readonly List<string> _undoStack = new();

    /// <summary>リドゥ用スナップショット（プロジェクト全体のJSON）。末尾が最新の変更前状態。</summary>
    private readonly List<string> _redoStack = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public ProjectSession(IProjectSessionHost host)
    {
        _host = host;

        // 自動保存：設定を読み、タイマーを作る。保存先（プロジェクトパス）が決まるまでは
        // タイマーが発火しても何もしない（SaveAutosaveNow が弾く）。
        Settings = AppSettings.Load();
        _autosaveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(Settings.ClampedIntervalMinutes)
        };
        _autosaveTimer.Tick += (_, _) => SaveAutosaveNow();
        if (Settings.AutosaveEnabled)
            _autosaveTimer.Start();
        UpdateAutosaveStatus();
    }

    /// <summary>アプリ設定（自動保存の ON/OFF と間隔）。%APPDATA% の settings.json に保存される。</summary>
    public AppSettings Settings { get; }

    /// <summary>現在開いているプロジェクトファイルのパス。未保存（新規）の場合は null（自動保存はしない）。</summary>
    public string? CurrentProjectPath
    {
        get => _currentProjectPath;
        private set
        {
            _currentProjectPath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasProjectPath));
        }
    }

    /// <summary>プロジェクトファイルのパスが決まっているか（保存済み・開いたプロジェクトか）。</summary>
    public bool HasProjectPath => _currentProjectPath is not null;

    /// <summary>元に戻せるか（アンドゥスタックが空でないか）。メニューの有効化に使う。</summary>
    public bool CanUndo => _undoStack.Count > 0;

    /// <summary>やり直せるか（リドゥスタックが空でないか）。メニューの有効化に使う。</summary>
    public bool CanRedo => _redoStack.Count > 0;

    /// <summary>
    /// 変更操作の直前に呼ぶ。変更前のプロジェクト全体を JSON でスナップショットし、
    /// アンドゥスタックに積む。新しい編集なのでリドゥスタックは空になる。
    /// ドラッグのような連続操作では、操作の開始時に1回だけ呼ぶこと。
    /// </summary>
    public void BeginEdit()
    {
        _undoStack.Add(ProjectStore.Serialize(_host.Project));
        if (_undoStack.Count > MaxUndoSteps)
            _undoStack.RemoveAt(0); // 古い方から捨てる。
        _redoStack.Clear();
        RaiseUndoStateChanged();
    }

    /// <summary>1つ前の状態に戻す。スタックが空なら何もしない。</summary>
    public void Undo()
    {
        if (_undoStack.Count == 0)
            return;
        _redoStack.Add(ProjectStore.Serialize(_host.Project));
        var json = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        RestoreSnapshot(json);
        _host.SetStatusMessage("元に戻しました。");
    }

    /// <summary>元に戻した操作をやり直す。スタックが空なら何もしない。</summary>
    public void Redo()
    {
        if (_redoStack.Count == 0)
            return;
        _undoStack.Add(ProjectStore.Serialize(_host.Project));
        var json = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);
        RestoreSnapshot(json);
        _host.SetStatusMessage("やり直しました。");
    }

    /// <summary>プロジェクトの読み込み時に両スタックを空にする。</summary>
    public void ClearUndoHistory()
    {
        _undoStack.Clear();
        _redoStack.Clear();
        RaiseUndoStateChanged();
    }

    /// <summary>スナップショットからプロジェクトを復元し、画面を更新する。</summary>
    private void RestoreSnapshot(string json)
    {
        _host.IsPlaying = false; // 再生中なら止める（復元したプロジェクトと音声がずれるため）。
        _host.SetProject(ProjectStore.Deserialize(json));
        _host.SelectedClip = null; // 復元前のクリップ参照は古くなるので選択を外す。
        // 再生ヘッドが復元後の長さを超えていたら末尾に丸める。
        _host.PlayheadSeconds = Math.Min(_host.PlayheadSeconds, _host.Project.DurationSeconds);
        RaiseUndoStateChanged();
    }

    private void RaiseUndoStateChanged()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }

    public void SaveProject(string path)
    {
        var oldPath = _currentProjectPath;
        ProjectStore.Save(path, _host.Project);
        // 保存先が変わったら（名前を付けて保存）、古いパスの自動保存ファイルも消す。
        if (oldPath is not null
            && !string.Equals(oldPath, path, StringComparison.OrdinalIgnoreCase))
            AutoSave.DeleteAutosave(oldPath);
        CurrentProjectPath = path;
        // 明示保存が成功したら自動保存ファイルは不要になる（本ファイルが最新のため）。
        AutoSave.DeleteAutosave(path);
        _recoveryPending = false;
        HasRecoveryAutosave = false;
        AutosaveInfo = string.Empty;
        _lastAutosavedJson = ProjectStore.Serialize(_host.Project);
        _host.OnProjectPathChanged();
        UpdateAutosaveStatus();
        _host.SetStatusMessage($"保存しました：{path}");
    }

    public void LoadProject(string path)
    {
        _host.IsPlaying = false;
        ClearUndoHistory();
        _host.SetProject(ProjectStore.Load(path));
        _host.SelectedClip = null;
        _host.PlayheadSeconds = 0;

        // 自動保存ファイルを直接開いた場合は復旧扱いにする。
        // 本ファイルのパスを現在パスにして、保存すると本ファイルへ書くようにする。
        var isAutosaveFile = AutoSave.IsAutosavePath(path);
        CurrentProjectPath = isAutosaveFile ? AutoSave.GetProjectPathFromAutosave(path) : path;
        _host.OnProjectPathChanged();

        if (isAutosaveFile)
        {
            _recoveryPending = false;
            HasRecoveryAutosave = false;
            AutosaveInfo = string.Empty;
            _lastAutosavedJson = ProjectStore.Serialize(_host.Project);
            _host.SetStatusMessage($"自動保存ファイルを開きました：{path}。保存すると {CurrentProjectPath} に保存されます。");
        }
        else if (AutoSave.TryGetAutosaveInfo(path, out var autosaveTimeUtc, out var isNewer))
        {
            if (isNewer)
            {
                // 本ファイルより新しい自動保存ファイルがある。復旧の手がかりを残し、
                // 復旧・破棄・明示保存のどれかが起きるまで自動保存を止める
                // （今の内容で上書きすると復旧用のデータが消えてしまうため）。
                _recoveryPending = true;
                _lastAutosavedJson = null;
                HasRecoveryAutosave = true;
                AutosaveInfo = $"自動保存ファイルがあります（{autosaveTimeUtc.ToLocalTime():yyyy/MM/dd HH:mm}）。「ファイル」→「自動保存から復旧」で復旧できます。";
                _host.SetStatusMessage($"開きました：{path}。{AutosaveInfo}");
            }
            else
            {
                // 自動保存ファイルは本ファイルより古い（明示保存で置き換え済みのはず）。
                // 残っていたら消して、次回の自動保存から作り直す。
                AutoSave.DeleteAutosave(path);
                _recoveryPending = false;
                HasRecoveryAutosave = false;
                AutosaveInfo = string.Empty;
                _lastAutosavedJson = ProjectStore.Serialize(_host.Project);
                _host.SetStatusMessage($"開きました：{path}");
            }
        }
        else
        {
            _recoveryPending = false;
            HasRecoveryAutosave = false;
            AutosaveInfo = string.Empty;
            _lastAutosavedJson = ProjectStore.Serialize(_host.Project);
            _host.SetStatusMessage($"開きました：{path}");
        }
        UpdateAutosaveStatus();
        // バインドの音源解析を裏で走らせておく（終わればプレビューに反映される）。
        // キャッシュフォルダが決まっていれば、ディスクキャッシュから読むため速い。
        _ = _host.EnsureBindingAnalysesAsync();
    }

    /// <summary>
    /// テンプレートから新しいプロジェクトを作る（未保存扱い。現在パスは無しになる）。
    /// 復旧待ちがあっても解除する（テンプレートから作ると決めたため。古い自動保存ファイル自体は残る）。
    /// </summary>
    public void NewProjectFromTemplate(EditorProject project)
    {
        _host.IsPlaying = false;
        ClearUndoHistory();
        _host.SetProject(project);
        _host.SelectedClip = null;
        _host.PlayheadSeconds = 0;
        CurrentProjectPath = null;
        _host.OnProjectPathChanged();
        _recoveryPending = false;
        HasRecoveryAutosave = false;
        AutosaveInfo = string.Empty;
        _lastAutosavedJson = ProjectStore.Serialize(_host.Project);
        UpdateAutosaveStatus();
        _host.SetStatusMessage("テンプレートから新しいプロジェクトを作りました。保存するには「プロジェクトを保存」してください。");
        _ = _host.EnsureBindingAnalysesAsync();
    }

    /// <summary>
    /// 本ファイルより新しい自動保存ファイルの内容でプロジェクトを復旧する。
    /// 現在パスは本ファイルのままにするため、この後の明示保存は本ファイルへ書く。
    /// 自動保存ファイルは消さない（復旧後にまたクラッシュしても復旧できるようにする。
    /// 明示保存が成功したときに消える）。
    /// </summary>
    public void RecoverFromAutosave()
    {
        if (_currentProjectPath is null)
        {
            _host.SetStatusMessage("復旧するプロジェクトが開かれていません。");
            return;
        }
        var autosavePath = AutoSave.GetAutosavePath(_currentProjectPath);
        if (!File.Exists(autosavePath))
        {
            HasRecoveryAutosave = false;
            AutosaveInfo = string.Empty;
            _host.SetStatusMessage("自動保存ファイルが見つかりません。");
            return;
        }

        _host.IsPlaying = false;
        ClearUndoHistory();
        _host.SetProject(ProjectStore.Load(autosavePath));
        _host.SelectedClip = null;
        _host.PlayheadSeconds = 0;
        _recoveryPending = false;
        HasRecoveryAutosave = false;
        AutosaveInfo = string.Empty;
        _lastAutosavedJson = ProjectStore.Serialize(_host.Project);
        UpdateAutosaveStatus();
        _host.SetStatusMessage($"自動保存から復旧しました：{autosavePath}。保存するには「プロジェクトを保存」してください。");
        _ = _host.EnsureBindingAnalysesAsync();
    }

    /// <summary>
    /// 自動保存ファイルを破棄する（復旧しないと決めたとき）。破棄後は自動保存が再開する。
    /// 本ファイルや編集中のプロジェクトには触らない。
    /// </summary>
    public void DiscardAutosave()
    {
        if (_currentProjectPath is null)
            return;
        AutoSave.DeleteAutosave(_currentProjectPath);
        _recoveryPending = false;
        HasRecoveryAutosave = false;
        AutosaveInfo = string.Empty;
        _lastAutosavedJson = ProjectStore.Serialize(_host.Project);
        UpdateAutosaveStatus();
        _host.SetStatusMessage("自動保存ファイルを破棄しました。");
    }

    // ---- 自動保存 ----

    /// <summary>自動保存する（既定はオン）。変えると設定ファイルに保存される（保存に失敗しても切り替え自体は有効）。</summary>
    public bool AutosaveEnabled
    {
        get => Settings.AutosaveEnabled;
        set
        {
            if (Settings.AutosaveEnabled == value)
                return;
            Settings.AutosaveEnabled = value;
            SaveSettingsQuietly();
            if (value)
                _autosaveTimer.Start();
            else
                _autosaveTimer.Stop();
            OnPropertyChanged();
            UpdateAutosaveStatus();
        }
    }

    /// <summary>自動保存の間隔（分、1〜60）。</summary>
    public int AutosaveIntervalMinutes => Settings.ClampedIntervalMinutes;

    /// <summary>自動保存の間隔を変える（1〜60分に丸める）。変えると設定ファイルに保存され、タイマーの間隔も変わる（保存に失敗しても変更自体は有効）。</summary>
    public void SetAutosaveIntervalMinutes(int minutes)
    {
        var clamped = Math.Clamp(minutes, 1, 60);
        if (Settings.AutosaveIntervalMinutes == clamped)
            return;
        Settings.AutosaveIntervalMinutes = clamped;
        SaveSettingsQuietly();
        _autosaveTimer.Interval = TimeSpan.FromMinutes(clamped);
        OnPropertyChanged(nameof(AutosaveIntervalMinutes));
        UpdateAutosaveStatus();
    }

    /// <summary>設定を保存する。失敗しても例外は出さない（設定の保存失敗で編集を止めないため）。</summary>
    private void SaveSettingsQuietly()
    {
        try
        {
            Settings.Save();
        }
        catch
        {
            // 次回起動時は既定値に戻るが、今回のセッション中の設定は有効なまま。
        }
    }

    /// <summary>自動保存の状態表示（ステータスバーに出す）。</summary>
    public string AutosaveStatus
    {
        get => _autosaveStatus;
        private set { _autosaveStatus = value; OnPropertyChanged(); }
    }

    /// <summary>本ファイルより新しい自動保存ファイルがあるか（「自動保存から復旧」メニューの有効化に使う）。</summary>
    public bool HasRecoveryAutosave
    {
        get => _hasRecoveryAutosave;
        private set { _hasRecoveryAutosave = value; OnPropertyChanged(); }
    }

    /// <summary>自動保存ファイルについての説明文（例：更新日時と復旧方法）。無ければ空。</summary>
    public string AutosaveInfo
    {
        get => _autosaveInfo;
        private set { _autosaveInfo = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 今のプロジェクトを自動保存ファイルへ保存する（タイマー・終了時に呼ぶ）。
    /// 本ファイルは触らない。内容が前回の自動保存（または明示保存）と同じなら書き直さない。
    /// 復旧待ち・未保存・自動保存オフのときは何もしない。失敗しても例外は出さない。
    /// </summary>
    public void SaveAutosaveNow()
    {
        if (!Settings.AutosaveEnabled || _currentProjectPath is null || _recoveryPending)
            return;
        try
        {
            var json = ProjectStore.Serialize(_host.Project);
            if (json == _lastAutosavedJson)
            {
                AutosaveStatus = "自動保存：変更なし";
                return;
            }
            AutoSave.Save(_currentProjectPath, _host.Project);
            _lastAutosavedJson = json;
            AutosaveStatus = $"自動保存：{DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            AutosaveStatus = $"自動保存に失敗しました：{ex.Message}";
        }
    }

    /// <summary>ステータスバーの自動保存表示を今の状態に合わせて更新する。</summary>
    private void UpdateAutosaveStatus()
    {
        if (!Settings.AutosaveEnabled)
            AutosaveStatus = "自動保存：オフ";
        else if (_recoveryPending)
            AutosaveStatus = "自動保存：復旧待ちのため停止中";
        else if (_currentProjectPath is null)
            AutosaveStatus = "自動保存：未保存のプロジェクト";
        else
            AutosaveStatus = $"自動保存：{Settings.ClampedIntervalMinutes}分ごと";
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
