// アプリ起動。サービスを組み立て、MainWindow に ViewModel を渡す。
// （依存性注入コンテナは使わず、フェーズ1では手動で配線する。）

using System.Windows;
using Editor.Audio;
using Editor.Media;
using Editor.Render;

namespace Editor.App;

public partial class App : Application
{
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

        IFrameRenderer frameRenderer = new TimelineFrameRenderer(thumbnailService);
        var previewRenderer = new PreviewRenderer(frameRenderer);
        ITimelineExporter timelineExporter = new FrameSequenceExporter(frameRenderer, ffmpegSettings);

        var viewModel = new MainViewModel(
            mediaProbe, thumbnailService, exportService, waveformReader,
            previewRenderer, timelineExporter, ffmpegSettings);

        var window = new MainWindow
        {
            DataContext = viewModel
        };
        window.Show();
    }
}
