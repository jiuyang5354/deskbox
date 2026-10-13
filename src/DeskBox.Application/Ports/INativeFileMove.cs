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
