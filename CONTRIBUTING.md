# 贡献指南

1. Fork 后从 `main` 拉分支，分支名如 `feat/xxx`、`fix/xxx`。
2. 提交前本地执行 `dotnet build` 与 `dotnet test`，必须零警告（`TreatWarningsAsErrors`）。
3. 质量门禁：代码中不得留下待办标记或"未实现"异常占位；新功能必须附单元测试；Domain 层行覆盖率 ≥ 90%。
4. Domain 层不得引用任何外部包；Shell 互操作只放在 Infrastructure 层。
5. 提交信息使用 Conventional Commits（`feat:`、`fix:`、`docs:`、`test:`、`chore:`）。
6. 通过 Pull Request 提交，至少一人审查后合并。
