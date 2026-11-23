# ＋ 磁贴菜单灰死修复 + 分类/磁贴拖拽排序（2026-11-23）

## 1. 需求

用户手工回归（截图证实）：

1. **bug**：＋ 磁贴弹出的 4 项菜单全部灰色不可点；列表空白处右键菜单正常
2. **新功能**：分类条上的分类可以拖动改顺序
3. **新功能**：分类内已添加的图标（快捷方式/文件/文件夹/网址）可以拖动改顺序
4. **约束**：＋ 磁贴不可拖、永远末位（拖到它上面 = 挪到末位）

验收：
- AC-1 ＋ 磁贴菜单 4 项可用（Command 绑定解析成功，非灰色）
- AC-2 分类拖动换位，落点即目标位，持久化
- AC-3 磁贴拖动换位（同分类内），落点即目标位，持久化；跨分类仍走"拖到分类圆片"（已有）
- AC-4 ＋ 磁贴不可发起拖拽；拖到 ＋ 磁贴 = 移到该分类末位
- AC-5 全套探针 + AOT 产物 + PortableCheck/UiAcceptance 全过

## 2. 方案

### F1 菜单灰死（根因，Avalonia 源码证实）

`MenuItem` 源码：Command 绑定 `BindingError` → `_commandBindingError=true` →
`_commandCanExecute=false` → `IsEnabledCore` 禁用 → 灰。
`MenuFlyout` 的项不在控件逻辑树里（popup 独立宿主），`$parent[ItemsControl]` 沿逻辑树
向上找——找不到 → BindingError。磁贴自己的 `ContextMenu` 正常，因为 ContextMenu 是
button 的逻辑子级，链条完整。☰ 的 MenuFlyout 正常是因为它用直接 `{Binding AddPickerCommand}`
（flyout DataContext 继承 placement target = PanelViewModel），不依赖 $parent。

修法：＋ 磁贴改用 `Button.ContextMenu`（与磁贴菜单同模式，$parent 语法可用），
`Click` 事件里 `menu.IsOpen = true` 提供左键打开。右键行为保留（同一菜单）。

### F2 分类拖动排序

- 新拖拽格式 `TabIdFormat = "galah.tab-id"`（与 ItemIdFormat 同规则）
- 分类圆片加 `PointerPressed/Moved`：8px 阈值后发起 `DoDragDropAsync`（与磁贴同模式；
  阈值保证普通点击仍走 SelectTabCommand）
- `TabChip_DragOver` 扩为接受 item-id 或 tab-id
- `TabChip_Drop`：tab-id → `vm.MoveTabToIndex(dragged, IndexOf(target))`；item-id → 原逻辑
- `CatalogStore.MoveTabToIndex(tabId, targetIndex)`：重排 + 连续重编号 + 一次 Commit
  （现有 MoveTab 是相邻交换，拖动需要任意距离，语义不同，新方法是正解）

### F3 磁贴拖动排序

- 磁贴 button 加 `DragDrop.AllowDrop + DragOver/Drop`：item-id → `vm.MoveItemToIndex(itemId, 目标显示序号)`
- `CatalogStore.MoveItemToIndex` **已存在**（左移/右移命令在用），直接经 VM 暴露
- ＋ 磁贴也接受 drop：目标序号 = `Items.Count - 1`（末位），满足"＋ 永远末位、拖到它=移到末尾"
- ＋ 磁贴不可发起拖拽：上一轮已在 `Tile_PointerPressed` 挡掉（哨兵不进 _dragTile）
- 哨兵 Id="add-tile" 不在数据里：`MoveItemToIndex("add-tile")` 在 store 层 FindItem 失败
  自然拒绝（防御已覆盖，探针断言）

### 契约

- 显示投影序号 = 数据序号（真实条目在前、哨兵恒末位，SelectTab 保证），拖动换算直接用
  `vm.Items.IndexOf(target)`，无需减偏移
- `vm.Items.Count` 含哨兵（上轮已定），探针继续按 !IsAddTile 过滤

## 3. 计划

| # | 任务 | 验证 |
|---|---|---|
| 1 | ＋ 磁贴 Flyout→ContextMenu + AddTile_Click | 探针断言 4 项 Command 非 null |
| 2 | CatalogStore.MoveTabToIndex + VM 两个包装 | 探针：换位 + 持久化 |
| 3 | XAML：圆片拖拽事件、磁贴 Drop、＋ 磁贴 Drop | 编译 + 视觉树断言 |
| 4 | 代码：TabChip_Pointer*、Tile_DragOver/Drop、格式 | 编译 |
| 5 | PanelProbe 新断言（含哨兵拒绝） | 全 PASS |
| 6 | AOT 重建、pdb、PortableCheck/UiAcceptance | 全 PASS |
| 7 | 用户手工回归（真实鼠标拖分类/磁贴/＋） | 用户确认 |

单人开发，无独立 review。拖动本身是真实指针手势，探针只能锁状态机与数据层；
交互手感以手工回归为准。
