[中文](README.md) | **English**

# DeskBox

An open-source Windows desktop organizer. Group shortcuts, screenshots, documents, and other files into desktop boxes without moving the original files. Supports automatic classification rules, restoring the original arrangement, and an optional mode that moves files into a managed library.

- Windows 10 1809+ / Windows 11, x64.
- C# / .NET 8 and WPF.
- MIT license.

## Build

```sh
dotnet build
dotnet test
```

`DeskBox.Infrastructure` and `DeskBox.App` target `net8.0-windows` with `EnableWindowsTargeting` enabled. They can be compiled on non-Windows systems but run only on Windows.

## Documentation

- [Product requirements](docs/PRD.md)
- [Technical design](docs/TDD.md)
- [Contributing](CONTRIBUTING.md)
- [Privacy](PRIVACY.md)

These supporting documents are currently in Chinese.
