# AGENTS.md

本项目是 AEAssist 的 IAEPlugin 插件（白轴记录器）。请在修改时遵循以下约定。

## 目标
- 仅记录触发条件，不新增行为节点
- 导出 JSON 结构需保持与 AEAssist 触发树兼容
- 导出文件路径与命名规则保持稳定，除非用户明确要求

## 修改约定
- 如新增/变更记录来源，请同步更新 `README.md` 与 `docs/EXPORT_FORMAT.md`
- 如调整导出或去重策略，请同步更新 `docs/USAGE.md` 与 `docs/TROUBLESHOOTING.md`
- 避免引入新的外部依赖

## 代码风格
- 目标框架：`net10.0-windows`
- 日志输出保持简洁、可定位
- JSON 使用 `System.Text.Json`，保持缩进与 UTF-8 无 BOM

## 验证
- 尽量执行一次本地构建：`dotnet build D:\WhiteAxisRecorder\WhiteAxisRecorder.csproj`
- 若无法构建，请在交付说明中写明原因
