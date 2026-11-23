# 添加条目的两个入口（2026-11-23）

## 1. 需求

用户确认的方案（两轮界面稿）：

- 入口 1：**磁贴区空白处右键** → 弹 4 项菜单（添加快捷方式…／添加文件…／添加文件夹…／添加网址…），加到当前分类
- 入口 2：**每个分类的磁贴网格末尾有一个 ＋ 磁贴**，点击弹同一份 4 项菜单
- 分类行末尾原有的圆形 ＋（新建分类）职责不变，保持不动
- **2026-11-23 追加（用户确认）**：＋ 磁贴改为设置开关控制——设置-交互新增
  「显示"添加"占位磁贴（分类末尾）」，**默认关闭**；开启即时重投影，不必重开面板。
  关闭时添加走空白右键 / ☰ 菜单，空分类为纯空白。

边界条件：
- 开关开启时，空分类也要有 ＋ 磁贴（新建的分类能直接加东西，不必绕道 ☰）；
  开关关闭时空分类为纯空白，添加走空白右键 / ☰ 菜单
- ＋ 磁贴跟随最后一个磁贴流式排布，换行时跟在行尾
- 右键磁贴本身仍是该条目的编辑菜单，两个菜单不互相抢
- ＋ 磁贴不可拖拽、不可启动、不进持久数据

验收：
- AC-1 空白右键弹菜单，4 项都能正常打开各自的对话框并写入当前分类
- AC-2 ＋ 磁贴在每个分类末尾出现（含空分类），点击弹同一份菜单
- AC-3 ＋ 磁贴不出现在 catalog.json，不参与拖拽排序，LaunchTile 拒绝它
- AC-4 右键磁贴只弹条目菜单，不双弹
- AC-5 全套探针 + AOT 产物 + PortableCheck/UiAcceptance 全过

## 2. 方案

### 命令层：零新增

4 个命令已经在 `PanelViewModel`（`AddPickerCommand`/`AddFileCommand`/`AddFolderCommand`/`AddUrlCommand`，均走 `GuardTab(t => …)`，语义就是"加到当前分类"），☰ 菜单已在用。本轮纯 UI。

### 入口 1：ScrollViewer.ContextMenu

挂在 `TileScroll` 上，`ContextMenu` 4 项绑上述命令。Avalonia 的 `ContextMenu` 赋值即自动响应右键；磁贴自己的 `Button.ContextMenu` 在命中磁贴时优先并置 handled，父级不会再弹（AC-4 由手工回归确认——合成输入在本环境不可用，见 MEMORY）。

### 入口 2：哨兵磁贴（显示投影层，非数据层）

`Items` 是**每次 SelectTab 重建的显示投影**，持久数据是 `_catalog.Current`。所以往投影末尾追加一个特殊 `ItemViewModel` 是正当的：它是列表视图的固定最后一格，不是条目。

- `ItemViewModel` 加 `IsAddTile` 标志 + 静态工厂 `AddTile(iconEdge)`（`Item = null!`，`Name = "添加"`）
- `SelectTab` 在数据条目之后**无条件**追加哨兵（空分类时 Items 只有它）
- `LaunchTile` 首行拒绝 `IsAddTile`（防御性，即使 UI 走不到）
- View 层 `Tile_PointerPressed` 对 `IsAddTile` 直接返回，不进入拖拽
- 点击弹菜单用 `Button.Flyout` + `MenuFlyout`（与 ☰ 同一模式，零代码）
- 模板分叉：ItemTemplate 内两个 StackPanel，用 `IsVisible="{Binding IsAddTile}"` / `{Binding !IsAddTile}` 切换，不引入 DataTemplateSelector
- 视觉：虚线边框（用户确认"按你的建议"= 虚线，低调、一看就是占位），尺寸跟随 `TileWidth`/`IconEdge`

### 已知契约变化（必须如实交代）

`vm.Items.Count` 从此包含末尾哨兵，不再等于条目数。探针里把 Items.Count 当条目数断言的地方改为按 `!IsAddTile` 过滤——这是契约变更的跟进，不是迁就实现：持久数据侧的断言（`store.Current…Items.Count`）本来就是权威口径，保持不变。

### 风险

- 右键双弹（磁贴 vs 空白）：Avalonia 的 ContextMenu 优先级由命中测试决定，理论安全，但只有真实鼠标能验证 → 交付时列为手工回归项
- `Item = null!`：任何未来消费 `tile.Item` 的新代码都可能 NPE。用 `IsAddTile` 显式挡住现有 2 个消费点（`Launch`、`ShowEditItem`），并在属性注释里写明

## 3. 计划

| # | 任务 | 验证 |
|---|---|---|
| 1 | `ItemViewModel.IsAddTile` + 工厂 + `LaunchTile` 拒绝 | 编译 |
| 2 | `SelectTab` 追加哨兵（含空分类） | 探针 |
| 3 | XAML：ScrollViewer.ContextMenu ＋ 哨兵模板 ＋ Button.Flyout | 编译 + 探针 |
| 4 | View 层拖拽拒绝哨兵 | 代码审查 |
| 5 | PanelProbe 契约跟进 + 新增哨兵/菜单断言 | 全 PASS |
| 6 | 全套探针回归、AOT 重建、清 pdb、PortableCheck/UiAcceptance | 全 PASS |
| 7 | 用户手工回归（重点：右键双弹、＋ 磁贴换行排布） | 用户确认 |

单人开发，无独立 review。
