namespace DeskBox.Application.Ports;

/// <summary>
/// 原生"不替换"移动的抽象。Windows 实现为 MoveFileExW(src, dst, 0)：
/// 不带 MOVEFILE_REPLACE_EXISTING，也不带 MOVEFILE_COPY_ALLOWED。返回 Win32 错误码，0 表示成功。
/// </summary>
public interface INativeFileMove
{
    int Move(string sourcePath, string targetPath);
}

public static class Win32Errors
{
    public const int Success = 0;
    public const int FileNotFound = 2;
    public const int PathNotFound = 3;
    public const int AccessDenied = 5;
    public const int NotSameDevice = 17;
    public const int WriteProtect = 19;
    public const int SharingViolation = 32;
    public const int LockViolation = 33;
    public const int FileExists = 80;
    public const int AlreadyExists = 183;
    public const int CloudFirst = 362;
    public const int CloudLast = 406;
}

/// <summary>文件系统只读探测（属性、是否目录），便于测试。</summary>
public interface IFileSystemProbe
{
    bool IsDirectory(string path);

    /// <summary>返回 Win32 文件属性位；文件不存在时返回 null。</summary>
    uint? GetAttributes(string path);
}

/// <summary>已知文件夹解析。Windows 上 PublicDesktop 由 SHGetKnownFolderPath(FOLDERID_PublicDesktop) 得到。</summary>
public interface IKnownFolders
{
    string PublicDesktop { get; }
    /// <summary>当前用户桌面（FOLDERID_Desktop）。</summary>
    string UserDesktop { get; }
}

/// <summary>目录中的一项（只读目录项元数据，不打开文件本身）。</summary>
/// <param name="FileId">"卷序列号:128 位文件 ID"，从目录项读取（不打开文件，不会触发云端下载）。</param>
public sealed record DirectoryItem(string FullPath, uint Attributes, string FileId, DateTimeOffset LastWriteTime)
{
    public bool IsDirectory => (Attributes & Win32FileAttributes.Directory) != 0;
    public bool IsReparsePoint => (Attributes & Win32FileAttributes.ReparsePoint) != 0;
    public bool IsCloudOnly => Win32FileAttributes.IsCloudOnly(Attributes);
}

/// <summary>
/// 列出一个目录的直接子项（不递归，不跟随重解析点）。Windows 实现用目录句柄上的
/// GetFileInformationByHandleEx(FileIdExtdDirectoryInfo)，所有路径带 \\?\ 前缀。
/// </summary>
public interface IDirectoryLister
{
    IReadOnlyList<DirectoryItem> List(string directory);
}

/// <summary>监听一个目录的直接子项变化；释放返回值即停止。</summary>
public interface IDirectoryWatcher
{
    IDisposable Watch(string directory, Action<DesktopChange> onChange);
}

public static class Win32FileAttributes
{
    public const uint Offline = 0x0000_1000;
    public const uint RecallOnOpen = 0x0004_0000;
    public const uint RecallOnDataAccess = 0x0040_0000;
    public const uint CloudMask = Offline | RecallOnOpen | RecallOnDataAccess;
    public const uint Directory = 0x0000_0010;
    public const uint ReparsePoint = 0x0000_0400;

    /// <summary>仅按属性位判断"仅云端"（RECALL_ON_DATA_ACCESS / RECALL_ON_OPEN / OFFLINE），绝不打开文件。</summary>
    public static bool IsCloudOnly(uint attributes) => (attributes & CloudMask) != 0;
}
