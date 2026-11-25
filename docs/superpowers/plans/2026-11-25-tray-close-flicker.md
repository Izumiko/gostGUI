# Tray Close Flicker Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use subagent-driven-development (recommended) or executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 点击主窗口 × 时无闪烁地收进托盘，并能正常恢复。

**Architecture:** 保留 `FormClosing` 和托盘恢复的事件流，只用窗体可见性来收起和显示窗口；不再切换任务栏属性。

**Tech Stack:** C#、.NET Framework 4.8 WinForms、现有 `--selftest`。

**Spec:** `docs/superpowers/specs/2026-11-25-tray-close-flicker-design.md`

## Global Constraints

不改配置提交、位置保存、后台代理或真正退出，不加运行时依赖。

## Review Focus

- 收起仍应让可见窗口隐藏，且不能销毁它的句柄；从托盘恢复后窗体应重新可见。由新增自检覆盖。
- 重复关闭、恢复不应重建句柄。由同一自检跑两个循环覆盖。
- 关闭时仍保存编辑和窗口几何；由现有关闭、保存与退出自检覆盖。

### Task 1: 收起与恢复不重建窗口

**Files:** `gostGUI/SelfTest/SelfTest.Window.cs`（用例）、`gostGUI/SelfTest/SelfTest.Core.cs`（注册）、`gostGUI/Form/FormMain.Tray.cs`（修复）。

**Interfaces:** 现有 `FormMain.Close()` 和 `RestoreAndBringToFront()`；拆出私有 `RestoreTrayVisibility()`，由生产恢复流程与自检共用。自检不调用窗口置顶或键盘输入。

- [x] 在现有 STA 夹具里用临时配置创建主窗体并 `Show()`，订阅 `HandleDestroyed`，捕获原始 `Handle`；用 `Close()` 触发收起，用反射调用生产恢复流程共用的 `RestoreTrayVisibility()`；测试窗体透明、不激活，也不创建托盘图标。重复两次，每半轮断言没有销毁句柄、句柄仍等于原始值，收起后不可见且未释放，恢复后可见；在销毁测试窗体前完成断言。以 `Form_TrayRoundTrip_KeepsWindowHandle` 登记。
- [x] 构建 Release：`& 'C:\Program Files\Microsoft Visual Studio\18\Professional\MSBuild\Current\Bin\MSBuild.exe' gostGUI.sln /t:Build /p:Configuration=Release /v:m /nologo`。运行 `Start-Process .\gostGUI\bin\Release\gostGUI.exe -ArgumentList '--selftest' -Wait -PassThru`；检查 `gostGUI/bin/Release/selftest.log`，预期新增用例因 `HandleDestroyed` 失败。
- [x] 在 `FormMain_FormClosing` 和 `RestoreAndBringToFront` 中仅删除两处 `ShowInTaskbar` 赋值，保留其余逻辑。
- [x] 重复 Release 构建、自检并检查日志，检查最终差异。实测 Release 构建成功，新增用例修复前因句柄销毁失败，修复后通过。修复后共 131 项，唯一失败仍是修改前已失败的 `Form_LayoutAt120And144Dpi_IsNotClipped`（期望 1024，实际 1028）。
