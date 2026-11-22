# 变更记录

格式：每次交付一节，写清楚**用户能感知的变化**、**怎么验证**、**怎么回滚**。
内部重构不列，除非它改变了行为。

## 未发布 — 2.0（v2 分支）

### 新增
- Avalonia 11 + NativeAOT 重写，产物 43.2 MB（4 文件绿色版），1.x 是 68.6 MB 单文件
- 托盘图标、右键原生菜单、全局热键、单实例转发
- 面板自定义标题栏：显示产品名、☰ 菜单、关闭按钮、可拖动
- 分类条支持顶部/左侧两种位置，图标支持小/中/大三档
- 分类条上的「＋」新建分类圆片（1.x 对齐）：横向布局在行尾、竖向布局在左栏底部，
  点击直接弹名称输入框建类并选中，不再只能从右键菜单进
- 条目右键菜单：打开、编辑、左右移、删除
- 添加选择器：系统工具、设置项、标准文件夹、已安装应用四个来源，支持搜索与多选
- 编辑对话框：名称、目标、参数、工作目录、图标、管理员运行、类型建议、提交校验门
- 库维护：扫描失效项、去重、按名称排序、重建图标缓存、整库导出与导入
- 设置：主题（跟随系统/浅色/深色）、图标尺寸、分类位置、面板材质、点击方式、
  面板位置、热键、开机自启、桌面快捷方式
- 对话框表面与主面板对齐（设置/添加/编辑/确认/输入/维护六个窗口）：
  窗口底色换成面板同款不透明表面，保留系统标题栏与 Win11 系统圆角，
  不再是素白/素黑断层窗
- 拖放文件、粘贴 URL
- 启动失败的 toast 提示
- URL 条目抓取站点图标（仅在点击按钮时联网）
- 深色/浅色两套主题，跟随系统时实时响应变化

### 修复
- 磁贴列表超过一屏后滚轮无法滚动（面板级滚轮拦截吞掉了滚动）；
  现在列表能正常滚，滚到底/顶边缘才切换分类（与 1.x 一致）
- 设置里的「滚轮切换分类」「数字键切换分类」两个开关原本完全无效，现在生效
- 方向键切换分类、Ctrl+Tab / Ctrl+Shift+Tab 回绕原本不响应（被焦点导航先截走），现在生效
- Ctrl+小键盘数字 0-9 与主键盘数字行为一致（1.x 对齐）
- AOT 下 `JsonSerializer` 反射序列化启动即崩（改用源生成器）
- AOT 下内置 COM 不可用（快捷方式与 .msc 图标改为手写 vtable）
- 第二实例转发后抛「Dispatcher shut down」
- 关闭面板后再也打不开（`Closing` 拦截改隐藏）
- 设置界面下拉框撑到滚动条上
- 设置里四个「存了但不生效」的项：自启、点击方式、材质、失焦隐藏
- 编辑窗口三个浏览按钮与「抓取图标」按钮点了没反应
- CI 只上传 exe、漏掉三个原生 dll，导致下载的包打不开

### 已知未完成
- 独立 Code Review（单人开发，见 `docs/PROCESS-AUDIT.md`）
- 托盘右键菜单与真实鼠标退出链的自动化验收（当前 RDP 会话输入退化）

### 验证方法
```bash
dotnet build Galah2.sln -c Release
dotnet run --project tools/ComProbe      -c Release
dotnet run --project tools/IconProbe     -c Release
dotnet run --project tools/SettingsProbe -c Release
dotnet run --project tools/PanelProbe    -c Release
dotnet run --project tools/UiAcceptance  -c Release -- src/Galah2/bin/Release/net8.0/Galah2.exe
dotnet run --project tools/PortableCheck -c Release -- artifact
```

### 回滚
2.0 出问题时：
1. **退回 1.x**：`main` 分支的 `Galah.exe` 可直接使用，数据格式完全兼容
   （2.0 读写的 `GalahData/` 就是 1.x 的格式）。
2. **退回上一版 2.0**：保留上一版的整个 `artifact/` 目录，整目录替换即可。
   注意不能只换 exe——四个文件是一套。

**回滚前先备份 `GalahData/`**，它是唯一不可再生的数据。

双向兼容性已实测（`tools/rollback-check.ps1`，9/9 通过）：
1.x 读 2.0 创建的数据不隔离不损坏，2.0 读 1.x 碰过的数据同样正常。
