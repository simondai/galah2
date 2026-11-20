# 手工测试第三轮：六个问题

## 1. 需求
| # | 现象 | 根因（已确认） | 验收标准 |
|---|---|---|---|
| 1 | exe 无图标 | csproj 未设 ApplicationIcon | 发布的 exe 提取得到图标（实测） |
| 2 | 材质切换无效果 | 半透明填充 #F2=95%（1.x 是 #E8=91%）+ Mica 在本环境渲染近不透明 | 两档填充 alpha 分别 FF/E8（=1.x），切换肉眼可见 |
| 3 | 热键松开即缩回 | 未设 MOD_NOREPEAT，按键重复连发 Toggle | 注册 flags 含 0x4000；按住不再连发 |
| 4 | 无法拉伸 | SystemDecorations=None 无系统边缘，需自实现 | 8 方向边缘 6px 热区可拉伸，Min 420x300 |
| 5 | 跟随鼠标意义 | 功能正常（Rolan 式热键唤出贴光标） | 无需改码，README 说明 |
| 6 | 面板可移出屏幕 | 拖动/显示无钳制 | 拖动/缩放后窗口恒在工作区内；GetAsyncKeyState 兜底防拖粘 |

## 2. 方案
- 1：复制 1.x 的 app.ico 进 2.0，csproj 加 `<ApplicationIcon>`；发布后用 ExtractAssociatedIcon 实测验证 AOT 保留该资源（若被剥掉则改走 win32res）。
- 2：Tokens.axaml 两处 #F2→#E8；ApplyMaterial 简化为固定 Transparent（四角圆角仍保住），半透明观感完全对齐 1.x。MaterialProbe 断言更新为 E8/FF。
- 3：HotkeyService.Register 里 OR 0x4000（MOD_NOREPEAT，Win7+）。
- 4：PanelWindow 隧道式 PointerPressed/Moved/Released：窗口边缘 6px 内进入缩放模式，标记 e.Handled 压掉瓦片点击与标题栏拖动；用 GetCursorPos 绝对坐标 + 原始 bounds 计算新几何，钳制 MinWidth/MinHeight。
- 6：拖动/缩放的每步位移后把 Position 钳进包含窗口中心的工作区；拖动循环里 GetAsyncKeyState(VK_LBUTTON) 为弹起则立即结束拖动（防 Avalonia 按压态失真导致"窗口粘鼠标"）。

## 3. 计划
2/3/6 先行（小改动）→ 1（需发布验证）→ 4（最大块）→ MaterialProbe/GeometryProbe 更新 → 全探针 → 提交推送 → CI → 重建产物。

## 4. 实施结果（2026-11-20 收尾）
- #1 图标：csproj ApplicationIcon + app.ico（16~256 五尺寸）。发布后 PE 资源表实测 RT_ICON=5，资源管理器有图标。此前"RT_ICON=0"测的是旧产物（当时 csproj 尚未设置），"AOT 剥图标"结论作废。
- #2 材质：填充 #E8232327/#E8F5F5F7（=1.x），ApplyMaterial 固定 Transparent、不再请求 Mica（本机合成器报 Mica 实渲染近不透明，是"两档无区别"的另一半根因）。MaterialProbe 4/4：FF/E8、两档均 Transparent。
- #3 热键：两个叠加根因。① MOD_NOREPEAT 缺失（按住连发 Toggle）；② Register fire-and-forget 返回假失败——诊断实测：同线程注册 OK、跨线程排队 1408。修为同步等待窗口线程执行。SettingsProbe 实测 alt+space 注册 OK。
- #4 拉伸：8 方向 6DIP 热区 + GetCursorPos 绝对坐标 + 钳制 MinWidth/MinHeight/工作区。Avalonia 11 无对角光标，四角共用 SizeAll。GeometryProbe 7 条热区断言全过。注意：本机 ref assembly 只导出 public 成员，EdgesFor 需 public（internal 不进 ref，探针引用会 CS0117）。
- #5 跟随鼠标：功能正常（热键唤出贴光标，Rolan 式交互），README 补充说明。
- #6 出屏：两处根因。① 拖动无钳制 + Avalonia 按压态失真"粘鼠标"→ 每步 ClampToScreens + GetAsyncKeyState(VK_LBUTTON) 兜底；② PlacePanel 用 DIP 宽高比物理像素工作区，DPI≠100% 时钳制算错 → 乘 RenderScaling 修正。
- 验收：九工具全绿（ComProbe 11 / IconProbe 12 / SettingsProbe 20 / PanelProbe 52 / DialogProbe 8 / GeometryProbe 14 / MaterialProbe 4 / UiAcceptance 11 / PortableCheck 9）。
- 流程说明：步骤 1-3 由并行会话先写、本会话按其规格收尾并修正两处根因（图标、热键 1408）；步骤 6 无独立 reviewer，由两个会话互为对照但非正式 review。
