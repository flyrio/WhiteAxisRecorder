# 构建与部署

## 依赖
- .NET SDK（支持 `net10.0-windows`）
- AEAssist 运行环境

## 构建
在 PowerShell 中执行：

```powershell
# 进入项目目录
cd D:\WhiteAxisRecorder

# 构建
dotnet build .\WhiteAxisRecorder.csproj
```

## 输出位置
- 如果设置了 `AEPath`：输出到 `$(AEPath)Plugins\WhiteAxisRecorder`
- 未设置 `AEPath`：输出到 `D:\AE3.0\Plugins\WhiteAxisRecorder`

## 部署
将构建输出目录复制/保持在 AEAssist 的 `Plugins` 目录下，启动 AEAssist 后启用插件即可。
