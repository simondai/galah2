# 对话框表面与主面板对齐（轻量方案）

日期：2026-11-22
状态：已实现，探针全绿

## 1. 需求分析

- **问题**：设置/添加/编辑/确认/输入/维护六个对话框用 `WindowBrush` 素窗底 + 系统标题栏，
  与主面板（圆角 RootBorder + 面板表面 + 发丝线）质感断层。
- **使用者**：所有用户；对话框是每次添加/编辑/确认都会经过的界面。
- **方案取舍（owner 选了轻量档）**：
  - 保留系统标题栏与 Win11 系统 8px 圆角（不画自绘标题栏，不自己管拖动/阴影边界）
  - 窗口底色换成面板同款**不透明**表面（`PalettePanelFillOpaque`，复用现成 token，零新色）
  - 不加内嵌卡片：小确认框加卡片会挤，且大对话框的素表面已经和面板同语言
  - 不做全无边框档（方案 1）留待 owner 体验后决定
- **边界**：accent 按钮上的 `Foreground="{DynamicResource WindowBrush}"` 不动——那是
  "按钮上的深色字"语义，与窗底无关；`WindowBrush` token 本身保留（仍是调色板最亮档）。

## 2. 方案设计

- `Styles/Tokens.axaml`：新增 `DialogSurfaceBrush`（= `PalettePanelFillOpaque` 的 SolidColorBrush）。
- 六个对话框 XAML：`Background="{DynamicResource WindowBrush}"` → `DialogSurfaceBrush`。
- `DialogProbe`：
  - 宿主从最小 ProbeApp 换成**产品 App**（`Galah2.App` + `SetupWithLifetime`，
    与 MaterialProbe 同模式）——最小 App 没有 token 词典，表面断言无从谈起
    （旧行为：窗口底是默认白，资源全 MISS）
  - 窗口查找从 `IClassicDesktopStyleApplicationLifetime.Windows` 改为注册的
    desktop lifetime 实例（`Application.Current.Windows` 在 11.3 不存在）
  - 新断言：prompt 窗口的 `Background` 与窗口资源链查到的 `DialogSurfaceBrush` **引用相同**
    （DynamicResource 交出词典原实例；引用不等 = token 接线错了，色值巧合相同也拦得住）

## 3. 计划

单批提交：Tokens + 6 XAML + DialogProbe 改造与新断言 + CHANGELOG + 本文档。
回滚：`git revert` 该提交。

## 4. 验收（实测）

- DialogProbe 全过，含新断言：`bg=#fff7f7f8 token=#fff7f7f8`（引用相等，light 主题）
- 全解构建 0 错误
- 遗留观察：11.3 里 `Application.Current.Resources[key]` 对合并词典的 token 全部 MISS，
  必须走 `IResourceHost.TryFindResource`（窗口侧）——已写进断言注释，防后人踩坑
