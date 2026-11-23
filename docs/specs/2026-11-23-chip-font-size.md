# 分类名字号设置（2026-11-23）

## 1. 需求

用户要求：设置里允许调节**分类名**（分类条圆片上的标签）的字号。

## 2. 方案（沿图标大小的既有模式）

- 三档，不做自由输入：与 IconSizes 同一设计理据（AC-14）——分类条是面板导航
  骨架，随意字号会撑坏行高。
- 档位 DIP：小 11 / 中 12（现状，全局 Button 字号）/ 大 14。
- 存储：`settings.json` 新增 `tabFontSize`（"small"|"medium"|"large"，缺省 medium；
  老文件无此字段 → 反序列化得默认值，无需迁移）。Sanitized() 归一化非法值。
- 设置窗口：外观分组新增下拉「分类名字号」（小/中/大），位于图标大小之后。
- 生效路径：`PanelViewModel.ApplyLayout` → `ApplyChipFontSize`（仅 Raise，不重投影；
  字号是绑定驱动的，圆片 XAML 绑 `ChipFontSize`）。设置变更回调已调用 ApplyLayout，
  故即时生效，不必重开面板。
- 影响范围：横向与竖向两个分类条上的圆片标签；标题栏 ☰/✕ 按钮自带字号，不受影响；
  新建分类的 ＋ 圆片是 28×28 定宽圆，不受影响。

## 3. 顺手修复

`SettingsDocument.Clone()` 漏了 `ShowAddTile`（上一轮 6589ad8 引入）：内存快照来自
Clone，重启后「显示添加占位磁贴」开关会丢失。本次把 `ShowAddTile` 和
`TabFontSize` 一并补进 Clone。

## 4. 验收

- AC-1 设置窗口外观分组出现「分类名字号」下拉，三档可选，缺省「中」
- AC-2 切换档位后已打开面板的圆片标签字号即时变化（11/12/14），无需重开面板
- AC-3 重启后档位保持（含上轮开关的持久化修复）
- AC-4 ☰/✕ 按钮与 ＋ 新建圆片外观不变

验证：PanelProbe 新增 6 条断言（默认 12、三档映射、窗口级实时跟随），全过；
8 套 in-process 探针全过；AOT 重建后 PortableCheck + UiAcceptance 全过。
