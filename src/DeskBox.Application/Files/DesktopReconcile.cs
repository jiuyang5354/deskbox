using DeskBox.Application.Ports;
using DeskBox.Application.Startup;
using DeskBox.Domain.Entities;
using DeskBox.Domain.Files;

namespace DeskBox.Application.Files;

/// <summary>一次扫描的结果。Present 是磁盘上确实存在的全部路径（含 file_id 无效、不能收的项），用于判断哪些归属行该删。</summary>
public sealed record DesktopScan(IReadOnlyList<DesktopItem> Items, IReadOnlySet<string> Present, IReadOnlyList<string> Failures)
{
    /// <summary>用户桌面和公共桌面都读取成功、中途没出错。</summary>
    public bool Complete => Failures.Count == 0;
}

/// <summary>
/// 启动对账的扫描（TDD v1.0.14 §4.5.5 第 2 条）：用户桌面 + 公共桌面的直接子项，按完整路径去重（不区分大小写），
/// 不收带隐藏或系统属性的项（如 desktop.ini）；文件夹和快捷方式都收；仅云端文件只看属性、不读内容。
/// 与 <see cref="DesktopEnumerator"/> 不同：读失败不吞掉，而是记进 <see cref="DesktopScan.Failures"/>，供"扫描完整才删"判断。
/// </summary>
public sealed class DesktopScanner(IKnownFolders folders, IDirectoryLister lister)
{
    public const string UserDesktopName = "用户桌面";
    public const string PublicDesktopName = "公共桌面";

    public async Task<DesktopScan> ScanAsync(CancellationToken ct = default)
    {
        var items = new List<DesktopItem>();
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();
        foreach (var (name, dir, isPublic) in new[] { (UserDesktopName, folders.UserDesktop, false), (PublicDesktopName, folders.PublicDesktop, true) })
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(dir))
            {
                failures.Add($"{name}读取失败（路径为空）");
                continue;
            }
            IReadOnlyList<DirectoryItem> listed;
            try
            {
                listed = await Task.Run(() => lister.List(dir), ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{name}读取失败（{ex.GetType().Name}）");
                continue;
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
            {
                failures.Add($"{name}扫描中途出错（{ex.GetType().Name}）");
                continue;
            }
            foreach (var i in listed)
            {
                if ((i.Attributes & (Win32FileAttributes.Hidden | Win32FileAttributes.System)) != 0) continue;
                if (!present.Add(i.FullPath)) continue; // 同一完整路径只收一份
                if (FenceItem.IsValidFileId(i.FileId))
                    items.Add(new DesktopItem(i.FullPath, i.FileId.ToUpperInvariant(), i.IsDirectory, isPublic, i.LastWriteTime));
            }
        }
        return new DesktopScan(items, present, failures);
    }
}

/// <summary>对账结果。StagingMissing 时什么都没写。</summary>
public sealed record ReconcileResult(int Found, int Written, int Pruned, bool PruneSkipped, bool StagingMissing = false);

/// <summary>
/// 启动对账写库（TDD v1.0.14 §4.5.5 第 3–8 条），一个 BEGIN IMMEDIATE 事务：
/// 已在方框里的路径不动；新路径按文件名自然排序追加到暂存方框末尾；扫描完整时才删磁盘上已不存在的行。
/// 不调用任何文件移动接口，也不碰文件本身。
/// </summary>
public sealed class DesktopReconciler(IUnitOfWorkFactory db, IFenceStore fences, IFenceItemStore items, IDesktopScanLog log, Func<Guid>? newId = null)
{
    private readonly Func<Guid> _newId = newId ?? Guid.NewGuid;

    public async Task<ReconcileResult> ReconcileAsync(DesktopScan scan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scan);
        var pruned = new List<string>();
        string? skipReason = null;
        var outcome = await db.InTransactionAsync(async u =>
        {
            var staging = (await fences.ListAsync(u, ct)).FirstOrDefault(f => f.IsStaging);
            if (staging is null) return new ReconcileResult(scan.Present.Count, 0, 0, false, StagingMissing: true);

            var all = await items.ListAllAsync(u, ct);
            var known = new HashSet<string>(all.Select(i => i.FilePath), StringComparer.OrdinalIgnoreCase);
            var next = all.Where(i => i.FenceId == staging.Id).Select(i => i.SortOrder).DefaultIfEmpty(-1).Max() + 1;
            var fresh = scan.Items.Where(i => !known.Contains(i.FullPath))
                .OrderBy(i => WinPath.GetFileName(i.FullPath), NaturalNameComparer.Instance).ToList();
            foreach (var i in fresh)
                await items.UpsertAsync(u, new FenceItem(_newId(), staging.Id, i.FullPath, i.FileId, i.IsPublicDesktop, next++), ct);

            if (!scan.Complete) skipReason = string.Join("；", scan.Failures);
            else if (scan.Present.Count == 0 && all.Count > 0) skipReason = $"扫描到 0 项但库里有 {all.Count} 行";
            else
            {
                foreach (var gone in all.Where(i => !scan.Present.Contains(i.FilePath)))
                {
                    await items.DeleteAsync(u, gone.Id, ct);
                    pruned.Add(WinPath.GetFileName(gone.FilePath));
                }
            }
            return new ReconcileResult(scan.Present.Count, fresh.Count, pruned.Count, skipReason is not null);
        }, ct);

        // 提交之后才写日志：事务回滚时不留假记录
        if (outcome.StagingMissing)
        {
            log.StagingFenceMissing();
            return outcome;
        }
        foreach (var name in pruned) log.FenceItemPruned(name);
        if (skipReason is not null) log.DesktopScanPruneSkipped(skipReason);
        log.DesktopScanCompleted(outcome.Found, outcome.Written, outcome.Pruned, outcome.PruneSkipped);
        return outcome;
    }
}

/// <summary>文件名自然排序，不区分大小写：File1 &lt; file2 &lt; file10。完全相同（忽略大小写）时按序数比较保证稳定。</summary>
public sealed class NaturalNameComparer : IComparer<string>
{
    public static readonly NaturalNameComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                int si = i, sj = j;
                while (i < x.Length && char.IsAsciiDigit(x[i])) i++;
                while (j < y.Length && char.IsAsciiDigit(y[j])) j++;
                var a = x[si..i].TrimStart('0');
                var b = y[sj..j].TrimStart('0');
                var c = a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);
                if (c != 0) return c;
            }
            else
            {
                var c = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                if (c != 0) return c;
                i++;
                j++;
            }
        }
        var rest = (x.Length - i).CompareTo(y.Length - j);
        return rest != 0 ? rest : string.CompareOrdinal(x, y);
    }
}

/// <summary>
/// 启动对账入口（§4.5.5 第 1、10 条）：只读模式不对账、记 DesktopScanSkipped；写入行数大于 0 时托盘提示一次「已收纳 N 个桌面文件」。
/// 启动时与收到 TaskbarCreated 时各调用一次。对账失败不影响方框显示，记 DesktopReconcileFailed。
/// </summary>
public sealed class DesktopReconcile(DesktopScanner scanner, DesktopReconciler reconciler, OperatingMode mode, IDesktopScanLog log, ITrayNotifier? tray = null)
    : IStartupReconcile
{
    public static string CollectedNotice(int n) => $"已收纳 {n} 个桌面文件";

    public async Task<ReconcileResult?> RunAsync(CancellationToken ct = default)
    {
        if (mode.IsReadOnly)
        {
            log.DesktopScanSkipped(mode.ReadOnlyReason ?? "只读模式");
            return null;
        }
        try
        {
            var result = await reconciler.ReconcileAsync(await scanner.ScanAsync(ct), ct);
            if (result.Written > 0) tray?.ShowNotice(CollectedNotice(result.Written));
            return result;
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            log.DesktopReconcileFailed($"{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
