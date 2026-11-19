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

## 3.2 与 1.x 的覆盖对照

功能面已对齐，验收方式不同：

| 1.x 验收 | 2.0 对应 | 状态 |
|---|---|---|
| 托盘消息窗存在、左键激活面板 | UiAcceptance：toggle 后窗口出现且尺寸正确 | 完成 |
| 首次启动留可达入口 | UiAcceptance：托盘常驻 + 关闭后仍存活 | 完成 |
| 右键菜单弹出 | 未覆盖（需要真实鼠标点击托盘） | 缺 |
| 设置窗/选择器/维护窗可打开 | SettingsProbe + PanelProbe 走命令面 | 完成 |
| 真实鼠标退出链 | 未覆盖 | 缺 |
| 面板位置切换 | PanelProbe 验证放置函数 | 完成 |

五个验收工具的断言数：ComProbe 11、IconProbe 12、SettingsProbe 9、PanelProbe 40、
UiAcceptance 11。

**本轮 UiAcceptance 抓到的两个真实进程边界 bug**（探针层面看不到）：

1. 第二实例转发 toggle 后走 `desktop.Shutdown()`，此时生命周期还在启动中，
   主循环随即抛 `the Dispatcher shut down`。改为直接退出进程。
2. 面板点 ✕ 后 `Closed` 里 `Hide()` 太晚——Avalonia 的窗口一旦真正关闭就不能再显示，
   下一次热键抛 `Cannot re-show a closed window`，表现为"程序还在但叫不出来"，
   正是 1.x 那个死程序 bug 换了个形态。改为在 `Closing` 里取消关闭并隐藏。


## 3.3 本轮收尾：材质与 .msc 的实测结论

这两项原本记在“仍与 1.x 有差距”，本轮查实后结论与 1.x 的记录**不一致**。

### 材质（Mica）

1.x 记录：「本 RDP 会话不合成 DWM 材质」，因此当时把真 Mica 记为不可验证限制。

2.0 实测：`actual=Mica`（窗口自己报告的实际值，不是请求值），且屏幕像素在不同位置
取到不同颜色（中心 `27,27,31`、圆角外 `42,42,46`），说明 DWM 的材质确实合成进来了。

结论：**在这台机器上 Mica 是生效的**，1.x 那条限制没有复现。面板底色改成
`#E6232327`（90% 不透明）让材质透出来；原来写死的 `#232327` 把材质完全挡住了，
等于请求了却看不见。

### `.msc` 图标

1.x 记录：「现代 .msc 内嵌 PNG 压缩图标，`ExtractIconExW` 返回 0、`SHGetFileInfoW`
回落灰白占位图，只有 COM handler 能取到」。

2.0 实测（三个 .msc 逐一对照 handler 与通用路径）：

| 文件 | handler 饱和像素 | 通用路径饱和像素 | 结论 |
|---|---|---|---|
| services.msc | 217 | 217 | 一致 |
| devmgmt.msc | 3 | 4 | 一致（该图标本身就是近灰度设计） |
| diskmgmt.msc | 2 | 2 | 一致 |

结论：**两条路径结果等同，通用 `SHGetFileInfoW` 已经能取到真实图标**，1.x 那条
「必须走 COM handler」在当前环境不成立。handler 路径仍然保留（它是 Explorer 自己的
做法，成本可接受，且有 4 秒超时守卫），但它不是这些图标能显示的原因。

## 4. 已确认必须遵守的技术约束

这些是实测得出的，不是推测：

1. **`FluentTheme` 不可省**。没有主题包，`ItemsControl` 不生成容器，所有列表渲染为空。
2. **AOT 必须真实启动验证**。`dotnet publish` 成功 + IL3050 警告 都不代表能跑；
   JSON 反射会在建窗口之前抛异常，表现为"双击没反应"。
3. **COM 槽位索引是 ABI 契约**。1.x 的 `[ComImport]` 不能用于 AOT，
   手写 vtable 时错一位就写坏内存。
4. **测内存用 Private Working Set**。Working Set 含共享 DLL 页，虚高约一倍。
5. **不要承诺"隐藏后内存回落"**。WPF 和 Avalonia 都不回落（实测）。
