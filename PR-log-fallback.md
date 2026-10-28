# PR 2 — fix: surface logging/degrade failures instead of swallowing them (日志不存在 / 降级报错)

Base: `main`

## 问题

当日志目录无法创建、或无写入权限、或日志写入失败时，`CompanionLog` 之前会**静默降级**
（丢弃日志但不告知用户），导致启动异常时用户完全看不到任何原因。

## 修复

- `CompanionLog` 新增静态回调 `FallbackError`：初始化失败与写入失败时，通过它**上报原因**
  而不再静默吞异常。
- 桌面端 `Main` 在最早时机注册 `FallbackError`，以弹窗呈现（不依赖日志文件），确保日志
  不可用时失败可见。
- 启动阶段的致命异常（`catch` 块）在写入 `companion.log` 之外，额外弹出错误对话框，
  让用户**直接看到启动失败的真实原因**，而不依赖日志文件是否可写。

## 配置目录注释修正

- `Companion.Server` 配置路径处加中性注释：移出安装目录以避免只读安装位置写入失败。
- 明确说明：此前"必须以管理员权限才能打开 exe"的**原始根因尚未确认**，本次仅解决
  "安装目录只读"这一明确可复现的场景，不断言配置写入就是崩溃根因。

## 测试场景

将日志目录设为无写权限，验证弹窗提示"无法写入日志"，且程序其余行为按降级逻辑继续
（或按实际失败原因弹窗）。

## 涉及文件

- `src/Companion.Desktop/CompanionLog.cs`（`FallbackError` 降级报错通路）
- `src/Companion.Desktop/Program.cs`（注册 FallbackError + 启动失败弹窗）
- `src/Companion.Desktop.Wpf/Program.cs`（同上）
- `src/Companion.Server/Program.cs`（配置路径中性注释）
