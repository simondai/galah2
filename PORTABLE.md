# 2.0 是绿色版吗？

**是。** 但形态和 1.x 不同，这点必须说清楚。

## 实测结论

在全新空目录里只放发布产物，程序能直接启动，并且验证过：

| 属性 | 结果 |
|---|---|
| 从任意目录运行 | ✅ 无需安装 |
| 需要 .NET 运行时 | ❌ 不需要（原生编译） |
| 用户数据位置 | ✅ exe 旁边的 `GalahData/` |
| 是否写 `%APPDATA%` / `%LOCALAPPDATA%` | ❌ 一处都不写 |
| 首次运行是否写注册表 | ❌ 不写（自启要用户主动勾选） |

实测证据见 `tools/PortableCheck`：它把发布目录整个拷到临时文件夹、启动、比对启动前后的
`%LOCALAPPDATA%` 条目差异、并检查注册表 Run 键。9 条断言全过才算绿色。

## 和 1.x 的两个差别

### 1. 不是一个 exe，是 4 个文件

| | 1.x | 2.0 |
|---|---|---|
| 产物 | 单个 `Galah.exe` 68.6 MB | `Galah2.exe` + 3 个原生 dll，共 44 MB |
| 打包 | `PublishSingleFile=true` | 不能单文件 |

原因是硬限制：**.NET 8 里 `PublishAot` 和 `PublishSingleFile` 互斥**（实测报错
`PublishAot and PublishSingleFile cannot be specified at the same time`）。

三个 dll 是 Skia / HarfBuzz / GLES 渲染栈的原生库，Avalonia 画一帧就要用，必须挨着 exe 放。
所以 2.0 的绿色版是**一个文件夹**，不是**一个文件**。

发布包里已经剔除了 pdb（否则会多 175 MB）。

### 2. 体积反而更小

68.6 MB → 44 MB。单文件牺牲换来的是原生编译省下的运行时体积。

## CI 曾经会把发布包做坏

修这个问题之前，workflow 只上传 `artifact/Galah2.exe` 一个文件，冒烟测试也只拷 exe。
**用户下载下来是打不开的**——三个 dll 不在包里。

现在：上传整个目录、tag 发布打成 zip、冒烟测试拷整个目录、并加 `PortableCheck` 守门。

## 唯一对外部的写入

`HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 的 `Galah` 值，只在用户勾选"开机自动
启动"时写入，取消勾选时删除。这是 1.x 就有的唯一注册表写入，不属于"安装"。
