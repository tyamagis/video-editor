// テスト用の作業フォルダ。使い終わったら中身ごと消す。

namespace Editor.Tests;

/// <summary>テスト用の作業フォルダ（%TEMP% 配下に一意な名前で作る）。</summary>
public sealed class TempDirectory : IDisposable
{
    public string Dir { get; } = Path.Combine(
        Path.GetTempPath(), "VideoEditorTests-" + Guid.NewGuid().ToString("N"));

    public TempDirectory()
    {
        Directory.CreateDirectory(Dir);
    }

    public string File(string name) => Path.Combine(Dir, name);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Dir))
                Directory.Delete(Dir, recursive: true);
        }
        catch
        {
            // テスト後の掃除の失敗は無視する。
        }
    }
}
