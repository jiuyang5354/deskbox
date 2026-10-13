# 《DeskBox 桌面收纳》系统技术设计方案（TDD）v1.0.3

## 1. 技术选型
| 项 | 选型 | 理由 / 备选 |
|---|---|---|
| 语言 / 运行时 | C# / .NET 8（win-x64，ReadyToRun + 裁剪） | 原生互操作稳定；Electron 内存开销过大 |
| UI | WPF + CommunityToolkit.Mvvm | 成熟、可嵌入桌面层 |
| 托盘 | H.NotifyIcon | MIT |
| Win32 互操作 | CsWin32 | 源生成，类型安全 |
| 存储 | SQLite + Dapper | 单文件、事务、零部署 |
| 日志 | Serilog（滚动，保留 7 天） | Apache 2.0 |
| 测试 | xUnit + FluentAssertions + NSubstitute + coverlet | — |

所有依赖协议（MIT / Apache 2.0）均与 MIT 兼容。

## 2. 分层
```mermaid
flowchart TD
  App[DeskBox.App<br/>WPF 方框窗口/托盘/设置] --> Application
  Infrastructure[DeskBox.Infrastructure<br/>Shell 互操作/SQLite/文件IO] --> Application
  Application[DeskBox.Application<br/>用例 + 端口接口] --> Domain[DeskBox.Domain<br/>实体/状态机/规则引擎，无外部依赖]
  Infrastructure --> DB[(SQLite %LOCALAPPDATA%\DeskBox)]
```

## 3. 端口接口（Application 层）
- `IDesktopEnumerator`：枚举用户桌面与公共桌面，变化事件。
- `IFileIdentity`：卷序列号 + 128 位文件 ID。
- `IFileMover`：同分区安全移动，返回 `MoveResult`：`Moved(Source, Target, Renamed)` 或 `Skipped(Reason)`，Reason ∈ Locked、ReadOnly、CrossVolume、CloudOnly、NeedsAdmin、NotFound、NameConflict、IoError。
- `INativeFileMove`：原生不替换移动的抽象（返回 Win32 错误码），使改名算法可在 Linux 上单测。
- `IFileSystemProbe`：读取文件属性、判断是否目录。
- `IKnownFolders`：已知文件夹；`PublicDesktop` 在 Windows 上由 `SHGetKnownFolderPath(FOLDERID_PublicDesktop)` 解析。
- `MoveResult.Skipped` 带可选 `NativeErrorCode`（原始 Win32 错误码，预检查跳过时为空）和 `AbortReason`（如 `NeedsAdmin (Win32 5)`），写入 Journal 中止原因和日志。
- `IJournal`：Pending → Committed / RolledBack 预写日志。
- `ISnapshotStore`：快照，保留 10 份。
- `IMonitorService`：显示器指纹、DPI、拔插事件。
- `IDesktopHost`：WorkerW 嵌入、`FWF_NOICONS` 系统图标显隐、`ExplorerRestarted` 事件。
- `IRuleEngine`：分类。

测试要求：`IFileMover`、`IJournal`、`IDesktopHost` 提供故障注入测试实现（占用、磁盘满、Pending 阶段崩溃、Explorer 重启）。

## 4. 关键机制
- **桌面嵌入**：向 Progman 发 `0x052C` 生成 WorkerW，方框窗口 `SetParent` 到其下；`FWF_NOICONS` 隐藏系统图标，改由 DeskBox 绘制。
- **Explorer 重启自愈**：每 5 秒巡检 Progman 句柄，变化即重新嵌入。
- **兜底（P0）**：托盘"临时显示系统桌面图标"；`SafeModePolicy`：连续 2 次启动后 30 秒内崩溃 → 安全模式。崩溃处理器先还原 Explorer 图标再写转储。
- **文件移动原子性**：写 Journal(Pending) → 移动 → Commit(实际路径)；启动时先恢复 Journal，再嵌入桌面层，最后渲染。
- **重命名跟踪**：按 `file_id` 查找记录更新 `file_path`；找不到则从方框移除。
- **显示器变化**：监听 `WM_DISPLAYCHANGE` / `WM_DPICHANGED`；指纹缺失 → Orphaned → 主屏暂存方框；重现后恢复。

### 4.1 重名改名算法（RenamingFileMover，Application 层）
1. **不预先检查**目标是否存在（避免检查与移动之间的 TOCTOU 竞争），直接调用 `MoveFileExW(src, dst, 0)`：不带 `MOVEFILE_REPLACE_EXISTING`，也不带 `MOVEFILE_COPY_ALLOWED`（跨卷直接失败）。
2. 返回 `ERROR_FILE_EXISTS(80)` / `ERROR_ALREADY_EXISTS(183)` 时，依次尝试 `name (n).ext`，n = 2..999（目录不拆扩展名；`.gitignore` 类无主名文件追加在末尾）。生成改名目标前**先去掉名字末尾的点和空格**再拆扩展名（`report. ` → `report (2)`，`notes.txt. ` → `notes (2).txt`）；全由点组成的名字保持原样。源文件访问仍使用原始名字。
3. 999 仍冲突 → `Skipped(NameConflict)`。
4. 移动前预检查（命中则**不调用**原生移动，`NativeErrorCode` 为空）：
   - 源文件属性含 `FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS(0x400000)`、`RECALL_ON_OPEN(0x40000)` 或 `OFFLINE(0x1000)` → CloudOnly；
   - 源路径位于公共桌面（`FOLDERID_PublicDesktop`）之下 → NeedsAdmin。
5. 错误码映射（`NativeErrorCode` 保留原值）：

| Win32 错误码 | 含义 | SkipReason | 说明 |
|---|---|---|---|
| 80 / 183 | FILE_EXISTS / ALREADY_EXISTS | —（改名重试） | 999 次用尽 → NameConflict（记 183） |
| 32 / 33 | SHARING_VIOLATION / LOCK_VIOLATION | Locked | 仅此两种归为 Locked |
| 2 / 3 | FILE_NOT_FOUND / PATH_NOT_FOUND | NotFound | |
| 5 | ACCESS_DENIED | NeedsAdmin | 兜底；主路径为公共桌面预检查 |
| 17 | NOT_SAME_DEVICE | CrossVolume | 不带 COPY_ALLOWED，跨卷直接失败 |
| 19 | WRITE_PROTECT | ReadOnly | 写保护介质（SD 卡写保护、只读光盘等） |
| 362–406 | ERROR_CLOUD_FILE_* | CloudOnly | 兜底；主路径为属性预检查 |
| 其他 | — | IoError | |

6. 成功时 `Moved.Renamed = (n > 1)`，UI 据此提示"已自动改名"。还原到被占用的原路径走同一算法。
7. `JournaledMove`：Pending 记录意图路径；成功后 `Commit(actualTargetPath)` 记录**实际落盘路径**；跳过则 `RollBack(abortReason)`，中止原因含原始错误码。

### 4.1.1 路径规范化（v1.0.3）
所有传给 Win32 文件 API（`MoveFileExW`、`GetFileAttributesW` 等）的路径都必须经过 `Win32PathNormalizer.Normalize`（Application 层纯函数，可在 Linux 上测试）：
- `C:\a\b` → `\\?\C:\a\b`；`\\server\share\a` → `\\?\UNC\server\share\a`，从而支持超过 260 字符的路径；
- 已带 `\\?\` 或 `\\.\` 前缀的路径原样返回；
- 只接受盘符绝对路径或 UNC 路径，`a\b`、`C:a`、`\a` 等相对路径直接拒绝，**绝不依赖当前工作目录**；
- `/` 统一为 `\`，合并重复分隔符，解析 `.` / `..`（越过根目录则拒绝）；
- **保留文件名末尾的空格和点**（如 `report. `、`a .`）：带前缀后 Win32 不再隐式裁剪，否则会操作到另一个文件；
- Unicode / emoji 文件名原样保留（UTF-16 传参）。

### 4.1.2 跳过原因文案
界面文案集中在 `DeskBox.Application/Resources/Strings.resx`（默认 zh-CN，键为 `SkipReason` 枚举名），通过 `SkipReasonText.Get` 读取；单测保证每个枚举值都有文案。日志与 Journal 仍使用 `Skipped.AbortReason`（如 `NeedsAdmin (Win32 5)`）。

### 4.2 整理任务状态机修订
- `Cancel` 只允许在 Classifying（尚未动文件）；Created / Snapshotting / Applying 均不可取消。
- Applying 中致命错误：Applying →(Fail)→ RollingBack →(RolledBack)→ Failed。
- 方框状态机（含 Deleted 终态）不变，已被 PM 接受。

## 5. 线程与性能
UI 线程只渲染；枚举、规则匹配、IO 走 `Task.Run` + `Channel<T>`。图标 LRU 缓存 512 项；`VirtualizingWrapPanel` 支持 2000+ 文件。空闲无轮询（Explorer 巡检除外）。目标启动 ≤ 1.5 s，空闲内存 ≤ 80 MB。

## 6. 可观测性
Serilog 写 `%LOCALAPPDATA%\DeskBox\logs`，每条带 `JobId` 与耗时；路径只记哈希 + 文件名。全局捕获 `DispatcherUnhandledException` 与 AppDomain 未处理异常。

## 7. 对 PRD 的澄清（已被 PM 采纳为 v1.1）
1. `fence_item.file_path` 全局唯一。
2. 坐标按"相对显示器物理像素 + 记录 DPI"存储，新增 `dpi` 字段。
3. 公共桌面项可映射、可拖动，不能移入收纳库。

## 8. 研发任务拆分
| # | 模块 | 内容 | 估时 |
|---|---|---|---|
| 1 | Domain | 实体、两个状态机、规则引擎，单测覆盖率 ≥ 90% | 3d |
| 2 | Infra | SQLite 仓储、迁移、Journal、快照存储 | 3d |
| 3 | Infra | 桌面枚举、FileId、FileMover、占用和云端文件检测 | 3d |
| 4 | Infra | WorkerW 嵌入、FWF_NOICONS、Explorer 重启自愈 | 4d |
| 5 | App | 方框窗口、自由层、拖放、右键菜单、DPI 处理 | 6d |
| 6 | App | 一键整理、还原、收纳库、实时收纳 | 4d |
| 7 | App | 多显示器、托盘、开机自启、设置 | 3d |
| 8 | 交付 | 安装包、卸载恢复、崩溃恢复 | 2d |

## 9. 交付流水线
`dotnet build` → 测试（Domain 覆盖率门禁 ≥ 90%）→ `dotnet publish`（win-x64，ReadyToRun）→ MSIX + Inno Setup EXE → 签名（SignPath，正式发布前接入）→ SHA256 + SBOM → GitHub Release 草稿，人工确认后发布。MSIX 清单 Publisher 须与证书主体一致。Shell 互操作与多屏 / DPI 集成测试在 `windows-latest` 与自托管物理机上运行。
