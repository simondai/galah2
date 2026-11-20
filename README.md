# Galah 2.0

Windows 启动器，用 **Avalonia 11 + NativeAOT** 重写。

1.x 在另一个仓库 `simondai/galah`（WPF）；本仓库是 2.0 重写。

> **先读 [FINDINGS-2.0.md](FINDINGS-2.0.md)，别默认这次重写省内存。**
> 实测下来内存并不是立项时想的那样：**待机反而比 1.x 更费**，面板只省 5~13%。
> 2.0 真正兑现的是 **体积小 37%**（68.6 MB → 43.2 MB）、**不依赖 .NET 运行时**、
> 以及原生启动速度。

---

## 产物与发布

本地发布产物在仓库根目录的 **`artifact/`**（4 个文件，43.2 MB）：

```powershell
powershell -ExecutionPolicy Bypass -File tools/publish-aot.ps1
```

绿色版怎么用、为什么不是单文件、CI 曾经怎么把包做坏，都写在 **[PORTABLE.md](PORTABLE.md)**。

## 功能状态

| 功能 | 状态 |
|---|---|
| 托盘图标、左键唤出、右键原生菜单 | 可用 |
| 全局热键（消息窗常驻，面板从未显示也能唤出） | 可用 |
| 单实例 + 转发 toggle | 可用 |
| 面板：标题栏、☰ 菜单、条目网格、右键菜单、拖动 | 可用 |
| 分类条顶部/左侧切换，图标三档尺寸 | 可用 |
| 直接读 1.x 的 `GalahData/catalog.json` | 可用 |
| 条目启动（exe / lnk / url / protocol） | 可用 |
| 桌面快捷方式读写，**不用 `ComImport`** | 可用，11/11 探针 |
| 图标提取 + PNG 缓存（含 .msc） | 可用，12/12 探针 |
| 设置（主题、图标尺寸、分类位置、材质、点击方式、面板位置、热键、自启、快捷方式） | 可用，17/17 探针 |
| 开机自启（写 HKCU Run，启动时再断言） | 可用 |
| 添加选择器（4 个来源、搜索、多选） | 可用 |
| 编辑对话框（带提交校验门） | 可用 |
| 库维护：扫描、清失效、去重、排序、重建图标缓存 | 可用 |
| 整库导出与导入 | 可用 |
| 提示框与确认框 | 可用 |
| 拖放文件、粘贴 URL | 可用 |
| 启动失败的 toast 提示 | 可用 |
| URL 条目抓取站点图标（仅手动按钮触发） | 可用 |
| 浏览按钮：选文件、选目录、选图标图片 | 可用 |
| 面板命令面 | 可用，52/52 探针 |
| 进程级验收（托盘、toggle、关闭后重开） | 可用，11/11 |
| 绿色版自检 | 可用，9/9 |
| 回滚兼容性（1.x ↔ 2.0 数据） | 可用，9/9 |
| 对话框（提示、确认） | 可用，8/8 探针 |
| NativeAOT 发布 | 可用，见 [PORTABLE.md](PORTABLE.md) |

---

## 构建

```bash
dotnet build Galah2.sln -c Release
```

运行：

```bash
dotnet run --project src/Galah2 -c Release
```

exe 会在自己旁边找 `GalahData/`（和 1.x 同一套约定），把已有的内容库拷进去就能用真实数据测。

### NativeAOT

需要 **VS 2022 或 Build Tools 的「使用 C++ 的桌面开发」工作负载**加 Windows SDK——
ILCompiler 用 MSVC 的 `link.exe` 链接。

```bash
dotnet publish src/Galah2 -c Release -r win-x64 -p:PublishAot=true -o artifact
```

**AOT 必须真实启动验证，不能只看编译通过。** `JsonSerializer` 的反射序列化在 AOT 下会抛
`JsonSerializerIsReflectionDisabled`，而且是在**任何窗口创建之前**——表现出来就是
"双击没反应"。发布成功不代表能跑。必须启动一次，确认托盘图标出来了。

---

## 验收

六个工具，全部可独立运行：

```bash
dotnet run --project tools/ComProbe      -c Release   # 快捷方式 COM 往返（11 条）
dotnet run --project tools/IconProbe     -c Release   # 图标提取（12 条）
dotnet run --project tools/SettingsProbe -c Release   # 设置与主题（17 条）
dotnet run --project tools/PanelProbe    -c Release   # 面板命令面（52 条）
dotnet run --project tools/UiAcceptance  -c Release -- <exe>  # 进程级（11 条）
dotnet run --project tools/PortableCheck -c Release -- artifact  # 绿色版（9 条）
```

内存测量：

```powershell
powershell -ExecutionPolicy Bypass -File tools/perf/measure.ps1
```

报告 **Private Working Set**（任务管理器"内存"列那个数）。Working Set 含共享 DLL 页，
会虚高近一倍，不要拿它当应用自己的内存。

---

## 目录结构

```
src/Galah2/
  Services/Interop/ShellLinkCom.cs     手写 COM vtable（AOT 没有内置包装器）
  Services/Interop/ShellIconHandler.cs  shell 图标处理器（.msc 那条路）
  Services/Interop/Win32.cs            P/Invoke 面
  Services/                            托盘、热键、单实例、位置、启动、主题、toast
  Persistence/                         JSON 存储 + 源生成 JsonContext
  Views/ ViewModels/                   Avalonia 界面
  Styles/Tokens.axaml                  全部颜色 token（深浅两套）
  Styles/Baseline.axaml                全部控件样式
tools/ComProbe/                        COM 往返探针
tools/IconProbe/                       图标提取探针
tools/SettingsProbe/                   设置与主题探针
tools/PanelProbe/                      面板命令面探针
tools/UiAcceptance/                    进程级验收
tools/PortableCheck/                   绿色版验证
tools/DialogProbe/                     对话框探针
tools/rollback-check.ps1             1.x <-> 2.0 数据兼容
docs/PROCESS-AUDIT.md                流程自查（缺口诚实记录）
docs/specs/                          需求规格（每轮开发前写）
CONTRIBUTING.md                      九步开发流程
tools/perf/measure.ps1                 内存测量
tools/publish-aot.ps1                  AOT 发布
```

## 许可证

MIT — 见 [LICENSE](LICENSE)。
