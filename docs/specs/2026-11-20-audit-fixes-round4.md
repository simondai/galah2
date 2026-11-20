# 审计修复轮次 4：滚轮路由、死设置接线、平台标注与 API 迁移

**日期**: 2026-11-20
**输入**: AUDIT-REPORT.md（对照复核后）+ 复核新发现的两个 P0
**基准**: HEAD @ ead855c

---

## 1. 需求分析（步骤 1）

### 解决什么问题

| # | 问题 | 来源 | 严重度 |
|---|------|------|--------|
| R1 | 磁贴列表超过一屏后无法滚动：Window 层 Tunnel 滚轮 handler 无条件 `Handled=true`，`ScrollContentPresenter` 的类处理器（`handledEventsToo=false`）永远不执行 | 复核新发现（对照实验：A 组 offset 0，B 组 600） | P0 |
| R2 | "滚轮切换分类"/"Ctrl+数字切换分类"两个设置开关无效（View 层从未读设置；VM 无投影） | 审计 2.1/2.2，复核确认（审计给的修法编译不过——VM 无字段） | P0 |
| R3 | 滚轮未实现 1.x 的"边缘才切换"语义 | 审计 2.3 | P0（与 R1 同一处代码） |
| R4 | 小键盘 Ctrl+数字不响应（1.x 支持 NumPad0-9） | 审计 3 | P0 |
| R5 | `DialogGuard` 死代码 | 审计 1.1 | 用户拍板：面板隐藏时 owned 对话框**应该**跟着消失（当前实测行为即是）→ 无需接线，删除并在文档记录设计决策 |
| R6 | `PanelPlacement.AreaFor` 死代码，且多屏回退语义与 `PlacePanel` 内联逻辑不一致（primary 优先 vs areas[0]） | 审计 1.3 + 复核 | 用户拍板：多屏回退 **primary 优先** → 接线 AreaFor，删内联逻辑 |
| R7 | 37 条 CA1416 平台警告（csproj 无 SupportedOSPlatform） | 复核新发现（审计漏报 79% 的警告） | P1 |
| R8 | 10 条 CS0618 过时拖放 API（DataObject/DataFormats/DoDragDrop/DragEventArgs.Data） | 审计 4 | P1（已实测 11.3.23 存在全部替代 API） |
| R9 | OnDrop 一处 `Console.Error.WriteLine` 残留 | 审计 7 | P1 |
| R10 | app.ico 与 Assets/galah.ico 内容相同（MD5 一致） | 审计 6（方向纠正：删 app.ico 而非 galah.ico，因 avares 只打包 Assets/**） | P2 |
| R11 | `SelectTabByIndex` 1-based 与 1.x 0-based 语义差异无注释 | 审计 5 | P2 |
| R12 | 材质设置文案"不透明/半透明"无法表达实际差异（91% vs 100% alpha） | 审计存疑 3 + 用户拍板 | P2 |
| R13 | 方向键切分类 + Ctrl+Tab 回绕实际是死功能：`KeyboardNavigationHandler` 在窗口 Bubble 路由先处理 Tab/箭头并标记 Handled，Bubble 注册的 KeyDown 收不到。与 R1 同类——审计完全未发现 | 新探针实测（开发中发现） | P0 |

### 使用者与边界

- 使用者：Galah2 全体用户（开源版）。
- 边界条件：
  - 滚轮边缘判定只在磁贴区 ScrollViewer 上生效；分类条、chrome 区的滚轮不切分类（1.x 同）。
  - 关闭两个开关后，滚轮只滚动列表、数字键完全无响应（1.x 同）。
  - HideOnBlur 开启 + 面板失焦 → 面板与 owned 对话框一起隐藏，为**决策后的期望行为**，不修。
  - CA1416 消除方式是声明平台（`SupportedOSPlatform`），不是改代码——产品本就是 Windows-only。

### 验收标准

1. 新探针（真实窗口 + 模板内 ScrollContentPresenter，不是 VM 方法直调）：
   - 列表溢出时滚轮使 `Offset.Y` 增加；
   - 未到边缘时滚轮**不**切分类；到边缘时才切（含回绕）；
   - `WheelSwitchTab=false` 后边缘滚轮不再切分类（但列表仍可滚动）；
   - `NumberKeysSwitchTab=false` 后 Ctrl+数字无效；
   - Ctrl+NumPad3 与 Ctrl+D3 等效（选第 3 个分类）；
   - Ctrl+D0 不选任何分类（DigitToIndex 刻意不映射 0）。
2. 构建（Release）**0 警告 0 错误**（当前 47 警告：37 CA1416 + 10 CS0618）。
3. 原 9 个探针全绿（回归）。
4. 重新 AOT 发布后 exe 图标仍存在（PE 资源表 RT_ICON>0，主色 #F25080）——图标去重不得破坏 ApplicationIcon。
5. AOT 发布产物真实启动一次（记忆里的教训：JSON 反射在建窗口前就抛）。
6. `grep -rn "DialogGuard" src tools` 为空；`grep -rn "AreaFor" src tools` 恰有一处调用。

## 2. 方案设计（步骤 2）

### R1+R2+R3+R4：输入路由重做（同一片代码，一次改对）

**滚轮**：
- `PanelWindow.axaml`：磁贴区 ScrollViewer 命名为 `TileScroll`，挂 `PointerWheelChanged="TileScroll_Wheel"`（Bubble，普通实例 handler——只影响该控件，Window 层 Tunnel handler 删除）。
- `PanelWindow.axaml.cs`：`TileScroll_Wheel` 对齐 1.x PanelWindow.xaml.cs:194-204：
  ```
  if (!vm.WheelSwitchTab) return;          // R2
  var atTop = scroll.Offset.Y <= 0;
  var atBottom = scroll.Offset.Y + scroll.Viewport.Height >= scroll.Extent.Height - 0.5;
  var edge = direction > 0 ? atBottom : atTop;
  if (!edge) { /* 不 Handled，让 presenter 正常滚动 */ return; }
  vm.SwitchTabRelative(direction); e.Handled = true;
  ```
  关键：不在边缘时**不设 Handled**，事件冒泡到 presenter 的类处理器正常滚动（R1 的修复本体）。
- 等等——实例 handler（Bubble）先于类 handler 吗？Avalonia 11 路由：类 handler 在实例 handler **之前**执行（同一路由段）。ScrollContentPresenter 是 ScrollViewer 的**子**元素，事件从 presenter 起源，先在 presenter 走自己的类 handler（滚动并 Handled），再冒泡到 ScrollViewer 的实例 handler。所以列表还能滚时 presenter 已 Handled，ScrollViewer 上的实例 handler 默认不吃已处理事件……不对，实例 handler 默认也不吃 Handled。
  
  **修正方案**：handler 挂 ScrollViewer 用 `RoutingStrategies.Bubble` + `handledEventsToo: true` 的 AddHandler 重载。这样：presenter 滚动了（设 Handled）→ handler 仍被调用，但看到 `e.Handled==true` 直接返回（说明列表滚动了）；presenter 没滚动（已在边缘，或无溢出）→ handler 看到未 Handled，做边缘判定切分类。这天然就是"边缘才切换"！1.x WPF 的 PreviewMouseWire 同构。加 `if (e.Handled) return;` 开头。
- 边缘判定读 ScrollViewer 的 Offset/Viewport/Extent（逻辑像素，无需换算）。

**数字键**（OnKeyDown 内）：
```
if (e.KeyModifiers.HasFlag(Control) && vm.NumberKeysSwitchTab) {
    var digit = e.Key switch { >= D0 and <= D9 => e.Key - D0, >= NumPad0 and <= NumPad9 => e.Key - NumPad0, _ => -1 };
    var index = TabNavigation.DigitToIndex(digit);
    if (index >= 0 && vm.SelectTabByIndex(index + 1)) e.Handled = true;
}
```
（TabNavigation 是 0-based，SelectTabByIndex 2.0 是 1-based，衔接处 +1，注释写明。）

**VM 投影**：`ApplySettings` 补 `_wheelSwitchTab/_numberKeysSwitchTab` 字段 + 公开属性 + Raise 列表。`SwitchTabRelative` 改用 `TabNavigation.StepIndex`（R1.2 死代码复活）。

**DialogGuard（R5）**：删除文件。PanelWindow Deactivated 保持现状（行为已是期望）。设计决策记录在本文档与 CHANGELOG。

**AreaFor（R6）**：`PlacePanel` 内联 `WorkAreaAtCursor() ?? areas[0]` 改为 `PanelPlacement.AreaFor(cursor)`（cursor → primary → areas[0]）。AreaFor 里 `WorkAreaAtCursor()` 重复调 GetCursorPos 一次——语义等价（cursor 非 null ⇔ 调用成功），不改签名，避免为接线而重写。

### R7：平台标注

csproj `<PropertyGroup>` 加 `<SupportedOSPlatform>windows</SupportedOSPlatform>`（需 `Microsoft.NET.Sdk` 自带的 analyzer 支持；若 SDK 版本要求 using，直接加即可验证：CA1416 归零）。

### R8：DataTransfer 迁移（已反射确认全部 API）

| 旧 | 新（11.3.23 实测存在） |
|----|----|
| `new DataObject()` + `Set("galah/item-id", id)` | `new DataTransfer()` + `.Add(DataTransferItem.Create(GalahItemFormat, id))`，`GalahItemFormat = DataFormat.CreateStringApplicationFormat("galah/item-id")`（static readonly） |
| `await DragDrop.DoDragDrop(e, data, effects)` | `await DragDrop.DoDragDropAsync(e, dataTransfer, effects)` |
| `e.Data.Contains("galah/item-id")` | `e.DataTransfer.Contains(GalahItemFormat)`（DataTransferExtensions） |
| `e.Data.Contains(DataFormats.Files)` | `e.DataTransfer.Contains(DataFormats.Files)`（若 Contains 无 string 重载则用 `TryGetFiles() is not null`，编译期定） |
| `e.Data.GetFiles()` | `e.DataTransfer.TryGetFiles()` → `IStorageItem[]?` |
| `e.Data.GetText()` | `e.DataTransfer.TryGetText()` → `string?` |

### R9：`Console.Error.WriteLine` → `DiagnosticsLog.Line("drop", ...)`

### R10：删 `src/Galah2/app.ico`，`<ApplicationIcon>Assets/galah.ico</ApplicationIcon>`。发布后重测 PE 图标（验收 4）。

### R11：`SelectTabByIndex` XML doc 注明"1-based；1.x 同名方法是 0-based，移植代码时注意"。

### R12：`MaterialOptions` = `["不透明（100%）", "半透明（91%，同 1.x）"]`。

### 风险

- **滚轮 handledEventsToo 方案依赖 Avalonia 类/实例 handler 顺序**（类 handler 先跑）。新探针必须先证"列表能滚"再证"边缘切换"，顺序颠倒会假绿。
- DataTransfer 自定义格式两端（拖源/落点）须共享同一 `DataFormat` 实例（static）。
- `SupportedOSPlatform` 可能连带触发新的警告形态（如 CA1416 变体），以构建输出为准。
- 图标改动影响发布产物，必须走 AOT 真实启动验收。

## 3. 计划（步骤 3）

| 批次 | 内容 | 提交 |
|------|------|------|
| C1 | R1-R4 输入路由 + VM 投影 + R5 删 DialogGuard + R6 AreaFor 接线 + 新探针 InteractionProbe（含全部 R1-R4 断言） | `fix(2.0): wheel/number-key routing honours settings and restores scrolling` |
| C2 | R7 SupportedOSPlatform + R8 DataTransfer 迁移 + R9 日志 + 探针补拖放断言 | `chore(2.0): zero-warning build (platform annotation + DataTransfer migration)` |
| C3 | R10 图标去重 + R11 注释 + R12 文案 + CHANGELOG/README 文档同步 | `chore(2.0): dedupe icon, material labels, doc sync` |

- 每批：改码 → 构建 0 警告 → 全探针绿 → 提交。
- C2 后跑一次 AOT 发布 + 启动探针；C3 后再跑一次（图标验收）。
- 工作量：C1 约 2h（探针是大头），C2 约 1h，C3 约 0.5h。
- 步骤 6（Review）：单人项目，无独立 reviewer；以探针 + 构建门禁替代，如实记录"无独立 review"。
- 回滚方案：三个提交独立可 revert；C1 revert 后回到现状（列表不可滚），C2/C3 revert 无功能影响。

## 决策记录（用户拍板 2026-11-20）

1. **owned 对话框随面板隐藏**：是期望行为。面板收起 = 整个会话收起。DialogGuard 删除。
2. **多屏回退 primary 优先**：AreaFor 语义为准，接线。
3. **材质文案**：实现不动，选项文案表达 91% vs 100%。
