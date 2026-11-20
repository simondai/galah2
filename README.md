# Galah 2.0

Windows 桌面启动器（launcher），以作者家的宠物凤头鹦鹉命名。绿色免安装，数据就在 exe 旁边。

用 **Avalonia 11 + NativeAOT** 重写；1.x 是 WPF 版本，在另一个仓库 [simondai/galah](https://github.com/simondai/galah)。

![平台](https://img.shields.io/badge/%E5%B9%B3%E5%8F%B0-Windows-blue) ![产物](https://img.shields.io/badge/%E4%BA%A7%E7%89%A9-43.2%20MB-success) ![运行时](https://img.shields.io/badge/%E8%BF%90%E8%A1%8C%E6%97%B6-%E6%97%A0%E9%9C%80%E5%AE%89%E8%A3%85-success)

## 它是什么

把常用的程序、文件、文件夹、网址、系统设置页收进一个面板：分类页签 + 图标磁贴网格，
单击/双击启动，托盘常驻后台，全局热键（Alt+Space）随时唤出。
所有数据（分类、条目、设置、窗口状态）都落在 exe 同目录的 `GalahData/` 文件夹，随文件夹一起搬走。

- **绿色免安装**：4 个文件共 43.2 MB，原生编译，不装 .NET 运行时、不写注册表、不留后台服务
- **默认不联网**：没有任何自动网络行为；唯一的联网操作是你在编辑网址条目时手动点"获取网站图标"，且只访问该站点本身
- **持久可靠**：写盘原子替换 + 双备份轮换（`.bak.1`/`.bak.2`），损坏自动隔离，一次用户动作一次写盘
- **可直接读 1.x 数据**：`GalahData/` 格式未变，两个版本可以互相读对方的数据

> **先读 [FINDINGS-2.0.md](FINDINGS-2.0.md)，别默认这次重写省内存。**
> 实测下来内存并不是立项时想的那样：**待机反而比 1.x 更费**，面板只省 5~13%。
> 2.0 真正兑现的是 **体积小 37%**（68.6 MB → 43.2 MB）、**不依赖 .NET 运行时**、
> 以及原生启动速度。

---

## 快速开始

拿到产物后，直接双击 `artifact/Galah2.exe` 即可。启动后驻留托盘（不弹面板），
按 **Alt+Space** 或点托盘图标唤出。

## 产物与发布

自己构建的话，产物在仓库根目录的 **`artifact/`**（4 个文件，43.2 MB）：

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
| 面板可缩放、位置与尺寸记忆 | 可用，7/7 探针 |
| 添加选择器列表带图标 | 可用 |
| Ctrl+1..9 直达分类，滚轮切分类 | 可用 |
| 跨分类拖动条目（把瓦片拖到分类标签上） | 可用 |
| Ctrl+Tab / Ctrl+Shift+Tab 循环切换分类 | 可用 |
| **最大化状态记忆** | **不适用**（无边框面板不提供最大化） |
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
tools/GeometryProbe/                   几何与选择器图标探针
tools/rollback-check.ps1             1.x <-> 2.0 数据兼容
docs/PROCESS-AUDIT.md                流程自查（缺口诚实记录）
docs/specs/                          需求规格（每轮开发前写）
CONTRIBUTING.md                      九步开发流程
tools/perf/measure.ps1                 内存测量
tools/publish-aot.ps1                  AOT 发布
```

## 系统要求

- Windows 10 1709+ / Windows 11（x64）
- 运行时自包含，无需安装 .NET

## 二次开发

### 开发环境

- [Visual Studio 2022](https://visualstudio.microsoft.com/)（或任意顺手的 C# IDE / VS Code）
- **.NET SDK 8.0+**
- **VS 2022 Build Tools 的「使用 C++ 的桌面开发」工作负载 + Windows SDK** ——
  NativeAOT 用 MSVC 的 `link.exe` 链接，缺了只能编译不能发布
- Git

克隆后两条命令验证环境：

    dotnet build Galah2.sln -c Release
    dotnet run --project tools/PanelProbe -c Release

只想跑起来不改 AOT，`dotnet run --project src/Galah2 -c Release` 就行（JIT 模式，启动慢些）。

### 本项目的开发工具链

- **Alma**（AI 结对编程）：需求拆解、实现、测试与调试的主力
- **Git + GitHub Actions**：版本管理；CI 自动跑构建、七个探针、AOT 发布与绿色版校验
- **dotnet CLI**：构建、测试、AOT 发布，不依赖 IDE
- **PowerShell + Win32/UIA**：进程级验收脚本与诊断

### 代码地图

| 你要改的东西 | 去哪 |
|---|---|
| 面板长什么样、交互 | `src/Galah2/Views/PanelWindow.axaml` + `.axaml.cs` |
| 颜色、深浅主题 | `src/Galah2/Styles/Tokens.axaml`（两套配色）+ `Baseline.axaml`（控件样式） |
| 面板命令、分类与条目逻辑 | `src/Galah2/ViewModels/PanelViewModel.cs` |
| 启动流程、托盘、热键、几何记忆 | `src/Galah2/Services/AppRuntime.cs` |
| 图标提取（含 .msc 的 COM 路径） | `src/Galah2/Services/IconService.cs` |
| JSON 存储与源生成器 | `src/Galah2/Persistence/` |
| 手写 COM vtable（AOT 必需） | `src/Galah2/Services/Interop/` |

改完按 [CONTRIBUTING.md](CONTRIBUTING.md) 的九步流程走：先写 `docs/specs/`，再动代码，
跑全套探针，最后提交。

## 名字与致敬

Galah（凤头鹦鹉）是作者家的宠物。粉红凤头鹦鹉聪明、话多、精力旺盛——大概也是一个好启动器的样子。

本项目致敬 [Rolan](https://getrolan.com/) 的作者。作者本人是 Rolan 2 近十年的老用户；Galah 的
功能原型参考自 Rolan，但全部代码从零实现，不含任何 Rolan 的代码、图标或素材。

## 许可证

MIT — 见 [LICENSE](LICENSE)。
