# 2.0 是绿色版吗？

**是。** 但形态和 1.x 不同，这点必须说清楚。

## 产物在哪

本地发布产物在仓库根目录的 **`artifact/`**：

```
D:\Rolan_desktop\galah2\artifact\
  Galah2.exe              27.4 MB
  libSkiaSharp.dll         9.0 MB
  av_libglesv2.dll         5.2 MB
  libHarfBuzzSharp.dll     1.7 MB
  ────────────────────────────────
  共 4 个文件              43.2 MB
```

生成方式：

```powershell
powershell -ExecutionPolicy Bypass -File tools/publish-aot.ps1
```

这个脚本会先检查 MSVC 工具链（缺了会直接说清楚缺什么，而不是甩一堆 ILCompiler 报错），
发布完自动删掉 pdb，最后打印产物清单。

CI 的产物同样叫 `artifact/`，作为 GitHub Actions 的 artifact 上传；打 tag 时压成
`Galah2-windows-x64.zip` 挂到 Release。

## 怎么用

**整个目录一起拷走**，放到哪都能跑。只拷 `Galah2.exe` 是打不开的——三个原生 dll 缺一不可。

## 实测结论

把发布目录整个拷到全新空目录里启动，验证过：

| 属性 | 结果 |
|---|---|
| 从任意目录运行 | 是，无需安装 |
| 需要 .NET 运行时 | 不需要（原生编译） |
| 用户数据位置 | exe 旁边的 `GalahData/` |
| 写 `%APPDATA%` / `%LOCALAPPDATA%` | 一处都不写 |
| 首次运行写注册表 | 不写（自启要用户主动勾选） |

验证工具是 `tools/PortableCheck`：它把发布目录拷到临时文件夹、启动、比对启动前后的
`%LOCALAPPDATA%` 条目差异、并检查注册表 Run 键。9 条断言全过才判定为绿色版。

## 和 1.x 的两个差别

### 1. 不是一个 exe，是 4 个文件

| | 1.x | 2.0 |
|---|---|---|
| 形态 | 单个 `Galah.exe` | `Galah2.exe` + 3 个原生 dll |
| 体积 | 68.6 MB | 43.2 MB |
| 打包方式 | `PublishSingleFile=true` | 不能单文件 |

原因是硬限制：**.NET 8 里 `PublishAot` 和 `PublishSingleFile` 互斥**，实测直接报错
`PublishAot and PublishSingleFile cannot be specified at the same time`。

三个 dll 是 Skia / HarfBuzz / GLES 渲染栈的原生库，Avalonia 画一帧就要用，必须挨着 exe 放。
所以 2.0 的绿色版是**一个文件夹**，不是**一个文件**。

发布包里已经剔除 pdb（否则会多 175 MB 调试符号）。

### 2. 体积反而更小

68.6 MB → 43.2 MB。单文件的牺牲换来的是原生编译省下的运行时体积。

## CI 曾经会把发布包做坏

修这个问题之前，workflow 只上传 `artifact/Galah2.exe` 一个文件，冒烟测试也只拷 exe。
**用户下载下来是打不开的**——三个 dll 不在包里。

现在：上传整个目录、tag 发布打成 zip、冒烟测试拷整个目录、并加 `PortableCheck` 守门。

## 唯一对外部的写入

`HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 的 `Galah` 值，只在用户勾选
"开机自动启动"时写入，取消勾选时删除。这是 1.x 就有的唯一注册表写入，不属于"安装"。
