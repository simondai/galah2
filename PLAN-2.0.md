# Galah 2.0 — 规划与进度

> 实测数据见 **[FINDINGS-2.0.md](FINDINGS-2.0.md)**。本文只记计划与完成度。

---

## 1. 仓库布局

```
galah/    1.x 维护线，main 分支，已冻结（只接受 hotfix）
galah2/   2.0 开发线，v2 分支
```

- v2 分支已推送到 `github.com:simondai/galah`，`main` 保持 1.x 不变。
- CI：v2 用 `.github/workflows/build2.yml`（构建 + COM 探针 + AOT 发布 + AOT 冒烟测试），
  1.x 的 `release.yml` 只在 main 上。

---

## 2. 代码迁移分级（13,223 行的实际去向）

### A 类 — 纯逻辑，直接搬（已完成）

零 UI 依赖，改命名空间即可。已搬入 `src/Galah2/`：

```
Models/Catalog.cs                    Models/SettingsDocument.cs
Models/UiStateDocument.cs            Persistence/AppDataPaths.cs
Persistence/CatalogStore.cs          Persistence/JsonFileStore.cs
Persistence/SettingsStore.cs         Persistence/UiStateStore.cs
Services/NameOrder.cs                Services/TabNavigation.cs
Services/PanelPlacement.cs           Services/DialogGuard.cs
Services/DiagnosticsLog.cs           Services/ItemValidator.cs
Services/KindInference.cs            Services/UrlSafety.cs
Services/UrlFileParser.cs            Services/ProtocolSafety.cs
Services/HotkeyParser.cs             Services/ShortcutMaker.cs
Services/MaintenanceService.cs       Services/MigrationService.cs
Services/MonitorGeometry.cs          Services/Interop/Win32.cs
Services/LauncherService.cs          Services/LinkResolver.cs
Services/CatalogSources/ (五个源)
```

**额外改造**：JSON 全部改走 `Persistence/JsonContext.cs` 源生成器（AOT 必需）。

### B 类 — 换 UI 类型（部分完成）

| 1.x 文件 | 2.0 对应 | 状态 |
|---|---|---|
| `TrayIconService.cs` (250 行 Win32) | `Services/TrayService.cs` (~60 行) | ✅ Avalonia `TrayIcon` 直接覆盖 |
| `HotkeyService.cs` (HwndSource) | `Services/HotkeyService.cs` | ✅ 改为 message-only 窗口 |
| `SingleInstanceService.cs` (WM_COPYDATA) | `Services/IpcToggle.cs` | ✅ 改为命名 event |
| `PanelController.cs` | `Services/AppRuntime.cs` | ✅ 简化 |
| `ThemeService.cs` | `Styles/Baseline.axaml` | ✅ 静态样式，无运行时切换 |
| `IconService.cs` (494 行) | — | ❌ 未迁移 |
| `IconArt.cs` (397 行) | — | ❌ 未迁移 |
| `ImportService.cs` / `ToastService.cs` | — | ❌ 未迁移 |

### C 类 — XAML 全重写（部分完成）

| 1.x | 2.0 | 状态 |
|---|---|---|
| `PanelWindow.xaml` (487) | `Views/PanelWindow.axaml` (~70) | ✅ |
| `App.xaml` (210，含 ContextMenu 三模板) | `App.axaml` (~20) | ✅ 整段丢弃 |
| `Themes/Dark.xaml` + `Light.xaml` | `Styles/Baseline.axaml` | ✅ 合并为一份 |
| `SettingsWindow.xaml` (163) | — | ❌ |
| `EditItemWindow.xaml` (162) | — | ❌ |
| `AddPickerWindow.xaml` (148) | — | ❌ |
| `MaintenanceWindow.xaml` (145) | — | ❌ |
| `PromptWindow.xaml` (80) | — | ❌ |

### D 类 — 验收体系

1.x 的 25 条 acceptance 依赖 WPF UIA 树。2.0 目前只有两个真实工具：

- `tools/ComProbe` — COM 往返（11 断言）
- `tools/perf/measure.ps1` — 内存测量（固定操作序列）

**没有**迁移 1.x 的 UI 级验收，这是后续最大的缺口。

---

## 3. 剩余工作

按依赖排序：

1. **编辑 / 添加选择器** — 内容库现在只读：能读 1.x 数据，但 2.0 内不能增删改。
2. **库维护 / 导入导出** — 后端服务（`MaintenanceService` / `MigrationService`）已搬，缺 UI。
3. **UI 级验收工具** — Avalonia 的 UIA 暴露度弱于 WPF，需要新的验证策略。
4. **drag & drop、材质效果** — 1.x 有，2.0 未做。
5. **`.msc` 图标的 COM handler 路径** — 1.x 有一整套专门处理，2.0 只走了通用 shell 图标。

---

## 4. 已确认必须遵守的技术约束

这些是实测得出的，不是推测：

1. **`FluentTheme` 不可省**。没有主题包，`ItemsControl` 不生成容器，所有列表渲染为空。
2. **AOT 必须真实启动验证**。`dotnet publish` 成功 + IL3050 警告 都不代表能跑；
   JSON 反射会在建窗口之前抛异常，表现为"双击没反应"。
3. **COM 槽位索引是 ABI 契约**。1.x 的 `[ComImport]` 不能用于 AOT，
   手写 vtable 时错一位就写坏内存。
4. **测内存用 Private Working Set**。Working Set 含共享 DLL 页，虚高约一倍。
5. **不要承诺"隐藏后内存回落"**。WPF 和 Avalonia 都不回落（实测）。
