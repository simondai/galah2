# 分类圆片右键菜单（2026-11-24）

## 1. 需求

用户要求：分类名药丸（chip）右键菜单，两项：
- 重命名分类
- 删除分类（**分类下必须没有图标才允许删除**）

## 2. 方案

- 菜单：ContextMenu（不是 MenuFlyout——flyout 项不在逻辑树，$parent 命令路径解析不到，
  MenuItem 会因 BindingError 置灰，见 2026-11-23 灰菜单修复的结论）。
- **第一版翻车复盘（用户报"菜单全灰"）**：XAML 里给 ContextMenu 绑
  `DataContext="{Binding $parent[Button]...}"` 失败——$parent 路径跨不过 popup 边界，
  DataContext 解析不到 → 命令绑定 BindingError → 整菜单置灰。与磁贴菜单注释里
  "the exact symptom reported twice" 同一症状。
- **定稿（照磁贴菜单已验证模式）**：ContextMenu 不写任何 $parent 绑定；
  `Opening` 处理器（ChipMenu_Opening）做两件事：
  1. `SetChipMenuTarget(PlacementTarget.DataContext)`——把被右键的圆片记到 VM
     （命令改无参，从目标执行，不再用 CommandParameter 跨边界传参）；
  2. `menu.DataContext = PanelViewModel`——注入后行命令走裸绑定
     `{Binding RenameChipCommand}` / `{Binding DeleteChipCommand}`。
  `x:DataType="vm:PanelViewModel"` 写在 ContextMenu 上是承重件：缺了它编译绑定会按
  模板数据类型 TabViewModel 解析而构建失败。
- 删除双保险：菜单行 `IsEnabled` 绑 `ChipMenuTargetIsDeletable`（Opening 时按目标的
  HasNoEntries 刷新）；命令层 `DeleteTabIfEmpty` 再查 catalog，>0 拒绝并 toast。
- 重命名：复用 `RenameTab`（prompt 预填现名）。
- 删除双保险：
  1. UI 层：菜单行 `IsEnabled` 绑 `TabViewModel.HasNoEntries`（有条目即置灰）；
  2. 命令层：`DeleteTabIfEmpty` 查 catalog 条目数，>0 时 toast「分类「X」下还有 N 个条目，
     不能删除」并拒绝——即便菜单行被启用或命令被程序化调用，规则依然成立。
- `TabViewModel` 新增 `HasNoEntries`（Refresh 投影时由 `tab.Items.Count == 0` 填充）。
- 横/竖两个布局的圆片模板都挂同一份菜单。
- 既有 ☰ 菜单的「删除当前分类」（带"及其全部条目"确认）不变——那是显式级联删除的入口。

## 3. 验证

- PanelProbe 断言：HasNoEntries 投影正确；目标记录（空=可删/非空=不可删）；圆片菜单
  重命名的是圆片自己的分类；空分类可删；非空分类拒绝（toast 文案含条目数）；窗口级
  每个圆片都带 2 行菜单；**灰菜单契约**——模拟 Opening（反射调 ChipMenu_Opening，
  手动补 PlacementTarget，因直接调用跳过了 ContextMenuService 的赋值）后
  menu.DataContext 必须是 PanelViewModel、目标必须按条目数记录可删性。
  菜单行的真实可点性仍留手工验收（进程内 popup 命令解析不可断言，既有结论）。
- 探针状态纪律：新块只用一次性空分类做删改试验，保持探针假设的 `[默认, 测试分类]`
  布局——prompt 块用绝对索引 1 选 tab，动布局会把后续块全带偏（本次实测踩中）。
- 待手工验收：圆片右键两行的真实可点性（进程内 popup 命令解析不可断言，既有结论）。
