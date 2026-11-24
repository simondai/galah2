# 分类圆片右键菜单（2026-11-24）

## 1. 需求

用户要求：分类名药丸（chip）右键菜单，两项：
- 重命名分类
- 删除分类（**分类下必须没有图标才允许删除**）

## 2. 方案

- 菜单：ContextMenu（不是 MenuFlyout——flyout 项不在逻辑树）。
- **两次翻车复盘（两次用户报"菜单全灰"）**：
  1. 第一版：XAML 里给 ContextMenu 绑 `DataContext="{Binding $parent[Button]...}"`。
     $parent 路径跨不过 popup 边界 → DataContext 解析不到 → 命令绑定 BindingError → 整菜单置灰。
  2. 第二版：`Opening` 处理器注入 DataContext（XAML 接线 `Opening="ChipMenu_Opening"`）。
     **Avalonia 的 ContextMenu 上，XAML 事件接线根本不生效**——Opening 处理器从未被调用，
     DataContext 从未被换，菜单照旧全灰。而磁贴菜单（一直用的 `$parent[ItemsControl]` 绑定）
     其实从诞生起就是灰的，只是从来没被真机验收过（探针测不了 popup 内命令解析）。
  探针盲区同步暴露：之前探针"真实打开菜单"是**反射手动调 handler 再 Open()**，
  绕过了事件接线，所以全绿。补了"纯 Open() 不手动调"的接线测试后，JIT 下立即复现。
- **定稿（唯一模式：行只绑宿主控件自己继承的 DataContext）**——与真机验证可用的
  空白处菜单同构，无任何 popup 跨界路径、无任何事件接线：
  - 圆片菜单：行绑 `TabViewModel.RenameCommand` / `DeleteCommand`
    （Refresh 时按 id 闭包接线，Refresh 中途重投影不会让旧菜单行作用到错误分类）；
    删除行 `IsEnabled` 绑 `HasNoEntries`。
  - 磁贴菜单：行绑 `ItemViewModel.OpenCommand/EditCommand/MoveLeftCommand/
    MoveRightCommand/DeleteCommand`（SelectTab 建磁贴时接线）。
  - 哨兵（＋磁贴）菜单：行绑哨兵自己的 `AddPickerCommand/AddFileCommand/AddFolderCommand/
    AddUrlCommand`（即面板同名的四个命令，作用当前分类）。左键打开不再需要注入 DataContext。
  - VM 删除：RenameChipCommand/DeleteChipCommand/SetChipMenuTarget/ChipMenuTargetIsDeletable
    及 code-behind 的 ChipMenu_Opening/AddTileMenu_Opening/GiveMenuTheViewModel 全部移除。
- 删除双保险：菜单行 `IsEnabled` 绑 `HasNoEntries`（继承 DataContext，直接绑定）；
  命令层 `DeleteTabIfEmpty` 再查 catalog，>0 拒绝并 toast。
- 重命名：复用 `RenameTab`（prompt 预填现名）。
- 删除双保险：
  1. UI 层：菜单行 `IsEnabled` 绑 `TabViewModel.HasNoEntries`（有条目即置灰）；
  2. 命令层：`DeleteTabIfEmpty` 查 catalog 条目数，>0 时 toast「分类「X」下还有 N 个条目，
     不能删除」并拒绝——即便菜单行被启用或命令被程序化调用，规则依然成立。
- `TabViewModel` 新增 `HasNoEntries`（Refresh 投影时由 `tab.Items.Count == 0` 填充）。
- 横/竖两个布局的圆片模板都挂同一份菜单。
- 既有 ☰ 菜单的「删除当前分类」（带"及其全部条目"确认）不变——那是显式级联删除的入口。

## 3. 验证

- PanelProbe VM 级：每个圆片自带 rename/delete 命令；重命名作用在圆片自己的分类；
  空分类可删；非空分类拒绝（toast 文案含条目数）。
- PanelProbe 窗口级——**真实打开**（探针跑在真实桌面，窗口真显示；这是结构断言看不见的
  一层，两次灰菜单报告都死在这里）：
  - 圆片菜单：纯 Open()（不手动调任何 handler）后菜单打开、DataContext 是圆片自己的
    TabViewModel、重命名行命令活着且可点、删除行 enabled 状态与该分类条目数一致。
  - 磁贴菜单：打开后「打开」「删除」行命令活着且可点，删除行就是该磁贴自己的 DeleteCommand。
  - 哨兵菜单：打开后 4 行全部活着且可点，且绑的是哨兵自己的 AddPickerCommand。
- AOT 探针：同一套断言的 AOT 编译版本（`dotnet publish -p:PublishAot=true` 发布探针后
  真机运行），确认编译 XAML 绑定与 JIT 行为一致。
- 回归：其余 9 套探针全绿；产品 AOT 重建 + PortableCheck + UiAcceptance 全过。
- 探针状态纪律：新块只用一次性空分类做删改试验，保持探针假设的 `[默认, 测试分类]`
  布局——prompt 块用绝对索引 1 选 tab，动布局会把后续块全带偏（本次实测踩中）。
- 待手工验收：圆片右键两行的真实可点性（进程内 popup 命令解析不可断言，既有结论）。
