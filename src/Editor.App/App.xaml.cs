// アプリ起動。サービスを組み立て、MainWindow に ViewModel を渡す。
// （依存性注入コンテナは使わず、フェーズ1では手動で配線する。）

using System.Windows;
using Editor.Audio;
using Editor.Media;
using Editor.Render;

namespace Editor.App;

public partial class App : Application
{
    private StreamingFrameSource? _streamingFrameSource;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                args.Exception.Message, "エラー",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        var ffmpegSettings = new FfmpegSettings();
        var mediaProbe = new MediaProbe(ffmpegSettings);
        var thumbnailService = new ThumbnailService(ffmpegSettings);
        var exportService = new ExportService(ffmpegSettings);
        var waveformReader = new WaveformReader();
        // 波形のディスクキャッシュ。保存先（プロジェクトのキャッシュフォルダ）は
        // プロジェクトを開く・保存するたびに ViewModel が設定し直す。
        var waveformCache = new WaveformCache(waveformReader);
        var beatAnalyzer = new AudioBeatAnalyzer();
        var silenceDetector = new SilenceDetector();
        var bandProvider = new AudioBandEnergyProvider();
        // ピーク解析（音声ノーマライズ用）。プレビューと書き出しで同じ
        // インスタンスを共有し、メモリキャッシュも共有する。
        var peakAnalyzer = new PeakAnalyzer();
        _streamingFrameSource = new StreamingFrameSource(ffmpegSettings, mediaProbe);

        IFrameRenderer frameRenderer = new TimelineFrameRenderer(thumbnailService, _streamingFrameSource, bandProvider, bandProvider);
        var previewRenderer = new PreviewRenderer(frameRenderer);
        ITimelineExporter timelineExporter = new FrameSequenceExporter(frameRenderer, ffmpegSettings, mediaProbe, peakAnalyzer);

        var viewModel = new MainViewModel(
            mediaProbe, exportService, waveformCache,
            previewRenderer, timelineExporter, ffmpegSettings, beatAnalyzer, bandProvider, silenceDetector, peakAnalyzer);

        var window = new MainWindow
        {
            DataContext = viewModel
        };
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _streamingFrameSource?.Dispose();
        base.OnExit(e);
    }
}
