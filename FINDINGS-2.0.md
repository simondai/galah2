# Galah 2.0 — 实测结论（最终）

> 所有数字均为本机实测（Win11 26200 / RDP 会话）。
> 指标 = `Private Working Set`（任务管理器"内存"列）。
> Working Set 含共享页，会虚高，不作为承诺依据。

---

## 1. 三个主要结论

### 1.1 Avalonia 省内存，但幅度远小于预期，而且待机反而更费

| 状态 | WPF 1.0 | Avalonia 2.0（托管） | Avalonia 2.0（AOT） |
|---|---|---|---|
| **待机（托盘）** | **11.1 MB** | 27.9 MB | 18.9 MB |
| **面板展开** | **57.4 MB** | 50.1–54.3 MB | 63.6 MB |

- 面板展开：**省 5–13%**，不是宣传里那种量级
- 待机：**更费**（27.9 / 18.9 vs 11.1）
- **AOT 不是内存优化**：待机比托管版好（18.9 vs 27.9），但面板展开更差（63.6 vs 54.3）

### 1.2 FluentTheme 是硬依赖，砍不掉

实测（3 组对照，真实截图 + 像素统计判定）：

| 实验（无主题） | 结果 |
|---|---|
| 硬编码 `TextBlock` | ✅ 正常渲染 |
| `ItemsControl` + XAML `ItemTemplate` | ❌ **0 个元素** |
| `ItemsControl` + 纯 C# 构建 + 显式 `FuncDataTemplate` | ❌ **仍然 0 个元素** |

**Avalonia 的 `ItemsControl` 容器生成依赖主题包提供的默认容器主题。**
没有 `FluentTheme`，每个列表都是空盒子。这不是绑定问题（`ItemsSource` 已确认为非空，
代码直接赋值也一样）。

→ 省内存的唯一办法是**不用 `ItemsControl`**（手搓 `StackPanel` + 手动填充），
而不是"砍主题保留列表"。代价是所有列表类 UI 都要手写。

### 1.3 "隐藏后内存回落"不成立

| 实现 | 展开 | 隐藏 18s |
|---|---|---|
| WPF 1.0 | 57.4 MB | 58.1 MB |
| Avalonia + 主题 | 50.1 MB | 50.3 MB |

早期探针看到的 30.7 → 18.9 回落是 `Task.Delay` + `Dispatcher.Post` 的时序巧合，
受控对比中不存在。**不要写进 DoD。**

---

## 2. NativeAOT — 已跑通，收益在启动和体积

### 2.1 工具链

本机原本没有 MSVC 链接器。已提权安装：

- **VS 2022 BuildTools 17.14.45** + VCTools 工作负载（含 MSVC 14.44.35207）
- **Windows SDK 10.0.26100.0**

`dotnet publish -c Release -r win-x64 -p:PublishAot=true` 现在可成功产出原生 exe。

### 2.2 产物

| | 1.0（WPF 单文件） | 2.0 AOT |
|---|---|---|
| 发布体积 | **68.6 MB** | **23.8 MB** |
| 依赖 | 需 .NET 运行时 | 无（原生） |

**体积缩小 65%。**

### 2.3 AOT 路上必须踩的两个坑（都已解决）

**坑 1 — 内置 COM 不可用。** `BuiltInComInteropSupport=false` 时，1.0 的
`[ComImport] IShellLinkW` 整套失效。已改为手工调 vtable
（`Services/Interop/ShellLinkCom.cs`），验证 11/11 断言通过：

```
PASS  TryCreate                 PASS  target
PASS  file on disk [1914 bytes] PASS  arguments
PASS  Resolve().Ok              PASS  workdir
PASS  icon index                PASS  icon source
PASS  missing reports error     PASS  corrupt does not throw
```

**坑 2 — JSON 反射序列化在 AOT 下直接崩。** 首次 AOT 运行崩在这个异常：

```
Unhandled Exception: System.InvalidOperationException: JsonSerializerIsReflectionDisabled
   at System.Text.Json.JsonSerializer.GetTypeInfo(...)
   at Galah2.Persistence.JsonFileStore`1.Load()
   at Galah2.Persistence.SettingsStore.Load()
   at Galah2.Services.AppRuntime..ctor(...)
```

它是**启动前**发生的（窗口还没建），所以看起来像"双击无反应"。
修复：`Persistence/JsonContext.cs` 注册源生成器 + `SerializerOptions.TypeInfoResolver`。

⚠ **注意**：编译期仍有 IL3050 警告（泛型 `T` 无法静态分析），
但运行时通过 `TypeInfoResolver` 正常工作——**已验证**。不要因为警告消失才认为修好了。

**这条是最重要的工程教训**：AOT 下"能编译"≠"能运行"。
`dotnet publish` 成功、有 IL3050 警告时，必须真实启动一次，否则会漏掉启动即崩。

---

## 3. 总结：2.0 该不该做

按实测，诚实的结论是：

| 你可能想要的目标 | 实测结果 | 判断 |
|---|---|---|
| 待机省内存 | 11.1 → 18.9~27.9 MB | ❌ 反向 |
| 面板省内存 | 57.4 → 50.1 MB | ⚠ 5–13%，不显著 |
| 启动更快 | AOT，未精确测时 | ✅ 原生无 JIT，确凿收益 |
| **体积更小** | **68.6 → 23.8 MB** | ✅ **65%，最确定的收益** |
| 无运行时依赖 | AOT 单 exe | ✅ 确凿 |

**如果目标是"常驻内存"，Avalonia+AOT 这条路没有兑现。**
真正兑现的是**体积**和**启动/无依赖**。

要不要继续，取决于哪个目标对你更重要。

---

## 4. 已经真实跑通的部分（可复现）

| 能力 | 验证方式 | 结果 |
|---|---|---|
| COM 无 `ComImport` 写/读 .lnk | `tools/ComProbe` | ✅ 11/11 |
| 托盘图标 | 真实进程 | ✅ |
| 全局热键 | 外部注册同名热键失败 | ✅ 已占用 |
| 单实例 + 转发 Toggle | 双进程计数 | ✅ 恒为 1 |
| 面板渲染（托管 + AOT） | 720×460 截图 + 像素统计 | ✅ 文字/底色均正常 |
| 读 1.0 的 catalog.json | 真实数据 | ✅ 2 分类 8 条目 |
| NativeAOT 启动 + 开面板 | 真实进程 | ✅ |
| JSON 源生成器 | AOT 运行时 | ✅ |

截图证据：`evidence/panel-aot.png`

---

## 5. COM 槽位表（维护必读）

**槽位索引就是 ABI 契约，错一位就写坏内存。**

| 坑 | 现象 | 真因 |
|---|---|---|
| `GetIconLocation` 参数错位 | `AccessViolationException` 崩进程 | 签名是 `(this, buf, cch, out int)`，out 在**第 4 位** |
| `Save` 用错 vtable | 写不出文件 | `IPersistFile` 是另一接口，必须 QI |
| `ref Guid` + `fixed` | `CS0213` | `ref` 参数本身已固定 |
| AddRef 读到 `0xCCCC..CCFF` | 疑似 vtable 损坏 | 探针多解一层指针 |

正确槽位：
- `IShellLinkW`：GetPath=3, GetWorkingDirectory=8, SetWorkingDirectory=9,
  GetArguments=10, SetArguments=11, GetIconLocation=16, SetIconLocation=17,
  Resolve=19, SetPath=20
- `IPersistFile`：Load=5, Save=6
