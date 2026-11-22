# 对话框全套无边框对齐（方案 1，取代同日轻量档）

日期：2026-11-22
状态：已实现，探针全绿
前置：2026-11-22-dialog-surface-alignment.md（轻量档只做底色，owner 实测后觉得"差点意思"）

## 1. 需求分析

- **问题**：轻量档只换了底色，对话框仍背系统标题栏，与面板（无边框 + 自绘标题栏 +
  圆角表面）仍有质感差。owner 决定上方案 1：全套对齐。
- **范围**：6 个对话框（设置/添加/编辑/确认/输入/维护）全部改无边框语法。
- **边界**：
  - 保留各自 CanResize / ShowInTaskbar / 定位语义（Confirm/Prompt 居中于 owner、不入任务栏；
    Maintenance 保持可 resize——无边框后无系统 resize 边，与面板同级待遇）
  - 关闭语义不变：所有对话框在 OnClosed 里 settle 回调，标题栏 ✕ 走 Window.Close
    即自动 settle（Confirm/Prompt 视为取消，EditItem 默认 Outcome 本就是 Cancelled）
  - 窗口高度 +34（自绘标题栏 34px 占位）

## 2. 方案设计

- 语法与 PanelWindow 完全一致：`SystemDecorations="None"` + `Background="Transparent"`
  + `RootBorder`（10px 圆角 + 发丝线 + DialogSurfaceBrush 表面）
  + 标题栏 Border（ElevatedBrush，34px，CornerRadius 10,10,0,0）。
- 标题绑定窗口自己的 `Title`（`RelativeSource AncestorType=Window`），
  EditItem 的动态标题照旧。
- 共享行为 `Views/DialogChrome.cs`：`Drag`（左键 → `Window.BeginMoveDrag(e)`）
  + `Close`（→ `Window.Close`）。
  实测坑：Avalonia 11 没有 WPF 式的无参 `DragMove()`；XAML 事件接线只接受
  x:Class 的实例方法，**不接受静态类方法**（AVLN3000），所以每个 code-behind
  加两行转发（`TitleBar_Drag`/`TitleBar_Close` → DialogChrome.*）。
- 结构坑：XAML 里 `Window.Styles` 是属性段，必须出现在子内容**之前**——
  EditItem/Maintenance 原有 Window.Styles 块在改造后落到了内容中间，编译报
  "Unable to resolve property Styles"，挪回窗口属性区解决。

## 3. 计划

单批提交：DialogChrome.cs + 6 XAML + 6 code-behind 转发 + DialogProbe 断言升级
+ CHANGELOG + 本文档。回滚：`git revert`。

## 4. 验收（实测）

- DialogProbe 全过，含新断言：
  - `dialog window is borderless [None]`
  - `dialog surface is the panel fill on the RootBorder`（引用相等）
  - `chrome carries a close button`
  - `chrome close settles as dismiss [False]`
- 全套 8 进程内探针 ALL PASS（SettingsProbe 的 combo 几何断言在带标题栏的
  新窗口下不受影响）

## 5. 遗留观察

- 无边框窗口没有 OS 阴影（面板同样如此），如后续觉得"飘"，可考虑 DWM 阴影 API，
  属于体验增强不属对齐范围
- Maintenance 可 resize 但无边框 resize 边：当前行为与面板一致（面板的 resize 是
  自绘边区），Maintenance 暂未移植边区逻辑，实际很少拖它的边
