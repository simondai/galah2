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



## 3.1 本轮完成 — 功能面基本对齐 1.x

| 功能 | 状态 |
|---|---|
| 面板标题栏（☰ 菜单、关闭、拖动） | 完成 |
| 分类条、条目网格、右键菜单（打开/编辑/左右移/删除） | 完成 |
| 分类增删改排序、滚轮与 Ctrl+1..9 切换、Esc 收起 | 完成 |
| 保存失败横幅 + 重试（INV-9） | 完成 |
| 编辑窗口（名称/目标/参数/工作目录/管理员/图标/类型建议/校验门） | 完成 |
| 添加选择器（4 个来源、搜索、多选、一次提交） | 完成 |
| 库维护（扫描/清失效/去重/排序/重建图标缓存/导出/导入） | 完成 |
| 提示与确认对话框 | 完成 |
| 拖放文件、粘贴 URL | 完成 |
| 设置窗口（快捷键/图标大小/交互开关/自启/数据目录） | 完成 |
| 分类竖排布局（top / left） | 完成，已实测 |
| 图标尺寸三档（24 / 40 / 56 DIP） | 完成，已实测 |

验收：ComProbe 11/11、IconProbe 10/10、SettingsProbe 9/9、PanelProbe 40/40。
AOT：26.4 MB，待机 18.6 MB，面板 71.8 MB（大图标 + 竖排实测 82 MB）。

## 3.2 仍与 1.x 有差距

1. **UI 级验收** — 1.x 有 25 条 UIA 驱动的进程级验收；2.0 目前是命令面探针加像素断言，
   缺真实鼠标/键盘的端到端覆盖。
2. **材质效果（Mica/亚克力）** — `TransparencyLevelHint="Mica"` 已设但未验证是否生效。
3. **`.msc` 图标的 COM handler 路径** — 1.x 有专门的 STA handler，2.0 只走通用 shell 图标，
   这类管理单元会拿到灰白占位图。

## 4. 已确认必须遵守的技术约束

这些是实测得出的，不是推测：

1. **`FluentTheme` 不可省**。没有主题包，`ItemsControl` 不生成容器，所有列表渲染为空。
2. **AOT 必须真实启动验证**。`dotnet publish` 成功 + IL3050 警告 都不代表能跑；
   JSON 反射会在建窗口之前抛异常，表现为"双击没反应"。
3. **COM 槽位索引是 ABI 契约**。1.x 的 `[ComImport]` 不能用于 AOT，
   手写 vtable 时错一位就写坏内存。
4. **测内存用 Private Working Set**。Working Set 含共享 DLL 页，虚高约一倍。
5. **不要承诺"隐藏后内存回落"**。WPF 和 Avalonia 都不回落（实测）。
