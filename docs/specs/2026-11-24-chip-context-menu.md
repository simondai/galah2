# 分类圆片右键菜单（2026-11-24）

## 1. 需求

用户要求：分类名药丸（chip）右键菜单，两项：
- 重命名分类
- 删除分类（**分类下必须没有图标才允许删除**）

## 2. 方案

- 菜单：ContextMenu（不是 MenuFlyout——flyout 项不在逻辑树，$parent 命令路径解析不到，
  MenuItem 会因 BindingError 置灰，见 2026-11-23 灰菜单修复的结论）。
  DataContext 显式绑 `$parent[Button].((vm:PanelViewModel)DataContext)`，
  行参数 CommandParameter 绑圆片的 TabViewModel——作用在被右键的圆片，不是当前选中分类。
- 重命名：复用 `RenameTab`（prompt 预填现名）。
- 删除双保险：
  1. UI 层：菜单行 `IsEnabled` 绑 `TabViewModel.HasNoEntries`（有条目即置灰）；
  2. 命令层：`DeleteTabIfEmpty` 查 catalog 条目数，>0 时 toast「分类「X」下还有 N 个条目，
     不能删除」并拒绝——即便菜单行被启用或命令被程序化调用，规则依然成立。
- `TabViewModel` 新增 `HasNoEntries`（Refresh 投影时由 `tab.Items.Count == 0` 填充）。
- 横/竖两个布局的圆片模板都挂同一份菜单。
- 既有 ☰ 菜单的「删除当前分类」（带"及其全部条目"确认）不变——那是显式级联删除的入口。

## 3. 验证

- PanelProbe 新增 5 条断言：HasNoEntries 投影正确；圆片菜单重命名的是圆片自己的分类；
  空分类可删；非空分类拒绝（toast 文案含条目数）；窗口级每个圆片都带 2 行菜单
  （结构锁定，菜单命令解析按既有结论留手工验收）。
- 探针状态纪律：新块只用一次性空分类做删改试验，保持探针假设的 `[默认, 测试分类]`
  布局——prompt 块用绝对索引 1 选 tab，动布局会把后续块全带偏（本次实测踩中）。
- 待手工验收：圆片右键两行的真实可点性（进程内 popup 命令解析不可断言，既有结论）。
