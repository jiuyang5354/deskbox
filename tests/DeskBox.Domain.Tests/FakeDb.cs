using DeskBox.Application.Ports;
using DeskBox.Domain.Entities;

namespace DeskBox.Domain.Tests;

/// <summary>
/// 模拟 SQLite：每个工作单元只暂存写入，Commit 时一次性生效，未提交释放即回滚（全有或全无）。
/// 每个"步骤"（Begin、每次读写、Commit）都计数；CrashAtStep 命中时进程"被杀"：抛 KilledException，
/// 之后所有步骤都失败，直到 Restart()。约束（主键 / vault_path 唯一 / 状态必须为 Pending）在暂存时校验。
/// </summary>
internal sealed class FakeDb : IUnitOfWorkFactory
{
    public int Steps { get; private set; }
    public int? CrashAtStep { get; set; }
    public bool Dead { get; set; }
    public int Commits { get; private set; }
    public int Rollbacks { get; private set; }

    public void Restart() => (Dead, CrashAtStep, Steps, Commits, Rollbacks) = (false, null, 0, 0, 0);

    internal void Step()
    {
        if (Dead) throw new KilledException();
        Steps++;
        if (CrashAtStep == Steps)
        {
            Dead = true;
            throw new KilledException();
        }
    }

    public Task<IUnitOfWork> BeginAsync(CancellationToken ct = default)
    {
        Step();
        return Task.FromResult<IUnitOfWork>(new Uow(this));
    }

    internal static Uow Of(IUnitOfWork u) => u as Uow ?? throw new InvalidOperationException("必须使用 FakeDb 打开的工作单元");

    internal sealed class Uow(FakeDb db) : IUnitOfWork
    {
        private readonly List<Action> _staged = [];
        public bool Done { get; private set; }
        public FakeDb Db => db;

        public void Stage(Action apply)
        {
            if (Done) throw new InvalidOperationException("事务已结束");
            db.Step();
            _staged.Add(apply);
        }

        public void Read()
        {
            if (Done) throw new InvalidOperationException("事务已结束");
            db.Step();
        }

        public Task CommitAsync(CancellationToken ct = default)
        {
            if (Done) throw new InvalidOperationException("事务已结束");
            db.Step();
            foreach (var a in _staged) a();
            Done = true;
            db.Commits++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            if (!Done) { _staged.Clear(); Done = true; db.Rollbacks++; }
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>内存 Journal：所有写入都经过调用方的工作单元，没有自己的"连接"。</summary>
internal sealed class InMemoryJournal : IJournal
{
    private readonly Dictionary<Guid, int> _failures = [];
    public Dictionary<Guid, JournalEntry> Entries { get; } = [];
    public int Failures(Guid id) => _failures.GetValueOrDefault(id);

    public Task<bool> HasPendingAsync(IUnitOfWork uow, string fileId, CancellationToken ct = default)
    {
        FakeDb.Of(uow).Read();
        return Task.FromResult(Entries.Values.Any(e => e.Status == JournalStatus.Pending &&
            string.Equals(e.FileId, fileId, StringComparison.OrdinalIgnoreCase)));
    }

    public Task AppendPendingAsync(IUnitOfWork uow, JournalEntry entry, CancellationToken ct = default)
    {
        if (Entries.ContainsKey(entry.Id)) throw new InvalidOperationException("UNIQUE constraint failed: journal.id");
        FakeDb.Of(uow).Stage(() => Entries[entry.Id] = entry);
        return Task.CompletedTask;
    }

    // 候选路径已经由调用方写到 entry 上（同一对象）；这里只校验持久化顺序
    public Task UpdateCandidateAsync(IUnitOfWork uow, Guid entryId, string candidatePath, int attempt, CancellationToken ct = default)
    {
        Pending(entryId).TargetPath.Should().Be(candidatePath);
        FakeDb.Of(uow).Stage(() => { });
        return Task.CompletedTask;
    }

    public Task CommitAsync(IUnitOfWork uow, Guid entryId, string actualTargetPath, CancellationToken ct = default) =>
        StageOn(uow, entryId, e => e.Commit(actualTargetPath));

    public Task RollBackAsync(IUnitOfWork uow, Guid entryId, string abortReason, CancellationToken ct = default) =>
        StageOn(uow, entryId, e => e.RollBack(abortReason));

    public Task MarkLostAsync(IUnitOfWork uow, Guid entryId, CancellationToken ct = default) =>
        StageOn(uow, entryId, e => e.MarkLost());

    public Task<int> RecordRecoveryFailureAsync(IUnitOfWork uow, Guid entryId, CancellationToken ct = default)
    {
        Pending(entryId);
        var n = _failures.GetValueOrDefault(entryId) + 1;
        FakeDb.Of(uow).Stage(() => { _failures[entryId] = n; Entries[entryId].RecordRecoveryFailure(); });
        return Task.FromResult(n);
    }

    public Task<IReadOnlyList<JournalEntry>> GetPendingAsync(IUnitOfWork uow, CancellationToken ct = default)
    {
        FakeDb.Of(uow).Read();
        return Task.FromResult<IReadOnlyList<JournalEntry>>(Entries.Values.Where(e => e.Status == JournalStatus.Pending).ToList());
    }

    private JournalEntry Pending(Guid id)
    {
        var e = Entries[id];
        if (e.Status != JournalStatus.Pending) throw new InvalidOperationException($"journal {id} 不是 Pending");
        return e;
    }

    private Task StageOn(IUnitOfWork uow, Guid id, Action<JournalEntry> a)
    {
        var e = Pending(id);
        FakeDb.Of(uow).Stage(() => a(e));
        return Task.CompletedTask;
    }
}

internal sealed class InMemoryVaultRecords : IVaultRecordStore
{
    public List<VaultRecord> All { get; } = [];

    public Task AddAsync(IUnitOfWork uow, VaultRecord record, CancellationToken ct = default)
    {
        if (All.Any(r => string.Equals(r.VaultPath, record.VaultPath, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("UNIQUE constraint failed: vault_record.vault_path");
        FakeDb.Of(uow).Stage(() => All.Add(record));
        return Task.CompletedTask;
    }
}

internal sealed class InMemoryNotices : ILostFileNoticeStore
{
    public List<LostFileNotice> All { get; } = [];
    public int Saves { get; private set; }

    public Task AddAsync(IUnitOfWork uow, LostFileNotice notice, CancellationToken ct = default)
    {
        if (All.Any(n => n.JournalEntryId == notice.JournalEntryId))
            throw new InvalidOperationException("UNIQUE constraint failed: lost_file_notice.journal_entry_id");
        FakeDb.Of(uow).Stage(() => All.Add(notice));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<LostFileNotice>> GetUnacknowledgedAsync(IUnitOfWork uow, CancellationToken ct = default)
    {
        FakeDb.Of(uow).Read();
        return Task.FromResult<IReadOnlyList<LostFileNotice>>(All.Where(n => !n.IsAcknowledged).ToList());
    }

    public Task SaveAsync(IUnitOfWork uow, LostFileNotice notice, CancellationToken ct = default)
    {
        FakeDb.Of(uow).Stage(() => Saves++);
        return Task.CompletedTask;
    }
}
