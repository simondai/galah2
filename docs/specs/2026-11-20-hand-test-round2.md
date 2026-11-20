# 手工测试第二轮修复

## 1. 需求
用户手工测试发现四个问题：

| # | 现象 | 期望 |
|---|---|---|
| 1 | 「添加快捷方式」窗的列表项只有文字 | 每项显示图标（参照 1.x：32px 图标 + 名称 + 备注） |
| 2 | 按住标题栏拖动仍然抖动 | 拖动跟手，无抖动 |
| 3 | 主窗口不能调整大小 | 可拖动边缘/角落缩放，尺寸被记住 |
| 4 | 重新唤出后位置没保存 | 位置和尺寸在关闭/隐藏后保留，下次唤出恢复 |

### 验收标准（可断言）
- A1：`SeedViewModel` 有 `Icon` 属性，加载到图标后 `HasIcon` 为真
- A2：拖动时窗口位置单调跟随指针，不出现反向跳变
- A3：`CanResize` 为真，且 `Width`/`Height` 随缩放写回 ui-state
- A4：隐藏→再显示后，`Position` 与上次一致（±2px）

## 2. 方案

### 问题 1 — 列表项加图标
`SeedViewModel` 增加 `Bitmap? Icon` + `bool HasIcon`，构造时异步调 `IconService.GetAsync(seed, 32)`。
XAML 用 1.x 的三段式布局：32px 图标（无图标时显示首字母占位）+ 名称 + 备注。

### 问题 2 — 拖动抖动
上一版改成"屏幕坐标 + 原点偏移"，仍抖。原因需实测确认：`e.GetPosition(null)` 在不同
Avalonia 版本行为不同；若它返回的是相对**内容**而非屏幕，窗口一移就反馈循环。
方案：改用 Win32 `GetCursorPos` 取绝对屏幕坐标做基准，完全不依赖 Avalonia 的坐标语义。

### 问题 3 — 可调整大小
`CanResize="False"` → `True`。无边框窗口需要自绘缩放手柄或用 `ExtendClientArea`；
先尝试直接开 `CanResize`，实测无边框下是否可用，不行再加手动 resize 手柄。

### 问题 4 — 位置/尺寸记忆
在面板 `Closing`（已有）和新增的 `PositionChanged`/`SizeChanged` 里写回：
`_uiState.Current.Window.X/Y/Width/Height`，然后 `_uiState.Save()`。
写入要防抖（拖动时坐标变化极频繁，不能每像素写一次盘）。

## 3. 计划
| 任务 | 依赖 | 验证 |
|---|---|---|
| 1 图标 | IconService 已有 | SettingsProbe/PanelProbe 加断言 + 截图 |
| 2 拖动 | 无 | 实测连续拖动，比较位置序列 |
| 3 缩放 | 无 | 实测能拉动边缘 |
| 4 位置记忆 | 3 | 隐藏→唤出，比较 Position |
