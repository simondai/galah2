# 分类条「＋」新建分类圆片（1.x 对齐）

日期：2026-11-21
状态：已实现，探针全绿

## 1. 需求分析

- **问题**：1.x 的分类条上有「＋」圆片可直接新建分类（横向布局在行尾、竖向布局在左栏底部）；
  2.x 的 `NewTabCommand` 已存在（右键菜单在用），但分类条上缺这个入口。
- **使用者**：所有用户；日常最高频的建类路径从「右键 → 新建分类」回到 1.x 的一键位置。
- **范围**：仅 UI 入口 + 探针。`NewTab` 逻辑（弹名称框 → 建类 → 选中）1.x 与 2.x 已一致，不动。
- **边界**：横向/竖向两种布局都要有；空名称不建类（现有 Prompt 逻辑已保证）；不引入新设置项。

## 2. 方案设计

1.x 结构（`Galah/Views/PanelWindow.xaml`）：标签条是流式 StackPanel，ItemsControl 之后挂一个
28×28 圆形 Button（`NewTabCommand`，`#26D2D2D7` 圆底，hover 加深，按下缩 0.9，悬停/按压动效，
ToolTip「新建分类」）。竖向时同一按钮 margin 换成 `11,6,0,4` 挂在列尾。

2.x 实现：
- 两个分类条 ItemsControl 各包进原尺寸/位置不变的 StackPanel（横向 Top、竖向 Left，
  原 Margin/Width 原样上移），ItemsControl 之后放 `Classes="newtab"` 的按钮。
- 按钮模板内联（28×28 圆 Border + 「＋」字形），hover/pressed 样式放 `Styles/Baseline.axaml`
  （`Button.newtab:pointerover /template/ Border#chip` 等）。
- ViewModel 零改动，Command 直接绑 `NewTabCommand`。

关键踩坑（实测）：
- 11.3.23 的 ToolTip 附加属性是 `ToolTip.Tip`，不是 `ToolTip`（AVLN2000）。
- 11.3.23 的 `Button.Click` 是事件不是方法；Command 在 protected `OnClick()` 里执行，
  外部 `RaiseEvent(ClickEvent)` 不会触发 Command —— 探针因此直接执行按钮的 Command
  （验证绑定接线），NewTab 逻辑本身已有独立断言。
- `StyledElement.GetClasses()` 在 11.3.23 不存在，用 `element.Classes` 属性。
- 分类条可见性切的是父 StackPanel 的 `IsVisible`（绑 `!IsVerticalTabs`/`IsVerticalTabs`），
  子控件的显式 `IsVisible` 不随父级变化 —— 断言要看父条。
- 布局走 `vm.ApplyLayout(...)`；`ApplySettings` 不投影 TabPosition。

## 3. 计划

单批提交：XAML（两处圆片 + 样式）+ PanelProbe 六条断言 + CHANGELOG + 本文档。
回滚：`git revert` 该提交。

## 4. 验收（PanelProbe，真实 PanelWindow）

| 断言 | 结果 |
|---|---|
| both strips carry the new-category chip | PASS [2 chip(s)] |
| chip sits on the visible strip | PASS |
| chip click asks for a name | PASS [新建分类] |
| chip click created a tab | PASS |
| chip click selected the new tab | PASS |
| chip follows the vertical rail | PASS [hStrip=False vStrip=True] |

全套 8 进程内探针 ALL PASS；AOT 发布 + PortableCheck/UiAcceptance 沿用 CI 门禁。
SettingsProbe 的 alt+space 断言在「用户正在手工测试（实例持有热键）」时必然失败，属环境因素。
