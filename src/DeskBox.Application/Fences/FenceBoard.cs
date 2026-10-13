using DeskBox.Application.Ports;
using DeskBox.Domain.Entities;

namespace DeskBox.Application.Fences;

/// <summary>界面显示用的一个方框及其文件（只读快照）。</summary>
public sealed record FenceSnapshot(Fence Fence, IReadOnlyList<FenceItem> Items);

/// <summary>界面读取方框与文件的唯一入口（界面层不引用数据库，TDD §4.7 第 1 条）。</summary>
public sealed class FenceBoard(IUnitOfWorkFactory db, IFenceStore fences, IFenceItemStore items)
{
    public Task<IReadOnlyList<FenceSnapshot>> LoadAsync(CancellationToken ct = default) =>
        db.InTransactionAsync<IReadOnlyList<FenceSnapshot>>(async u =>
        {
            var list = new List<FenceSnapshot>();
            foreach (var f in await fences.ListAsync(u, ct)) list.Add(new(f, await items.ListByFenceAsync(u, f.Id, ct)));
            return list;
        }, ct);
}
