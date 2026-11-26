// MainViewModel の分割ファイル（partial）。選択中クリップの音声設定（ノイズ除去・ノーマライズ・フェード／ダッキング・逆再生・速度）（インスペクタ）。

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
    /// <summary>音声つきクリップ（音声または映像で、素材パスがある）が選択されているか。無音カット・ノイズ除去欄の有効化に使う。</summary>
    public bool HasAudioClip => SelectedClip is not null
        && (SelectedClip.Kind == ClipKind.Audio || SelectedClip.Kind == ClipKind.Video)
        && !string.IsNullOrEmpty(SelectedClip.SourcePath);

    /// <summary>無音カットを使えるか（音声つきクリップを選択中で、検出中でない）。</summary>
    public bool CanSilenceCut => HasAudioClip && !IsDetectingSilence;

    /// <summary>選択中クリップのノイズ除去の有効・無効（書き出し時に適用）。離散的な変更なので、代入前にアンドゥ用スナップショットを取る。</summary>
    public bool SelectedClipNoiseReductionEnabled
    {
        get => SelectedClip?.NoiseReductionEnabled ?? false;
        set
        {
            if (SelectedClip is null || SelectedClip.NoiseReductionEnabled == value)
                return;
            BeginEdit();
            SelectedClip.NoiseReductionEnabled = value;
            OnPropertyChanged();
        }
    }

    /// <summary>選択中クリップのノイズ除去の強さ（0.0〜1.0）。クリップ未選択時は 0.5。</summary>
    public double SelectedClipNoiseReductionStrength
    {
        get => SelectedClip?.NoiseReductionStrength ?? 0.5;
        set
        {
            if (SelectedClip is null)
                return;
            SelectedClip.NoiseReductionStrength = Math.Clamp(value, 0.0, 1.0);
            OnPropertyChanged();
        }
    }

    // ---- 音声ノーマライズ（インスペクタ） ----

    /// <summary>選択中クリップの音声ノーマライズの有効・無効（プレビュー・書き出し共通）。離散的な変更なので、代入前にアンドゥ用スナップショットを取る。有効にしたらピーク解析を裏で走らせる（再生時に待たずに済むよう先読みする）。</summary>
    public bool SelectedClipNormalizeAudioEnabled
    {
        get => SelectedClip?.NormalizeAudioEnabled ?? false;
        set
        {
            if (SelectedClip is null || SelectedClip.NormalizeAudioEnabled == value)
                return;
            BeginEdit();
            SelectedClip.NormalizeAudioEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(NormalizeHint));
            _ = RefreshNormalizeHintAsync();
        }
    }

    /// <summary>選択中クリップのノーマライズの目標ピークレベル（dBFS、-30〜0）。クリップ未選択時は -1。ピークは目標に依存しないため、変えても再解析は要らない（キャッシュ済みのピークからゲインを計算し直すだけ）。</summary>
    public double SelectedClipNormalizeTargetDb
    {
        get => SelectedClip?.NormalizeTargetDb ?? AudioNormalizer.DefaultTargetDb;
        set
        {
            if (SelectedClip is null)
                return;
            SelectedClip.NormalizeTargetDb = AudioNormalizer.ClampTargetDb(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(NormalizeHint));
            _ = RefreshNormalizeHintAsync();
        }
    }

    /// <summary>ノーマライズの状態についての説明文（測定したピークと適用されるゲインなど）。選択クリップが変わる・設定が変わると裏で更新する。</summary>
    public string NormalizeHint
    {
        get => _normalizeHint;
        private set
        {
            if (_normalizeHint == value)
                return;
            _normalizeHint = value;
            OnPropertyChanged();
        }
    }

    /// <summary>選択中クリップのノーマライズ情報を裏で測り、NormalizeHint を更新する。選択が変わった場合は古い結果を捨てる。ピーク解析はキャッシュされるため、2回目以降はすぐ終わる。</summary>
    private async Task RefreshNormalizeHintAsync()
    {
        var clip = SelectedClip;
        if (clip is null || !clip.NormalizeAudioEnabled || string.IsNullOrEmpty(clip.SourcePath))
        {
            NormalizeHint = string.Empty;
            return;
        }
        var clipId = clip.Id;
        var targetDb = AudioNormalizer.ClampTargetDb(clip.NormalizeTargetDb);
        NormalizeHint = "ピークを解析中…";
        AudioPeakResult peak;
        try
        {
            peak = await _peakProvider
                .GetPeakAsync(clip.SourcePath, clip.InPointSeconds, clip.SourceDurationSeconds)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            if (SelectedClip?.Id == clipId)
                NormalizeHint = $"ピークを測れませんでした：{ex.Message}（元の音量で再生・書き出しします）";
            return;
        }
        if (SelectedClip?.Id != clipId)
            return; // 選択が変わったので古い結果は捨てる。
        if (!peak.Available)
        {
            NormalizeHint = $"ピークを測れませんでした：{peak.FailureReason}（元の音量で再生・書き出しします）";
            return;
        }
        var gainDb = AudioNormalizer.ComputeGainDb(peak.PeakLinear, targetDb);
        var peakDb = AudioNormalizer.PeakToDb(peak.PeakLinear);
        var peakText = double.IsNegativeInfinity(peakDb) ? "無音" : $"{peakDb:F1} dBFS";
        var sign = gainDb >= 0 ? "+" : string.Empty;
        // 上限（+40 dB）で止まった場合は、目標に届かない旨を明示する。
        var capped = !double.IsNegativeInfinity(peakDb)
            && targetDb - peakDb > AudioNormalizer.MaxGainDb + 1e-9;
        NormalizeHint = capped
            ? $"ピーク {peakText} → ゲイン {sign}{gainDb:F1} dB（上限 +40 dB で止めています。目標 {targetDb:F1} dBFS には届きません）"
            : $"ピーク {peakText} → ゲイン {sign}{gainDb:F1} dB（目標 {targetDb:F1} dBFS）";
    }

    // ---- 音声フェード／ダッキング（インスペクタ） ----

    /// <summary>選択中クリップの音声フェードインの長さ（タイムライン秒、0〜60）。連続変更は欄の GotFocus でスナップショットを取る流儀（ノーマライズ目標と同じ）。</summary>
    public double SelectedClipAudioFadeInSeconds
    {
        get => SelectedClip?.AudioFadeInSeconds ?? 0;
        set
        {
            if (SelectedClip is null)
                return;
            SelectedClip.AudioFadeInSeconds = AudioGainEnvelope.ClampFadeSeconds(value);
            OnPropertyChanged();
        }
    }

    /// <summary>選択中クリップの音声フェードアウトの長さ（タイムライン秒、0〜60）。</summary>
    public double SelectedClipAudioFadeOutSeconds
    {
        get => SelectedClip?.AudioFadeOutSeconds ?? 0;
        set
        {
            if (SelectedClip is null)
                return;
            SelectedClip.AudioFadeOutSeconds = AudioGainEnvelope.ClampFadeSeconds(value);
            OnPropertyChanged();
        }
    }

    /// <summary>選択中クリップのダッキングの役割（なし／対象／トリガー）。離散的な変更なので、代入前にアンドゥ用スナップショットを取る。</summary>
    public DuckingRole SelectedClipDuckingRole
    {
        get => SelectedClip?.DuckingRole ?? DuckingRole.None;
        set
        {
            if (SelectedClip is null || SelectedClip.DuckingRole == value)
                return;
            BeginEdit();
            SelectedClip.DuckingRole = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsDuckingTargetSelected));
        }
    }

    /// <summary>選択中クリップがダッキング対象か（対象用パラメータ欄の表示に使う）。</summary>
    public bool IsDuckingTargetSelected => SelectedClip?.DuckingRole == DuckingRole.Target;

    /// <summary>ダッキングで下げる量（dB、0〜30）。対象クリップだけが使う。</summary>
    public double SelectedClipDuckingAmountDb
    {
        get => SelectedClip?.DuckingAmountDb ?? AudioGainEnvelope.DefaultDuckAmountDb;
        set
        {
            if (SelectedClip is null)
                return;
            SelectedClip.DuckingAmountDb = AudioGainEnvelope.ClampDuckAmountDb(value);
            OnPropertyChanged();
        }
    }

    /// <summary>ダッキングのアタック（秒、0.005〜2）。対象クリップだけが使う。</summary>
    public double SelectedClipDuckingAttackSeconds
    {
        get => SelectedClip?.DuckingAttackSeconds ?? AudioGainEnvelope.DefaultAttackSeconds;
        set
        {
            if (SelectedClip is null)
                return;
            SelectedClip.DuckingAttackSeconds = AudioGainEnvelope.ClampAttackSeconds(value);
            OnPropertyChanged();
        }
    }

    /// <summary>ダッキングのリリース（秒、0.005〜5）。対象クリップだけが使う。</summary>
    public double SelectedClipDuckingReleaseSeconds
    {
        get => SelectedClip?.DuckingReleaseSeconds ?? AudioGainEnvelope.DefaultReleaseSeconds;
        set
        {
            if (SelectedClip is null)
                return;
            SelectedClip.DuckingReleaseSeconds = AudioGainEnvelope.ClampReleaseSeconds(value);
            OnPropertyChanged();
        }
    }

    /// <summary>トリガー検出のしきい値（dBFS、-90〜0）。対象クリップだけが使う。変えるとトリガー区間の再解析が要るが、解析は再生・書き出し時にキャッシュキー（しきい値を含む）で取り直される。</summary>
    public double SelectedClipDuckingThresholdDb
    {
        get => SelectedClip?.DuckingThresholdDb ?? AudioGainEnvelope.DefaultThresholdDb;
        set
        {
            if (SelectedClip is null)
                return;
            SelectedClip.DuckingThresholdDb = AudioGainEnvelope.ClampThresholdDb(value);
            OnPropertyChanged();
        }
    }

    /// <summary>選択中クリップの逆再生（映像・音声とも逆向きに再生する）。離散的な変更なので、代入前にアンドゥ用スナップショットを取る。切り替えるとプレビューが描き直される。</summary>
    public bool SelectedClipIsReversed
    {
        get => SelectedClip?.IsReversed ?? false;
        set
        {
            if (SelectedClip is null || SelectedClip.IsReversed == value)
                return;
            BeginEdit();
            SelectedClip.IsReversed = value;
            OnPropertyChanged();
            NotifyTimelineChanged();
        }
    }

    /// <summary>
    /// 選択中クリップの再生速度（倍率、0.1〜4.0。1.0 が等速）。
    /// 変更は TimelineEditor.ChangeSpeed が行う（素材の使用範囲は変えず、
    /// タイムライン上の長さ = 使用範囲 ÷ 速度 に合わせる）。
    /// スライダーの連続変更ではアンドゥ用スナップショットは欄の GotFocus
    /// （EditableField_GotFocus）が操作の開始時に1回だけ取る流儀のため、
    /// ここでは BeginEdit しない（ノーマライズの目標ピークと同じ扱い）。
    /// 長さが変わるので NotifyTimelineChanged でタイムライン・プレビューを更新する。
    /// </summary>
    public double SelectedClipSpeed
    {
        get => SelectedClip?.Speed ?? 1.0;
        set
        {
            if (SelectedClip is null)
                return;
            var clamped = Clip.ClampSpeed(value);
            if (Math.Abs(SelectedClip.Speed - clamped) < 1e-12)
                return;
            TimelineEditor.ChangeSpeed(SelectedClip, clamped);
            OnPropertyChanged();
            NotifyTimelineChanged();
        }
    }
}
