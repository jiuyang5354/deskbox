using DeskBox.Application.Ports;

namespace DeskBox.Infrastructure.Files;

/// <summary>递归枚举收纳库目录下的文件和文件夹（恢复时按 file_id 扫描）。跳过无权访问的子目录。</summary>
public sealed class VaultDirectoryIndex : IVaultIndex
{
    private readonly string _root;

    public VaultDirectoryIndex(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = root;
    }

    public IEnumerable<string> EnumerateFiles() =>
        Directory.Exists(_root)
            ? Directory.EnumerateFileSystemEntries(_root, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint, // 不跟随联接点，避免循环与扫到库外
            })
            : [];
}
