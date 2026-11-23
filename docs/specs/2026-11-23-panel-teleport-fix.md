# 面板"点设置后飞出桌面"修复 + 首次召唤错位（2026-11-23）

## 1. 需求

用户实测报告（干净数据，1920×1080 @100%）：

> 主程序面板 → 手动移动到右下角 → 添加分类正常 → 添加快捷方式正常 → 再点"设置"（☰ 菜单），
> 这时候主程序框会移出桌面，好像是跟随鼠标。

现场数据（artifact\GalahData，用户测试后留存）：

| 文件 | 值 | 说明 |
|---|---|---|
| settings.json | panelPlacement=remember, hideOnBlur=false | 排除失焦隐藏、排除 mouse 模式 |
| ui-state 序列 | (1164,543) → (1162,542) → **(1545,697)** | 最后位置 720×460：右边缘超屏 345px、下边缘超屏 125px，重叠 37.9% |

验收标准：
- AC-1 ☰ 菜单任意项点击后，面板位置不变（位置只能被"用户按住 chrome 拖动"改变）
- AC-2 干净数据首次召唤，面板**居中**（面板中心 = 工作区中心），而不是左上角落在屏幕中心
- AC-3 拖到屏幕边缘时面板完整留在工作区内（resize 路径本来就是完整夹紧，drag 路径对齐它）
- AC-4 AOT 绿色产物 + PortableCheck + UiAcceptance 全过

## 2. 方案（根因，全部实测/代码证实）

### 根因 A：拖拽状态残留 → 菜单点击触发"幽灵拖拽"（用户报告的 bug）

PanelWindow 的 chrome 拖拽是手写的（Avalonia 11 无现成方案，注释说明了为何用
Win32 GetCursorPos 而非 GetPosition）：

- `Chrome_PointerPressed`：空 chrome 左键按下 → 记 `_dragOriginScreen / _dragWindowOrigin`
- `OnPointerMoved`：只要 `左键按下（事件标志 && IsKeyDown）` 且光标距**上次记录的 origin**
  ≥4px，就把面板移动到 `_dragWindowOrigin + (光标 − origin)`
- **origin 是持久字段**：一次真实拖拽结束后不清零（只清 `_dragging` 标志）

故障链（☰ 菜单的 flyout 是独立 HWND）：

1. 用户之前真实拖过面板 → `_dragOriginScreen=P`、`_dragWindowOrigin=W0` 残留在内存
2. 点 ☰ → Button 吞掉 press（`Button.OnPointerPressed` 置 `e.Handled=true`，Avalonia 源码证实）
   → chrome 的 origin 不更新；flyout 弹出（独立窗口，面板 Deactivated）
3. 点"设置…"项 → flyout 在按下瞬间关闭 → **指针落在面板上、左键仍按住** →
   面板收到 button-down 的 PointerMoved → `IsKeyDown(LButton)=true` →
   距残留 origin ≥4px → **拖拽触发：面板跳到 W0 + (光标 − P)**

数值吻合：末次拖拽把面板从 W0 拖到 (1162,542)，即 光标终值 C = P + (1162−W0.x, 542−W0.y)。
设置项点击点 M ≈ (1885,711)（flyout 第 9 行，实测 flyout 143×367 @ 1501,544 量级一致）。
幽灵拖拽后位置 = (1162,542) + (M − C) = **(1545,697) = ui-state 实测值**。✓

同一缺陷在 resize 路径同样存在（`_resizeEdges` 也是按下才置、松开才清；若松键落在
flyout/面板外，面板收不到 release → 残留 → 下一次 button-down 移动直接 ResizeTo）。

### 根因 B：首次召唤位置 = 屏幕中心点（不是面板居中）

`AppRuntime.PlacePanel` 用 `_panel.Bounds.Width/Height` 算尺寸——**首次 Show 之前窗口从未
布局，Bounds = 0×0**。remember 模式无记忆时走 center 分支：
`x = area.CenterX − 0/2 = CenterX = 960, y = CenterY = 516` → **面板左上角落在屏幕正中**。
干净数据下复现 3 次，ui-state 均为 (960,516)。✓（用户"手动移到右下角"的起点就是它）

### 放大因素：30% 重叠阈值让半出屏位置被"记住"

`MonitorGeometry.ClampToScreens` 的规则（spec TL-D7，为"拔显示器恢复记忆"设计）：
重叠 ≥30% 就保留原位置。(1545,697) 重叠 37.9% → 保留 → 越界位置被持久化。
live 拖拽不应使用这条宽松规则（resize 路径就是完整夹紧）——本轮把 drag 路径改为严格夹进
工作区；restore 路径保持 30% 不动。

### 修复设计

**F1（根因 A）——显式手势状态机，替代"持久 origin + 移动时猜"**：

```
_dragArmed   只在"空 chrome 按下"时置 true
_resizeArmed 只在"边缘按下"时置 true
两者在以下任一时刻清零：
  - OnPointerReleased（面板内松键）
  - Deactivated（flyout/任何弹窗抢走激活 → 这正是本 bug 的触发窗口）
  - OnPointerMoved 且左键未按（既有行为）
OnPointerMoved：
  拖拽分支只在 _dragArmed 时运行
  resize 分支只在 _resizeArmed 时运行（_resizeEdges 仍作为方向掩码）
```

状态机抽成纯类 `DragArmState`（internal，无 UI 依赖），探针可以直接断言全部转移：
- arm → moved(pressed) → drag=true
- arm → deactivated → moved(pressed) → drag=false   ← 本 bug 的回归断言
- arm → released → moved(pressed) → drag=false
- 未 arm → moved(pressed) → drag=false              ← 残留 origin 不再起作用

**F2（根因 B）**：`PlacePanel` 用 `_panel.Width / _panel.Height`（DIP 属性，XAML 里就有，
永远非零）× RenderScaling，替换 `Bounds`。

**F3（放大因素）**：drag 路径的逐步夹紧从 `ClampToScreens`（30% 规则）换成"最近工作区
内严格完整夹紧"（新私有方法，单位处理显式：目标/工作区/结果全 device px）。resize 路径
已正确，不动。restore 路径（PlacePanel 恢复记忆）继续用 `ClampToScreens`，不动。

### 已知但本轮不修的（记录在案）

- `BestArea`/`ResizeTo` 在 >100% DPI 下存在 device px 与 DIP 混用（用户 100%，不可见）
- 边框窗口无 DWM 阴影（borderless-chrome 轮次已记录）

## 3. 计划

| # | 任务 | 验证 |
|---|---|---|
| 1 | `DragArmState` 纯状态机 + 接线 PanelWindow（F1） | 编译 + 探针 |
| 2 | `PlacePanel` 尺寸来源改 Width/Height（F2） | 干净数据首召唤 = 居中（探针/手工） |
| 3 | drag 严格夹紧（F3） | 探针：拖到角上 → 面板完整在工作区内 |
| 4 | 探针：DragArm 状态机 4 断言（并入现有探针套件，新建 tools/DragArmProbe） | 全 PASS |
| 5 | 重建 AOT 产物、删 pdb、PortableCheck + UiAcceptance | 全 PASS |
| 6 | 交付：清掉用户 off-screen 的 ui-state（(1545,697) 会被 restore 原样恢复，必须重置），用户手工回归 | 用户确认 |

步骤 6 说明（诚实交代）：单人开发，无独立 review。合成鼠标注入在本环境（Alma 终端上下文，
无交互输入桌面：SetCursorPos 返回 false、GetForegroundWindow=0，实测）**无法**端到端复现
用户流程；机制由代码路径 + 数据几何吻合证实，最终验收以用户手工回归为准。
