**中文** | [English](README.en.md)

# DeskBox 桌面收纳

开源的 Windows 桌面整理工具：用**桌面分区方框**就地收纳快捷方式、截图、文档等文件（文件位置不变），支持按规则自动归类、一键还原，以及可选的"移入收纳库"。

- 系统：Windows 10 1809+ / Windows 11（x64）
- 技术栈：C# / .NET 8 + WPF
- 协议：MIT

## 构建
```bash
dotnet build
dotnet test
```
`DeskBox.Infrastructure` 和 `DeskBox.App` 面向 `net8.0-windows`，已开启 `EnableWindowsTargeting`，可在非 Windows 上编译，但只能在 Windows 上运行。

## 文档
- [产品需求 PRD](docs/PRD.md)
- [技术设计 TDD](docs/TDD.md)
- [贡献指南](CONTRIBUTING.md) · [隐私说明](PRIVACY.md)
