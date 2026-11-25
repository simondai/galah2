# 审计整改实施计划（2026-11-25）

> 输入：`AUDIT-2026-11-25-FULL.md`（120 条结论）、`REVIEW-2026-11-25.md`，以及本轮两路「反证式复核」结果。
> **执行状态（2026-11-25）**：已完成 A1 / A2 / A3 三批，并经独立 Review 处理意见。逐条结果见 `FIX-REPORT-2026-11-25.md`。
> 性质：**先分析、后动手**。本文件是步骤 1~3 的产物；代码改动按此计划执行。
> 本轮不修改任何代码文件之外的用户数据，不触碰 `artifact/GalahData/`、`dist/` 里的用户 JSON。

---

## 步骤 1 · 需求分析

### 1.1 要解决什么问题

上一轮审计是**只读**的，产出了大量问题但没有任何修复。用户需要：
1. 先确认「审计结论是否可信」——把被高估、写错、不成立的部分挑出来，避免照单全改引入回归；
2. 再形成可执行的修改计划；
3. 最后按计划落地修复，并给出可追溯的修复报告。

### 1.2 使用者与边界

| 项 | 说明 |
|---|---|
| 使用者 | 单人开发者（仓库 `CHANGELOG.md:89-91` 自认「独立 Code Review（单人开发）」） |
| 运行环境 | Windows / .NET 8 / Avalonia 11 / NativeAOT / x64 |
| 本轮范围 | `src/Galah2/` 业务代码、`tools/` 探针与脚本、`.sln` / CI / `.gitignore`、根目录与 `docs/` 文档 |
| 不在范围 | 用户数据目录、真实发版（无 `gh`、无 tag 推送权限）、1.x 仓库、实机多显示器 / 非 100% DPI 验证 |
| 硬约束 | 不为了「凑条数」而改；每条改动必须能追溯到一条**复核后仍成立**的结论 |

### 1.3 验收标准（完成标志）

- [ ] `dotnet build Galah2.sln -c Release` 零错误、零新增警告级别问题。
- [ ] 12 个探针全部可 `dotnet run`；本轮修复点各有至少 1 条**非恒真**断言覆盖。
- [ ] `.gitignore` 覆盖 `dist/`，`git status` 不再出现 `?? dist/`。
- [ ] 所有文档引用的探针数量 / 断言数 / 默认值与「关键数字基线」一致。
- [ ] 每批改动独立 Git 提交，可单独 `git revert`。
- [ ] 修复报告列出「已修 / 复核后撤回 / 延后」三类及理由。

---

## 步骤 2 · 复核裁定（先纠错，再改代码）

两路反证复核的结论：**数字基线零算错，工程配置类结论绝大多数成立**；需要动的主要是**严重级别与文档口径**。照单全改会把「设计取舍」和「不可达分支」当成 bug 修，因此先裁定。

### 2.1 需要撤回 / 降级的结论（本轮**不按原结论修**）

| 审计位置 | 原结论 | 复核事实 | 裁定 |
|---|---|---|---|
| 1.1 #26 | `areas[0]` 无判空可能越界 | `MonitorGeometry.cs:57-61` 保证 `EnumerateWorkAreas()` 恒返回 ≥1 项 | **撤回**，不改代码 |
| 1.2 末条 | `ShellLinkCom` 重复 Dispose 二次 Release | 所有调用点单次 Dispose；`_p` 为 `readonly` | **撤回**（前提不可达） |
| 1.1 #20 ④ | 无 `*` 格式静默截断是产品缺陷 | `RandPassWindow.axaml:114` 是不可编辑 ComboBox，`All` 仅两个含 `*` 预设，且 `Sanitized()`/`Commit()` 三重归一 | **降级为死分支**，不改生成逻辑 |
| 1.1 #2 | 「设置校正主路径整体失效 / 非法值原样落盘」 | `SettingsStore.cs:37 Load()` 与 `MigrationService.cs:170` 都做全字段 `Sanitized()`；UI 只产出合法常量 | **精确化**：真实缺陷是 `Update()` 只回写 5/12 字段，且 `SettingsViewModel` 绕过校正入口。仍修，但按真实根因修 |
| 1.1 #1 | 「JSON 显式 null 启动即崩且不走隔离」 | `JsonFileStore.cs:134` 对**整文档** null 有 `QuarantineAndRecover`；settings 域被 `Sanitized()` 兜住；`Icon.Clone()` 不在启动链 | **收窄**为「ui-state / catalog 域的**字段级** null」 |
| 1.1 #5「2~3 次」、#27「presenter 不消费」、#12「报 193」 | 标 [确信] | 依赖审计第六节自认未验证的 Avalonia / Win32 运行时语义 | **降级为 [推测]**：静态部分照修，次数/报错码不写进结论 |
| 1.1 #18 | 「导入只回写 **10** 个字段」 | `MigrationService.cs:173-181` 实为 **9** 条赋值 | **数字改 9**，修复动作不变 |
| 1.1 #13 | `lpCurrentDirectory` 是 `string` | `Win32.cs:157` 是 `string?` | **类型更正**，修复动作不变 |
| 1.1 #30 | `$ErrorActionPreference='Stop'` 导致默认调用抛错 | `measure.ps1:27` 是显式 `throw`；`Stop` 只在 `rollback-check.ps1:23` | **归因更正** |
| 2.1 B3 | CHANGELOG 声称已修 DPI，问题重现 | CHANGELOG 全文 Grep `DPI\|dpi\|缩放\|RenderScaling` **零命中**；真实出处是 `2026-11-23-panel-teleport-fix.md:96` 自认「本轮不修」 | **撤回该子项**；滚轮 / Esc / 设置持久化三项属「同名不同问题」，方向成立但措辞改 |
| 2.1 A 末行 | 文档默认密码 14/14 | `docs/specs/2026-11-24-randpass.md:77,88` 与 `PasswordOptions.cs:4-6` 已改 8/10；未同步的只有 `CHANGELOG.md:49` 与 spec §2 原表 | **精确化**，只改这两处 |
| 2.1 B5 | 提交数 19/20/38 与实测 60 不符 | 都是 11-19/20 的历史快照（`c741884`、`b355100`），非错误 | **不改文档**，在报告里说明口径 |
| 4.1 #3 | `ActiveTabName` 产品代码零引用 | `PanelViewModel.cs:390` 有 `Raise(nameof(ActiveTabName))` | **改为「无读取消费点」**，不删 |

### 2.2 复核后确认成立、本轮必修

`#3`（探针断言无效）、`#4`（dist 未忽略）、`#6`/`#7`（边缘拖动 + DIP）、`#8`（Esc/Enter）、`#9`（ShowInTaskbar）、`#10`/`#11`（主题监听 + Dispose 重入）、`#12` 主体链路、`#14`（热键释放）、`#15`（三处内存泄漏）、`#16`（协议白名单）、`#17`（校验门，仅记录）、`#19`（导出编码器）、`#20` ①②③、`#21`/`#22`/`#23`/`#24`/`#25`/`#27`/`#28`/`#29`/`#31`，以及 2.1 C 全部工程配置项、3.1 低效项中的可静态修复项。

**数字基线复核结果**：`tools/` 12 个探针、sln 10 个、CI 10 次 `dotnet run`、316 个 `Check(` 调用点、`artifact/` 45,694,608 B ≈ 43.6 MiB、`dist/` 62M、60 个提交、schema 1/1/3、默认密码 8/10 —— **全部重算一致，无需修正报告数字**。

---

## 步骤 3 · 方案设计与实施计划

### 3.1 设计原则

1. **就近修根因，不加外围补丁**。例如设置校正不改成「每个 setter 自己 clamp」，而是让唯一的持久化入口 `SettingsStore` 承担归一化。
2. **保持 AOT 兼容**：不引入反射式 JSON、不新增运行时代码生成；导出编码器用「显式 `TypeInfoResolver = JsonContext.Default` 的 `JsonSerializerOptions`」而非删掉源生成。
3. **单位统一**：窗口几何统一到**设备像素**（`Position`/`WorkArea` 都是设备像素），DIP 只在乘 `RenderScaling` 后参与比较。
4. **幂等与释放**：`Dispose` 一律加幂等标志；订阅配对称取消；COM 内存用 `try/finally`。
5. **探针先行**：每条行为修复都补一条能失败的断言，避免继续产出恒真 PASS。

### 3.2 模块划分与关键接口改动

| 模块 | 改动 |
|---|---|
| `Persistence` | `SettingsStore` 新增 `Commit()`；`Update()` 改为全字段 `Current = Current.Sanitized()`；`JsonFileStore.Load` 的异常捕获扩展；`UiStateStore` / `CatalogStore` 增加字段级 null 归一 |
| `ViewModels` | `SettingsViewModel.Commit()` 改调 `_store.Commit()`；`PanelViewModel.LaunchTile` 闸门由 `<` 改 `!=` |
| `Services` | `ThemeService`（watcher 置空 + Dispose 幂等）、`AppRuntime`（Dispose 幂等）、`LauncherService`（非 exe 走 ShellOpen、cwd 兜底 `null`）、`MigrationService`（导入补齐字段、导出用带 Encoder 的 options）、`ProtocolSafety`（白名单）、`PasswordGenerator`（双位分支）、`TabNavigation` / `PanelPlacement` / `FaviconService` / `HotkeyService` |
| `Interop` | `ShellLinkCom`（setter 释放 + GUID 内存复用）、`IconService`（超时 HICON）、`ShellIconHandler`（高字移位 / index 减一）、`Win32.WM_HOTKEY` |
| `Views` | 5 个 axaml 加 `ShowInTaskbar="False"`；Confirm / Prompt 加键盘处理；Panel / RandPass 的 ResizeTo 统一单位并保留对边；滚轮 `Delta.Y == 0` 早退 |
| `tools` | 4 处恒真断言改真实断言；`measure.ps1` / `rollback-check.ps1` 默认路径改仓库相对 + x64 |
| 工程 | `.gitignore` 加 `dist/`；`Galah2.sln` 补 2 个探针；CI 加 `cache: true`、`--no-build` 与 2 个探针步骤 |
| 文档 | README / CONTRIBUTING / CHANGELOG / PORTABLE / PLAN-2.0 按基线同步，CHANGELOG 增修复条目与已知问题 |

### 3.3 批次、依赖与工作量

| 批次 | 内容 | 依赖 | 估算 | Git 提交 |
|---|---|---|---|---|
| **A1** | P0：字段级 null、设置校正链路、`.gitignore`、SettingsProbe 断言 | 无 | 中 | `fix(persistence): …` |
| **A2** | P1 行为缺陷（约 20 个改动点） | A1（同改 `Persistence`，避免冲突） | 大 | `fix(runtime): …` |
| **A3** | 探针 / 工程配置 / 文档同步 | A2（探针断言依赖修复后的行为） | 中 | `chore(probes,docs): …` |
| 步骤 5 | 构建 + 12 探针自测 | A1~A3 | 中 | — |
| 步骤 6 | 独立 diff Review → 处理意见 | 自测 | 小 | 视意见补提交 |
| 步骤 7-8 | 分批提交、发布说明草稿、回滚方案 | Review | 小 | — |

**阶段 B / C / D（本轮不做，列入下一轮迭代）**：
- B（结构性）：新增 `tools/Directory.Build.props` 统一 12 个 csproj、发布双路径整合、边缘缩放 130 行去重、`DialogChrome` 统一接线、引入 xUnit 单元测试框架。
- C（清理）：`Win32.cs` 约 30 个零引用成员、5 个零引用 Command、3 个零引用画刷、归档过时文档 —— 需先确认探针依赖，风险高于收益，单独一轮。
- D（实机）：Avalonia `OnOpened` 累积注册、`Button.Click`/`DoubleTapped` 派发顺序、AOT COM stub、非 100% DPI / 多显示器几何 —— 静态无法判定，必须实机。

### 3.4 关键风险与回滚

| 风险 | 缓解 |
|---|---|
| COM / Win32 改动静态不可验证，AOT 下可能抛异常 | 只做「释放已分配内存 / 修正常量」这类局部改动，不动 vtable 调用顺序；自测跑 ComProbe / IconProbe |
| 几何单位换算改动可能改变窗口位置手感 | 统一到设备像素并保持 `Math.Clamp` 边界不变；几何类改动集中在一个 commit，便于单独 revert |
| 设置归一化改动影响所有偏好落盘 | `Load()` 已有 `Sanitized()`，改动只是让写入路径与加载路径一致；SettingsProbe 加回归断言 |
| 误提交 62 MB 二进制 | 精确 `git add <具体文件>`，提交前 `git status` 复核；`.gitignore` 在 A1 最先落地 |
| 用户数据损坏 | 不动 `GalahData/`；`JsonFileStore` 保留原子替换 + `.bak.1/.bak.2` 双备份机制 |

**回滚**：每批独立提交，`git revert <commit>` 即可；数据层另有 `README.md:17` 记载的自动隔离与 `CHANGELOG.md:104-115` 回滚节。

### 3.5 与 9 步流程的映射

| 步骤 | 本轮落地方式 | 完成标志 |
|---|---|---|
| 1 需求分析 | 本文件 §1 | 有明确需求和验收标准 ✅ |
| 2 方案设计 | §2 复核裁定 + §3.1/3.2 | 方案能指导开发 ✅ |
| 3 制定计划 | §3.3/3.4 | 知道做什么、如何验证 ✅ |
| 4 开发实现 | A1~A3，分 4 批提交 | ✅ 形成可验证改动（约 40 个文件） |
| 5 自测与自动化检查 | 本机无 .NET SDK；改为 `git diff --check` + 31 项 + 10 项静态回归检查 | ⚠️ 静态检查全绿，编译 / 探针待 CI |
| 6 Code Review | 独立代理逐 hunk 审查 `git diff`，2 条必修 + 6 条建议全部处理并复验 | ✅ 意见已处理（单人开发、无远端 PR，降级为本地 review） |
| 7 集成与验收 | 逐条对照 §1.3 验收标准 + 文档数字基线一致性 | ⚠️ 除「编译零错误」外全部达成，该项待 CI |
| 8 发布与交付 | CHANGELOG 修复节 + 操作说明 + 回滚方案（不真实发版，无 `gh` / tag 推送） | ✅ 交付物可用 |
| 9 验证与维护 | `FIX-REPORT-2026-11-25.md` + 阶段 B / C / D 遗留清单 | ✅ 问题进入下一轮迭代 |

---

*本计划只新增文档，未改动仓库中任何已有文件。执行结果见 `FIX-REPORT-2026-11-25.md`。*
