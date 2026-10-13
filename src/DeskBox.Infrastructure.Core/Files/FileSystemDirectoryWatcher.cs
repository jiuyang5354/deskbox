using DeskBox.Application.Ports;

namespace DeskBox.Infrastructure.Files;

/// <summary>FileSystemWatcher 监听目录的直接子项（不含子目录内部）。目录不存在时返回空订阅。</summary>
public sealed class FileSystemDirectoryWatcher : IDirectoryWatcher
{
    public IDisposable Watch(string directory, Action<DesktopChange> onChange)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(onChange);
        if (!Directory.Exists(directory)) return new Subscription(null);
        var w = new FileSystemWatcher(directory)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
        };
        w.Created += (_, e) => onChange(new DesktopChange(DesktopChangeKind.Created, e.FullPath, null));
        w.Deleted += (_, e) => onChange(new DesktopChange(DesktopChangeKind.Deleted, e.FullPath, null));
        w.Changed += (_, e) => onChange(new DesktopChange(DesktopChangeKind.Changed, e.FullPath, null));
        w.Renamed += (_, e) => onChange(new DesktopChange(DesktopChangeKind.Renamed, e.FullPath, e.OldFullPath));
        w.EnableRaisingEvents = true;
        return new Subscription(w);
    }

    private sealed class Subscription(FileSystemWatcher? watcher) : IDisposable
    {
        public void Dispose() => watcher?.Dispose();
    }
}
