using System.IO;
using System.Text;

namespace FrameTrace.Editor.Services;

public sealed class LibraryPublishLock : IDisposable
{
    private readonly string lockPath;
    private readonly FileStream stream;
    private bool disposed;

    private LibraryPublishLock(string lockPath, FileStream stream)
    {
        this.lockPath = lockPath;
        this.stream = stream;
    }

    public static bool TryAcquire(string assetsDirectory, out LibraryPublishLock? publishLock, out string? errorMessage)
    {
        Directory.CreateDirectory(assetsDirectory);
        var lockPath = Path.Combine(assetsDirectory, ".frametrace.publish.lock");
        try
        {
            var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
            stream.SetLength(0);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
            writer.WriteLine($"ProcessId={Environment.ProcessId}");
            writer.WriteLine($"StartedAt={DateTimeOffset.Now:O}");
            writer.Flush();
            publishLock = new LibraryPublishLock(lockPath, stream);
            errorMessage = null;
            return true;
        }
        catch (IOException)
        {
            publishLock = null;
            errorMessage = $"素材库正在由另一实例发布。锁文件：{lockPath}";
            return false;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        stream.Dispose();
        try
        {
            File.Delete(lockPath);
        }
        catch (IOException)
        {
            // 另一个实例已接管遗留锁文件；不能删除它的锁。
        }
    }
}