# Galah2 审计整改修复报告（2026-11-25）

> 输入：`AUDIT-2026-11-25-FULL.md`（120 条结论）、`REVIEW-2026-11-25.md`、两路反证复核、独立 Code Review。
> 计划：`docs/PLAN-2026-11-25-REMEDIATION.md`。
> 流程：按用户规定的 9 步（需求分析 → 方案 → 计划 → 开发 → 自测 → Review → 集成验收 → 发布交付 → 验证维护）执行。
> **交付物**：本报告 + 4 个 Git 提交 + 约 40 个文件的改动。

---

## 一、9 步流程执行结果

| 步骤 | 完成标志 | 本轮实际 | 状态 |
|---|---|---|---|
| 1 需求分析 | 明确需求和验收标准 | 计划文档 §1：范围、边界、6 条验收标准 | ✅ |
| 2 方案设计 | 方案能指导开发 | 计划文档 §2 复核裁定 + §3.1/3.2 设计原则与模块划分 | ✅ |
| 3 制定计划 | 知道做什么、如何验证 | 计划文档 §3.3/3.4 批次、依赖、风险、回滚 | ✅ |
| 4 开发实现 | 形成可验证改动，分批提交 | A1/A2/A3 三批 + 文档批次，共 4 次提交 | ✅ |
| 5 自测与自动化检查 | 满足基本质量要求 | `git diff --check` 无空白错误；31 项 + 10 项静态检查全绿 | ⚠️ 见 §5 |
| 6 Code Review | 意见已处理、获得批准 | 独立代理逐 hunk 审查，2 条必修 + 6 条建议全部处理并复验 | ✅ 降级见 §6 |
| 7 集成与验收 | 满足发布条件 | 逐条对照验收标准；文档数字基线一致 | ⚠️ 编译项待 CI |
| 8 发布与交付 | 目标用户可使用 | CHANGELOG 修复节 + 操作说明 + 回滚方案 | ✅ 未真实发版 |
| 9 验证与维护 | 问题进入下一轮迭代 | 本报告 + 阶段 B/C/D 遗留清单 | ✅ |

**两项降级说明**：① 本机无 .NET SDK / MSBuild，编译与 12 个探针无法在此运行；② 单人开发、无 `gh`、无远端 PR 权限，步骤 6 降级为「独立代理审查 + 意见处理复验」，未创建 PR。两者均需在有 SDK 的机器 / CI 上补齐。

---

## 二、复核裁定：先纠错，再改代码

两路反证复核（技术条线 + 工程文档条线）的结论：**审计的数字基线零算错**（12 探针 / 316 调用点 / 43.6 MiB / 62M / 60 提交 / schema 1-1-3 / 默认 8-10 全部重算一致），但若干**严重级别与文档口径**需要修正。照单全改会把设计取舍和不可达分支当 bug 修，因此先裁定。

### 2.1 撤回（不修）

| 条目 | 事实 | 处置 |
|---|---|---|
| 1.1 #26 `areas[0]` 越界 | `MonitorGeometry.cs:57-61` 保证 `EnumerateWorkAreas()` 恒非空 | 撤回，不改 |
| 1.2 `ShellLinkCom` 重复 Dispose 二次 Release | 所有调用点单次 Dispose，`_p` 是 `readonly` | 撤回，前提不可达 |
| 2.1 B3「CHANGELOG 声称已修 DPI」 | CHANGELOG 全文 Grep `DPI\|dpi\|缩放\|RenderScaling` **零命中**；真实出处是 `2026-11-23-panel-teleport-fix.md:96` 自认「本轮不修」 | 撤回该子项 |

### 2.2 降级 / 精确化

| 条目 | 原结论 | 修正 |
|---|---|---|
| 1.1 #20 ④ | 无 `*` 格式静默截断是产品缺陷 | 不可编辑 ComboBox + `Sanitized()`/`Commit()` 三重归一，UI 完全不可达 → 死分支，不改生成逻辑 |
| 1.1 #2 | 设置校正「主路径整体失效 / 非法值原样落盘」 | `SettingsStore.Load():37` 与 `MigrationService:170` 都有全字段 `Sanitized()`；真实缺陷是 `Update()` 只回写 5/12 字段 + UI 绕过校正入口。**仍修，按真实根因修** |
| 1.1 #1 | JSON 显式 null「启动即崩且不走隔离」 | `JsonFileStore:134` 对整文档 null 有隔离回退；settings 域被 `Sanitized()` 兜住 → **收窄为 ui-state/catalog 域的字段级 null** |
| 1.1 #5/#27/#12 | 标 [确信] 的「2~3 次」「presenter 不消费」「报 193」 | 依赖审计第六节自认未验证的 Avalonia/Win32 语义 → 静态部分照修，次数与错误码不写进结论 |
| 1.1 #18 | 导入只回写 10 个字段 | 实为 **9** 条赋值；修复动作不变 |
| 1.1 #13 | `lpCurrentDirectory` 是 `string` | 实为 `string?` |
| 2.1 A 末行 | 文档默认密码 14/14 | `randpass spec §7` 与 `PasswordOptions.cs:4-6` 已改 8/10，只有 `CHANGELOG.md:49` 未同步 |
| 2.1 B5 | 提交数 19/20/38 与 60 不符 | 都是 11-19/20 历史快照（`c741884`、`b355100`），非错误 → 不改文档 |
| 4.1 #3 | `ActiveTabName` 产品代码零引用 | `PanelViewModel.cs:390` 有 `Raise(nameof(...))` → 改为「无读取消费点」，不删 |

### 2.3 历史成因（解释「为什么会留下这些不一致」）

- 探针在 6 天内分 6 批加入（11-19 ComProbe → 11-24 PasswordProbe），文档在 8/9/10 套时分别定稿，从未统一回写。
- `7e5f487`（11-20「correct the size claim」）把体积改成 43.2 MB 时漏同步 `PLAN-2.0.md`，所以 PLAN 停留 26.4 MB。
- `b355100`（11-20「move 2.0 to its own repository」）把 2.0 从 `galah` 的 `v2` 分支迁到 `galah2` 的 `main`，`CHANGELOG.md:6` 是迁移前残留。
- `PLAN-2.0.md:39/50` 组合：A 类整份搬入 `Win32.cs`，B 类却把托盘换成 Avalonia `TrayIcon` → 整套托盘 P/Invoke 变死代码。
- `6ea6aff`（**审计当天**）才新增 `tools/release.ps1`，与更早的 CI zip 路径并存，尚未整合。
- `7ce48ff`（11-24）把默认密码改 8/10 并升 ui-state schema 3，spec 同步了、CHANGELOG 漏改。

---

## 三、已修清单

### A1 · P0 数据安全与工程阻断（提交 `58b28f4`）

| 文件 | 改动 |
|---|---|
| `.gitignore` | 新增 `dist/`，消除一次 `git add .` 纳入约 62 MB 二进制的风险 |
| `Persistence/JsonFileStore.cs` | 反序列化/规范化捕获扩展到 `NotSupportedException`、`ArgumentException`（AOT 漏注册类型）；`QuarantineAndRecover` 的备份解析与序列化同样加兜底，坏备份不再绕过隔离链 |
| `Persistence/UiStateStore.cs` | `Window` / `RandPassWindow` 字段级 null 归一；两个窗口都做非有限坐标与最小尺寸校正 |
| `Persistence/CatalogStore.cs` | 新增 `NormalizeLoadedDocument()`：`Tabs` / `tab.Items` / `item.Icon` / 字符串字段的 null 归一，删除 `MoveItemToTab` 里比较 itemId 与 tabId 的无效守卫 |
| `Persistence/SettingsStore.cs` | 新增 `Commit()`（全字段 `Sanitized()`）与 `Replace()`（导入用，且强制写本机 `CurrentSchema`）；`Update()` 收口到 `Commit()`，不再只回写 5 个字段 |
| `ViewModels/SettingsViewModel.cs` | 提交改走 `_store.Commit()`，不再直接 `Save()` 绕过归一化 |
| `Services/MigrationService.cs` | 导入由 9 字段逐项赋值改为 `Replace()` 整份替换（不再丢 17+ 个偏好）；导出/读取改用带 `UnsafeRelaxedJsonEscaping` 且 `TypeInfoResolver = JsonContext.Default` 的 `JsonOptions`（中文不再转义，AOT 仍安全） |

### A2 · P1 运行时行为（提交 `3860d93`）

| 文件 | 改动 |
|---|---|
| `Services/ThemeService.cs` | 重写：generation 机制使「固定主题 → 跟随系统」能重启监听线程；`volatile _generation`；`Dispose()` 幂等；新增 `IsWatching` 供回归探针 |
| `Services/AppRuntime.cs` | `Dispose()` 幂等；两处 `PanelPlacement.Resolve` 都传 `scale` |
| `ViewModels/PanelViewModel.cs` | 点击闸门由 `<` 改 `!=`（单击模式下双击不再二次启动），默认参数改 `-1` 表示「非手势入口直接启动」 |
| `Services/LauncherService.cs` | `.msi/.bat/.cmd/.com` 与 `.lnk` 一并走 `ShellOpen`；工作目录兜底由 `""` 改 `null`（避免 `ERROR_DIRECTORY` 267） |
| `Services/ProtocolSafety.cs` | 白名单补 `search-ms` / `ms-about`，移除无效的 `search` / `about`；`rest` 加长度与控制字符约束；删除死 using |
| `Services/MigrationService.cs` | 见 A1 |
| `Services/FaviconService.cs` | 允许 http→https 跳转（限 3 跳）；取消/超时异常重新抛出，让外层正确报「超时」而非「站点没有图标」 |
| `Services/PasswordGenerator.cs` | 双位分支遵守「无类似字符」且复制抽到的数字（`c+c`），不再另随机 `d` |
| `Services/TabNavigation.cs` | 用正数模运算，任意 delta 都返回合法索引；无选中时方向正确 |
| `Services/PanelPlacement.cs` | `EdgeMargin` 按 `scale` 换算，新增可选参数 `scale = 1.0` |
| `Services/IconService.cs` | 超时 HICON 由 worker 或调用方按 CAS 竞态归属销毁；`ManualResetEventSlim` 由 worker 独占释放 |
| `Services/Interop/ShellLinkCom.cs` | 4 个 setter 的 `CoTaskMem` 用 `try/finally` 释放；IID 内存改为加锁的一次性缓存 |
| `Services/Interop/ShellIconHandler.cs` | `IconSizeRequest` 高字正确移位；修正与 Win32 语义相反的「减一」注释 |
| `Services/Interop/Win32.cs` | `WM_HOTKEY` 由 `0x0303` 改 `0x0312` |
| `Views/PanelWindow.axaml.cs` / `RandPassWindow.axaml.cs` | `ResizeTo` 全程设备像素、末行转回 DIP；左/上边缘缩放保留用户放置的对边；纯水平滚轮 `Delta.Y == 0` 早退 |
| 5 个对话框 `.axaml` | 补 `ShowInTaskbar="False"` |
| `ConfirmWindow` / `PromptWindow` `.axaml.cs` | 新增 `OnKeyDown`：Esc 取消、Enter 确认 |
| `Views/RandPassWindow.axaml` | 「两位数字概率」→「双位数字（25% 概率）」 |

### A3 · 探针、工程配置与文档同步（提交见 §8）

| 文件 | 改动 |
|---|---|
| `tools/ComProbe` / `SettingsProbe` / `DialogProbe` / `PanelProbe` | 4 处恒真/重复断言改为可失败断言；新增 6 条回归断言（字段级 null 启动、Commit 全字段归一化、主题监听重启、单击模式双击不重复启动、右键 Open 不受点击模式影响等） |
| `tools/perf/measure.ps1` / `tools/rollback-check.ps1` | 默认路径改为仓库相对 + `x64`；`rollback-check` 的 `-V1` 改为显式必填并给出可操作报错 |
| `.github/workflows/build2.yml` | 新增 `actions/cache@v4`（按 `**/*.csproj` hash 键，因仓库无 lock 文件故不用 `cache: true`）；补跑 `DragArmProbe` / `PasswordProbe` |
| `README.md` / `CONTRIBUTING.md` | 验收命令补齐 12 个探针，断言数按实测基线统一（`Check(` 调用点合计 **322**）；目录树补 3 个探针；修正 `bin/x64/Release` 路径 |
| `CHANGELOG.md` | 新增「2026-11-25 全量审计整改」节（用户可感知变化 / 怎么验证 / 怎么回滚 / 已知未完成）；默认密码 14/14 → 8/10；RandPass 描述与实现对齐 |
| `PORTABLE.md` | 补记第二条发布路径 `tools/release.ps1` 及两条路径差异 |
| `PLAN-2.0.md` | 顶部加归档说明，明确其数字是 11-19/20 历史快照 |

**探针调用点新基线**：Com 11、Icon 8、Settings 27、Panel 126、UiAcceptance 11、PortableCheck 9、Dialog 60、Geometry 14、Material 5、Interaction 19、DragArm 10、Password 22 —— 合计 **322**（原 316 + 本轮 6 条新增回归断言）。

---

## 四、Code Review 与意见处理（步骤 6）

独立审查者逐 hunk 阅读 `git diff` 并回查签名，结论：**未发现确定的编译错误**，但发现 2 条必修回归和 6 条建议。全部已处理并复验。

| # | 级别 | 问题 | 处理 |
|---|---|---|---|
| M1 | 必修 | `LaunchTile(clickCount = 2)` 默认值撞上 `!=` 闸门：单击模式下右键「打开」与 PanelProbe 6 处断言全部失效，CI 必挂 | 默认值改 `-1`，仅 `clickCount >= 0` 走闸门；补 2 条回归断言 |
| M2 | 必修 | `Replace()` 会把备份里的 `settings.schema` 原样落盘，下次启动触发 `SealedForUpgrade` | `Replace()` 强制写 `SettingsDocument.CurrentSchema` |
| S1 | 建议 | `IconService` 的 `done.Dispose()` 与 worker `Set()` 并发 | `done` 所有权交给 worker，同线程 Set→Dispose |
| S2 | 建议 | `_generation` 无锁读写缺少可见性保证 | 改 `volatile` |
| S3 | 建议 | IID 缓存无同步，并发首次调用会泄漏 16 字节且拿到不同指针 | 加 `lock (_iidGate)` |
| S4 | 建议 | `CenterOnPointerArea` 未传 `scale`，高 DPI 下边距仍按 24 设备像素 | 补第 7 个参数 |
| S5 | 建议 | `JsonFileStore` 注释与实际效果不符；隔离路径自身仍裸调 `Serialize` | 改注释；备份解析/序列化与空库序列化都加兜底 |
| S6 | 建议 | 协议白名单注释「never matched」不准确；`rollback-check.ps1` 示例缺 `-V1`；双位分支冗余条件 | 改注释与示例；冗余条件保留作防御并加说明 |

---

## 五、自测与自动化检查（步骤 5）

**环境限制（必须如实记录）**：本机 `dotnet` 不在 PATH，`C:\Program Files\dotnet`、`C:\Program Files (x86)\dotnet`、`~/.dotnet`、VS MSBuild 均不存在；`~/.nuget/packages/avalonia` 也无缓存。因此 **本轮没有编译、没有运行任何探针、没有启动 AOT 产物**。CI（`build2.yml`）是唯一实际验证渠道；在有 SDK 的机器上必须先跑一次 `dotnet build Galah2.sln -c Release` 和 12 个探针再合并。

替代检查（全部执行并通过）：

- `git diff --check` → 无空白错误。
- 11 个 `.axaml` 全部 XML 合法；8 个窗口全部设置 `ShowInTaskbar="False"`。
- 31 项静态回归检查：残留坏模式（`< wanted`、`0x0303`、`AllowAutoRedirect=false`、`x = area.Right - w` 等）零命中；新增 API（`Commit`/`Replace`/`IsWatching`/幂等标志/`NormalizeLoadedDocument`/`OnKeyDown`）全部接线；`.gitignore`、README/CHANGELOG 数字、CI 与脚本配置一致。
- 复核后 10 项终检：全部 PASS（含 `LaunchTile` 闸门、`Replace` schema 归一、`volatile _generation`、IconService 竞态与超时 CAS、IID 锁、322 调用点、126/322 文档基线）。
- 独立 Review 逐 hunk 回查了所有高风险 API：Avalonia `OnKeyDown`/`Key` 枚举、源生成 JSON options 重载、`IntPtr extra = default`、`Array.Exists`、可选参数、闭包 `Interlocked.CompareExchange`、YAML 缩进 —— 均未发现确定错误。唯一无法 100% 静态排除的是本机没有 Avalonia 程序集，`OnKeyDown` 可重写性需在 CI 编译确认。

---

## 六、集成验收对照（步骤 7）

| §1.3 验收标准 | 结果 |
|---|---|
| `dotnet build Galah2.sln -c Release` 零错误 | ⚠️ 本机无 SDK，待 CI |
| 12 个探针可运行，修复点各有非恒真断言覆盖 | ✅ 断言已补齐（6 条新增）；运行待 CI |
| `.gitignore` 覆盖 `dist/` | ✅ `git status` 已无 `?? dist/` |
| 文档探针数 / 断言数 / 默认值与基线一致 | ✅ 12 探针、322 调用点、126(Panel)、27(Settings)、8/10 |
| 每批独立提交可单独 revert | ✅ 4 个提交 |
| 报告列出已修 / 撤回 / 延后 | ✅ 本报告 |

---

## 七、发布与交付（步骤 8）

- **发布说明**：`CHANGELOG.md` 的「2026-11-25 — 全量审计整改」节，含用户可感知变化、验证方法、回滚方案、已知未完成。
- **操作说明**：`README.md`「验收」给出 12 个探针命令；`PORTABLE.md` 说明两条发布路径差异。
- **回滚**：4 个提交可分别 `git revert <hash>`；数据层保留原子替换 + `.bak.1`/`.bak.2` 双备份；回滚前先备份 `GalahData/`。
- **未真实发版**：无 `gh`、无 tag 推送、无 SDK 构建产物，本轮不产出新 zip / Release。

### Git 提交

| hash | 内容 |
|---|---|
| `3281c86` | `docs: add audit triage and remediation plan` |
| `58b28f4` | `fix(persistence): normalize null JSON, gate settings writes, ignore dist` |
| `3860d93` | `fix(runtime): theme watcher, launch dispatch, geometry units, dialog behaviour` |
| 第 4 批（`git log -1` 可查） | `chore(probes,ci,docs): real assertions, probe coverage, baseline sync`（含本报告；报告无法自引用自身所在提交的哈希） |

> 提交时精确 `git add <具体文件>`，未纳入 `dist/`、未纳入工作区原有的 ` D AUDIT-REPORT.md`，也未纳入 `.workbuddy/`、两份未跟踪审计输入文档。

---

## 八、未修 / 延后（步骤 9 下一轮迭代）

### 阶段 B（结构性，需在有 SDK 的机器上做）
- `Galah2.sln` 补录 `DragArmProbe`/`PasswordProbe`，修 `InteractionProbe` 三处配置异类；CI 的 12 次 `dotnet run` 改 `--no-build`（**本轮刻意没做**：sln 用 `Release|x64` 而 `dotnet run --project` 默认配置不同，未验证前加 `--no-build` 会让 CI 找不到输出）。
- 新增 `tools/Directory.Build.props` 统一 12 个 csproj 的 `ProjectReference` 与公共属性。
- 发布双路径整合（CI zip vs `release.ps1` zip 名称/结构/notes 不同）。
- 边缘缩放 130 行重复代码去重、`DialogChrome` 统一接线、引入 xUnit 单元测试框架。
- `HotkeyService.Dispose` 未注销热键 / 未销毁窗口 / HWND 截断为 `uint`（Win32 线程语义，需实机）。
- `ItemValidator` 校验门仍只覆盖编辑对话框。

### 阶段 C（清理，需先确认探针依赖）
`Win32.cs` 约 30 个零引用 P/Invoke、5 个零引用 Command、3 个零引用画刷、过时文档归档。

### 阶段 D（实机验证，静态不可判定）
Avalonia `OnOpened` 累积注册、`Button.Click`/`DoubleTapped` 派发顺序、AOT COM stub、非 100% DPI / 多显示器几何、主题快速切换与二次 Dispose、`.msi` 启动、Esc/Enter、导入 `settings.schema > 1` 的备份。

### 计划偏差（如实标注）
1. 计划 A3 写了「sln 补录 2 个探针」，实际改为 **CI 直接按项目路径跑**，sln 补录延后到阶段 B —— 原因是本机无 SDK，sln 格式改动无法验证，误配会让整个 CI 挂掉。
2. 计划写了「CI 加 `--no-build`」，实际未做 —— 原因同上，且存在配置平台不一致风险；改为先加 NuGet 显式缓存（不依赖 lock 文件）。
3. 计划写了「`dotnet build` + 12 探针自测」，实际降级为静态检查 —— 环境无 SDK。

---

## 九、验证与维护清单（发布后）

1. 在 CI 上确认 4 个提交的全部检查通过，尤其是 `PanelProbe`（126 条）、`SettingsProbe`（27 调用点 / 29 行）、`PasswordProbe`、AOT smoke test。
2. 手工回归：主题 System→Light→System、拖左/上边缘缩放（100% / 150% / 200% DPI）、右键「打开」在两种点击模式、Esc/Enter、`.msi` 启动、导入含较高 `settings.schema` 的备份。
3. 监控：主题监听线程是否只存在一个、退出时是否无 `ObjectDisposedException`、导出文件中文是否可读、任务栏是否出现多余窗口。
4. 反馈与新缺陷进入下一轮 9 步流程，先写 spec 再改代码。

---

*本报告与实施计划均为新增文件；所有代码改动已按批次提交。本机未编译、未运行探针，相关结论以 CI 与实机为准。*
