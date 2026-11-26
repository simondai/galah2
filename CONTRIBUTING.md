# 开发流程

本项目的每一步开发都按这个九步流程走。**不允许跳步**——尤其是 1→2→3 必须在动手写代码之前完成。

| # | 步骤 | 做什么 | 完成标志 |
|---|---|---|---|
| 1 | 需求分析 | 明确解决什么问题、使用者是谁、范围及边界条件 | 有明确的需求和验收标准 |
| 2 | 方案设计 | 确定实现方式、模块划分、接口、数据结构及关键风险 | 方案能够指导开发 |
| 3 | 制定计划 | 拆分任务、明确依赖、估算工作量，安排测试和发布 | 知道做什么、谁来做、如何验证 |
| 4 | 开发实现 | 编写代码、补充测试，同步更新相关文档，分批提交 Git | 形成可验证的改动 |
| 5 | 自测与自动化检查 | 检查功能、异常情况，运行测试、构建及代码检查 | 满足基本质量要求 |
| 6 | Code Review | 提交 PR/MR，检查正确性、设计和可维护性 | 审查意见已处理，获得批准 |
| 7 | 集成与验收 | 合并改动，集成测试、回归测试，按需求验收 | 满足发布条件 |
| 8 | 发布与交付 | 交付版本，提供发布说明、操作说明及必要的回滚方案 | 目标用户可以使用 |
| 9 | 验证与维护 | 检查发布效果、监控异常、处理反馈和缺陷 | 问题进入下一轮迭代 |

## 每一步在本仓库的具体落地

### 1. 需求分析
写入 issue 或 `docs/specs/<功能>.md`，必须包含：解决什么问题、谁用、范围内/范围外、
以及**可验证的验收标准**（能被测试断言的那种，不是"体验良好"）。

### 2. 方案设计
同一份规格文件里追加：模块划分、新增/改动的接口、数据结构、关键风险与应对。
凡是"这个 API 到底什么行为"这类问题，方案阶段就要用实验确定，不要边写边试。

### 3. 制定计划
拆成可独立验证的任务，每个任务写明如何验证（哪条探针/哪个测试）。

### 4. 开发实现
代码 + 测试 + 文档**同步**提交，不是事后补。提交信息说清楚"为什么"而不只是"改了什么"。

### 5. 自测与自动化检查
必须真实运行，报实测输出：

```bash
dotnet build Galah2.sln -c Release
dotnet run --project tools/ComProbe         -c Release   # 11
dotnet run --project tools/IconProbe        -c Release   # 8 调用点 / 12 行
dotnet run --project tools/SettingsProbe    -c Release   # 27 调用点 / 28 行
dotnet run --project tools/PanelProbe       -c Release   # 126
dotnet run --project tools/DialogProbe      -c Release   # 60
dotnet run --project tools/GeometryProbe    -c Release   # 14
dotnet run --project tools/MaterialProbe    -c Release   # 5 调用点 / 4 行
dotnet run --project tools/InteractionProbe -c Release   # 19
dotnet run --project tools/DragArmProbe     -c Release   # 10
dotnet run --project tools/PasswordProbe    -c Release   # 22
dotnet run --project tools/UiAcceptance     -c Release -- src/Galah2/bin/x64/Release/net8.0/Galah2.exe
dotnet run --project tools/PortableCheck    -c Release -- artifact
```

涉及 UI 布局的改动，要用视觉树的 `Bounds` 实测，不要靠截图肉眼判读
（Fluent 控件的浅边框在截图里基本定位不到，容易得出错误结论）。

### 6. Code Review
⚠ **当前是单人开发，没有独立的第二人 review。**
这一环节的实际做法是：提交前自查一遍 diff，并在提交信息或讨论里如实说明
"本次无独立 review"。**不要假装这一步走过了。**

有协作者之后，改成真正的 PR 流程：分支提交 PR → 第二人审查 → 意见处理完毕 → 合并。

### 7. 集成与验收
全量探针绿 + 按第 1 步的验收标准逐条比对。任何一条不满足就不是"完成"。

### 8. 发布与交付
```powershell
powershell -ExecutionPolicy Bypass -File tools/publish-aot.ps1
```
`artifact/` 整个目录就是交付物。发布说明要写：本次改了什么、怎么验、怎么回滚。

回滚方式：1.x 在独立仓库 `simondai/galah` 的 `main` 分支完整可用；2.0 出问题时用户可退回上一版 `artifact/`（整个目录替换）。

### 9. 验证与维护
发布后记录实际表现（内存、体积、启动）。发现的问题进入下一轮第 1 步，而不是就地打补丁。
