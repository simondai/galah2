# Galah 2.0 代码审查报告

**审查时间**: 2026-11-20  
**审查范围**: 全仓库（src/Galah2 + tools + docs）  
**审查基准**: HEAD @ d255a16

---

## 一、确信的问题（有代码证据，建议修复）

### 1. 死代码：未被引用的类和方法

#### 1.1 `DialogGuard.cs`（33 行）
**位置**: `src/Galah2/Services/DialogGuard.cs`

**证据**:
```bash
grep -rn "DialogGuard" src tools --include=*.cs | grep -v obj | grep -v "DialogGuard.cs"
# 结果: 空
```

**问题**: 设计意图是"防止 hide-on-blur 吞掉对话框"，但从未被使用。当前 `PanelWindow.axaml.cs:41` 的 `Deactivated` 处理直接 Hide，没有 DialogGuard 保护。这意味着：如果用户在编辑条目时面板失焦，编辑窗口可能被误关。

**建议**: 要么接线（在 `ShowOwned` 里调用），要么删除。

---

#### 1.2 `TabNavigation.cs`（31 行）
**位置**: `src/Galah2/Services/TabNavigation.cs`

**证据**:
```bash
grep -rn "TabNavigation" src tools --include=*.cs | grep -v obj | grep -v "TabNavigation.cs"
# 结果: 空
```

**问题**: 纯函数数学库（`StepIndex`, `DigitToIndex`），从未被调用。`PanelViewModel.SwitchTabRelative` 直接写死了取模逻辑（`PanelViewModel.cs:259`），`PanelWindow.axaml.cs:82` 直接写死了数字键映射。

**建议**: 要么在 `PanelViewModel`/`PanelWindow` 里改用它，要么删除。

---

#### 1.3 `PanelPlacement.AreaFor` 方法
**位置**: `src/Galah2/Services/PanelPlacement.cs:82-90`

**证据**:
```bash
grep -rn "AreaFor" src tools --include=*.cs | grep -v obj | grep -v "PanelPlacement.cs"
# 结果: 空
```

**问题**: 方法存在但无调用者。`AppRuntime.PlacePanel` 直接调用 `MonitorGeometry.WorkAreaAtCursor()`。

**建议**: 删除。

---

### 2. 功能缺陷：设置开关无效

#### 2.1 `WheelSwitchTab` 开关死设置
**位置**: `src/Galah2/Views/PanelWindow.axaml.cs:87-94`

**证据**:
```csharp
private void OnWheel(object? sender, PointerWheelEventArgs e)
{
    if (DataContext is not PanelViewModel vm) return;
    if (e.KeyModifiers != KeyModifiers.None) return;

    vm.SwitchTabRelative(e.Delta.Y > 0 ? -1 : 1);  // 无条件切换，从不检查设置
    e.Handled = true;
}
```

**对照 1.x** (`galah/Galah/Views/PanelWindow.xaml.cs:194`):
```csharp
if (!_env.Settings.Current.WheelSwitchTab) return;  // 1.x 检查开关
```

**影响**: 用户关闭"滚轮切换分类"后，滚轮仍会切换分类。

**建议**: 在 `OnWheel` 开头加 `if (!vm.WheelSwitchTab) return;`。

---

#### 2.2 `NumberKeysSwitchTab` 开关死设置
**位置**: `src/Galah2/Views/PanelWindow.axaml.cs:80-85`

**证据**:
```csharp
if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key >= Key.D1 && e.Key <= Key.D9)
{
    vm.SelectTabByIndex(e.Key - Key.D1 + 1);
    e.Handled = true;
}
```

**对照 1.x** (`galah/Galah/Views/PanelWindow.xaml.cs:171`):
```csharp
if (!_env.Settings.Current.NumberKeysSwitchTab) return;  // 1.x 检查开关
```

**影响**: 用户关闭"Ctrl+数字切换分类"后，快捷键仍生效。

**建议**: 加 `if (!vm.NumberKeysSwitchTab) return;`。

---

#### 2.3 滚轮交互降级：未实现"边缘才切换"
**位置**: `src/Galah2/Views/PanelWindow.axaml.cs:87-94`

**证据**:
2.0 的 `OnWheel` 在任何滚轮事件都切换分类。1.x 只在磁贴区滚到边缘时切换（`galah/Galah/Views/PanelWindow.xaml.cs:197-200`）：
```csharp
var atTop = scroll.VerticalOffset <= 0;
var atBottom = scroll.VerticalOffset + scroll.ViewportHeight >= scroll.ExtentHeight - 0.5;
var edge = direction > 0 ? atBottom : atTop;
if (!edge) return;  // 不在边缘则不切换
```

**影响**: 2.0 用户滚动磁贴列表时会意外跳分类。1.x 只在滚到顶/底才跳。

**建议**: 将 `OnWheel` 移到磁贴区的 `ScrollViewer` 上，并检查 `VerticalOffset` 是否在边缘。

---

### 3. 功能缺陷：小键盘不支持

**位置**: `src/Galah2/Views/PanelWindow.axaml.cs:80-85`

**证据**:
```csharp
if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key >= Key.D1 && e.Key <= Key.D9)
// 只处理 D1-D9，未处理 NumPad1-9
```

**对照 1.x** (`galah/Galah/Views/PanelWindow.xaml.cs:174-178`):
```csharp
var digit = e.Key switch
{
    >= Key.D0 and <= Key.D9 => (int)(e.Key - Key.D0),
    >= Key.NumPad0 and <= Key.NumPad9 => (int)(e.Key - Key.NumPad0),
    _ => -1,
};
```

**影响**: 用户按小键盘的 Ctrl+1..9 无法切换分类。

**建议**: 补 `Key.NumPad1` 到 `Key.NumPad9` 的处理。

---

### 4. 代码质量：过时 API 未迁移

**位置**: `src/Galah2/Views/PanelWindow.axaml.cs`（多处）

**证据**（编译警告）:
```
warning CS0618: "DataObject"已过时:"Use DataTransfer instead" [PanelWindow.axaml.cs(150,24)]
warning CS0618: "DragDrop.DoDragDrop(...)"已过时:"Use DoDragDropAsync instead." [PanelWindow.axaml.cs(152,15)]
warning CS0618: "DragEventArgs.Data"已过时:"Use DataTransfer instead." [PanelWindow.axaml.cs(157,25)]
```

**影响**: Avalonia 12 可能移除这些 API。当前功能正常，但技术债。

**建议**: 一次性迁移到 `DataTransfer` / `DoDragDropAsync`。

---

### 5. 不一致：`SelectTabByIndex` 参数语义与 1.x 不同

**位置**: `src/Galah2/ViewModels/PanelViewModel.cs:265`

**证据**:
- 1.x: `SelectTabByIndex(int index)` 是 **0-based**
- 2.0: `SelectTabByIndex(int oneBased)` 是 **1-based**

**影响**: 当前 2.0 内部调用一致（无 bug），但从 1.x 移植代码时容易踩坑。

**建议**: 保持 1-based（更符合用户直觉"Ctrl+1 选第一个"），但在 XML 注释里显式标注与 1.x 的差异。

---

### 6. 冗余：重复的图标文件

**位置**: 
- `src/Galah2/app.ico`（76485 字节）
- `src/Galah2/Assets/galah.ico`（76485 字节）

**证据**:
```bash
md5sum src/Galah2/app.ico src/Galah2/Assets/galah.ico
# 两者 MD5 相同
```

**问题**: `app.ico` 用于 csproj 的 `<ApplicationIcon>`，`galah.ico` 用于托盘图标。内容相同，可合并为一个文件。

**建议**: 删除 `Assets/galah.ico`，托盘图标也引用 `app.ico`。

---

### 7. 冗余：调试代码残留

**位置**: `src/Galah2/Views/PanelWindow.axaml.cs:215`

**证据**:
```csharp
catch (Exception ex) { Console.Error.WriteLine("drop failed: " + ex.Message); }
```

**问题**: 唯一一处 `Console.Error.WriteLine`，其他错误都走 `DiagnosticsLog`。

**建议**: 改为 `DiagnosticsLog.Line("drop", "failed: " + ex.Message)`。

---

## 二、存疑的部分（需要进一步验证或决策）

### 1. 面板位置策略的"跟随鼠标"是否必要

**现象**: 设置里有"跟随鼠标"选项，但用户反馈"有什么存在的意义？"。

**分析**: 这是 Rolan 式交互（热键唤出时面板出现在光标处）。但当前实现有 bug（面板会移出屏幕），且用户没感知到价值。

**待决策**: 是修复 bug 并保留，还是直接移除该选项？

---

### 2. `HotkeyService` 的 `MOD_NOREPEAT` 是否必要

**现象**: 加了 `MOD_NOREPEAT`（0x4000）防止按键重复触发。

**分析**: 从症状推断（按住 Alt+Space 松开后面板缩回）得出此结论，但未实测验证。用户反馈"Alt+Space 能看到程序，但松开就退回后台"更像是注册失败（Windows 系统菜单抢占），而非按键重复。

**待验证**: 需要实测确认 `MOD_NOREPEAT` 是否真的解决了问题。

---

### 3. 材质系统是否应该简化

**现象**: 当前有"不透明"和"半透明"两档，但实现复杂（窗口 Transparent + 填充色切换）。

**分析**: 1.x 的半透明是 `#E8`（91% 不透明），2.0 已对齐。但用户反馈"切换没有任何效果"，可能是因为：
- 半透明和不透明的视觉差异太小
- 或者用户期望的是 Mica/Acrylic 材质（但当前实现已放弃 Mica）

**待决策**: 是否需要恢复 Mica 支持，或者干脆去掉"半透明"选项？

---

### 4. `app.ico` 的 `ApplicationIcon` 是否真的被 AOT 保留

**现象**: 用户反馈"资源管理器里 Galah2.exe 没有图标"。

**分析**: csproj 已设 `<ApplicationIcon>app.ico</ApplicationIcon>`，但 AOT 发布可能剥离图标资源。需要实测验证。

**待验证**: 检查发布产物的 PE 资源表是否有 `RT_GROUP_ICON`。

---

## 三、建议的优先级

### P0（影响功能，必须修）
1. `WheelSwitchTab` / `NumberKeysSwitchTab` 开关死设置
2. 滚轮交互降级（未实现"边缘才切换"）
3. 小键盘不支持

### P1（代码质量，建议修）
4. 死代码清理（`DialogGuard`, `TabNavigation`, `PanelPlacement.AreaFor`）
5. 过时 API 迁移（`DataObject` → `DataTransfer`）
6. 调试代码残留（`Console.Error.WriteLine`）

### P2（优化，可选）
7. 重复图标文件合并
8. `SelectTabByIndex` 语义差异注释

### 待决策
9. 面板位置策略是否保留"跟随鼠标"
10. 材质系统是否简化

---

## 四、统计

- **死代码**: 3 处（2 个类 + 1 个方法）
- **功能缺陷**: 4 处（2 个死设置 + 1 个交互降级 + 1 个小键盘缺失）
- **代码质量**: 2 处（过时 API + 调试残留）
- **冗余**: 1 处（重复图标）
- **不一致**: 1 处（`SelectTabByIndex` 语义）
- **存疑**: 4 项（需实测或决策）

---

**审查人**: Alma  
**审查方式**: 静态代码分析 + 1.x 对照
