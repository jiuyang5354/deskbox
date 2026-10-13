using System.Runtime.CompilerServices;
using DeskBox.Application.Ports;
using DeskBox.Domain.Entities;

namespace DeskBox.Application.Files;

/// <summary>
/// 枚举用户桌面与公共桌面的直接子项（TDD v1.0.7 §4.5）：
/// <list type="bullet">
/// <item>只列直接子项，绝不递归；联接点 / 目录符号链接作为一项出现，但不进入其内部。</item>
/// <item>元数据全部来自目录项（属性、file_id、修改时间），不打开任何文件，因此不会触发云端文件下载。</item>
/// <item>同一 file_id 只出现一次（用户桌面优先）；file_id 格式不合法的项跳过。</item>
/// <item>某个桌面目录不存在或无法列出时跳过该目录，另一个照常枚举。</item>
/// </list>
/// </summary>
public sealed class DesktopEnumerator : IDesktopEnumerator, IDisposable
{
    private readonly IKnownFolders _folders;
    private readonly IDirectoryLister _lister;
    private readonly IDirectoryWatcher _watcher;
    private readonly List<IDisposable> _subscriptions = [];

    public DesktopEnumerator(IKnownFolders folders, IDirectoryLister lister, IDirectoryWatcher watcher)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(lister);
        ArgumentNullException.ThrowIfNull(watcher);
        (_folders, _lister, _watcher) = (folders, lister, watcher);
    }

    public event EventHandler<DesktopChange>? Changed;

    public async IAsyncEnumerable<DesktopItem> EnumerateAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (dir, isPublic) in Desktops())
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<DirectoryItem> items;
            try { items = await Task.Run(() => _lister.List(dir), ct); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 桌面目录不存在 / 无权访问（例如公共桌面被策略隐藏）：不影响另一个桌面
                continue;
            }
            foreach (var i in items)
            {
                if (!FenceItem.IsValidFileId(i.FileId) || !seen.Add(i.FileId)) continue;
                yield return new DesktopItem(i.FullPath, i.FileId.ToUpperInvariant(), i.IsDirectory, isPublic, i.LastWriteTime);
            }
        }
    }

    /// <summary>开始监听两个桌面目录的变化（只监听直接子项）。重复调用无副作用。</summary>
    public void Start()
    {
        if (_subscriptions.Count > 0) return;
        foreach (var (dir, _) in Desktops())
            _subscriptions.Add(_watcher.Watch(dir, c => Changed?.Invoke(this, c)));
    }

    public void Dispose()
    {
        foreach (var s in _subscriptions) s.Dispose();
        _subscriptions.Clear();
    }

    private IEnumerable<(string Dir, bool IsPublic)> Desktops()
    {
        if (!string.IsNullOrWhiteSpace(_folders.UserDesktop)) yield return (_folders.UserDesktop, false);
        if (!string.IsNullOrWhiteSpace(_folders.PublicDesktop)) yield return (_folders.PublicDesktop, true);
    }
}
