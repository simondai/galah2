# 拖拽排序失效根因：Button 吞掉按下事件（2026-11-23 复查）

## 1. 需求

用户手工测试（14:07 新产物）反馈：分类拖动、磁贴拖动**全部无效果**。
添加类功能正常。要求复查代码。

## 2. 根因（Avalonia 源码级，非推测）

三处源码连成一条链：

1. `Avalonia.Base/Interactivity/EventRoute.cs` 145-160：路由到每个元素时，
   **先跑类处理器**（`InvokeRaised`），再跑实例处理器，且实例处理器的调用条件是
   `!e.Handled || handledEventsToo`。
2. `Avalonia.Base/Input/InputElement.cs` 222：`PointerPressedEvent.AddClassHandler<InputElement>(x => x.OnPointerPressed(e))`
   —— 每个元素的 `OnPointerPressed` 虚方法就是在类处理器阶段被调用的。
3. `Avalonia.Controls/Button.cs` 402-418：`Button.OnPointerPressed` 在左键按下时
   **`e.Handled = true`**。

结论：XAML 里 `PointerPressed="Tile_PointerPressed"` / `PointerPressed="TabChip_PointerPressed"`
是**实例处理器、默认 handledEventsToo=false**，在 Button 类处理器置 Handled 之后被路由跳过——
**永远不执行**。`_dragTile/_dragTab` 恒为 null，`PointerMoved` 首行即退出，拖拽发不起来。

旁证（与用户观察完全一致）：
- 点击/启动/选中正常 —— Click 是 Button 自己 raised 的新事件，不受影响
- 分类切换正常 —— Command 走 Button 内部路径
- 窗口 chrome 拖拽、边缘缩放正常 —— 它们注册在 **Tunnel** 阶段
  （`Window_PointerPressedTunnel`，早于类处理器），项目里一直是对的
- 类处理器默认路由阶段 = Direct|Bubble，不含 Tunnel，所以 tunnel 注册天然免疫

同类隐患：`Chrome_PointerPressed`（Border 不吞事件）安全；但任何"给 Button 挂实例
PointerPressed 想做手势"的代码都是死代码。本轮把两个磁贴/圆片的按下捕获统一挪进窗口
tunnel 处理器，机制上收口。

## 3. 修复设计

- 删除 XAML 上两处 `PointerPressed=` 接线与两个失效方法
- `Window_PointerPressedTunnel`（已存在、已在正确阶段）在 resize 分支之后接管按下捕获：
  从 `e.Source` 沿视觉树向上找最近的 Button，按 Tag 类型分派：
  `ItemViewModel(非哨兵)` → 磁贴拖拽起点；`TabViewModel` → 圆片拖拽起点；其余清空两者
- 新增纯函数 `DragButtonFrom(object? source)`（internal static）：向上找 Tag 为
  ItemViewModel/TabViewModel 的 Button。探针直接断言它——因为事件源往往是磁贴内部的
  TextBlock/Border，不是 Button 本身，这个向上查找是修复的另一半
- `PointerMoved` 保留在按钮上：Button 不吞 moved（未覆写 OnPointerMoved），冒泡可达
- ＋ 磁贴：按下时 `IsAddTile` 排除 → 不可拖；哨兵 id 在 store 层也拒绝（已有双保险）

## 4. 计划

| # | 任务 | 验证 |
|---|---|---|
| 1 | `DragButtonFrom` + tunnel 捕获 + 删死代码 | 编译 |
| 2 | 探针：DragButtonFrom 从内层 TextBlock 回溯到 Button（磁贴/圆片/哨兵/＋圆片四类） | 全 PASS |
| 3 | 探针：＋ 磁贴 ContextMenu 可 Open（左键路径不抛异常） | PASS |
| 4 | 全套回归 + AOT 重建 | 全 PASS |
| 5 | 用户手工回归（分类拖动/磁贴拖动/拖到＋） | 用户确认 |

单人开发，无独立 review。拖拽手势本身仍需真实鼠标验收（探针锁的是机制与查找逻辑）。
