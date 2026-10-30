# WinMemoryCleaner 分析与优化报告

> 项目：WinMemoryCleaner（WPF / .NET Framework 4.0 / C# 4）
> 日期：2026-10-30
> 范围：缺陷修复、架构调整、性能优化、工程卫生

---

## 一、总览

| 指标 | 优化前 | 优化后 | 变化 |
|---|---|---|---|
| 单元测试数量 | 353 | **367** | +14（回归测试） |
| 测试通过率 | 353/353 | **367/367** | 0 失败 |
| 发布版 EXE 体积 | 501,760 B | **423,424 B** | **−78,336 B（−15.6%）** |
| EXE 内测试类型 | 77 | **0** | 全部移除 |
| EXE 对 NUnit 的依赖 | 有 | **无** | 已解除 |
| 编译警告 | 有（CS1691） | **0** | 全部清零 |
| 空闲 CPU 占用 | 忙等自旋 | **0.01%** | 修复 |
| 语言列表耗时 | 5.6 ms/次 | **≈0**（缓存） | 修复 |
| 设置写入耗时 | 21.8 ms/次 | **合并为 1 次** | 修复 |

**经执行验证的高危缺陷：6 个**，其中 2 个会造成错误的内存显示（10000%），1 个使"关闭自动更新"完全失效。

---

## 二、修复的缺陷

### 1. 内存占用百分比计算错误（严重）

**现象**：某些单位组合下 `Used.Percentage` 输出 `10000%`，`Free.Percentage` 输出 `-9900%`。

**根因**：`MemoryStats` 用两个**已被缩放到不同单位**的值做除法。

```csharp
// 优化前
used = Used.Value > 0 && Total.Value > 0
     ? (int)(Used.Value * 100 / Total.Value)   // Used=100(GB) / Total=1(TB) = 10000
     : 0;
```

**修复**：改用原始字节做整数运算，并对越界值做钳制。

```csharp
// 优化后
var usedBytes = total >= free ? total - free : free - total;
if (used == null)
    used = total > 0 ? (int)(usedBytes * 100L / total) : 0;
used = Math.Max(0, Math.Min(100, (int)used));
```

**验证**（真实数值代入）：

| 场景 | 修复前 | 修复后 |
|---|---|---|
| 1 TB 总量 / 已用 100 GB | 10000% | **9%** |
| 16 GB 页面文件 / 已用 300 MB | 1875% | **1%** |
| 显式传入 memoryLoad=75 | 75% | 75% |

> 该缺陷在物理内存上不易暴露（Total/Used 往往落在同一单位区间），**虚拟内存（页面文件）才是必现场景**。

---

### 2. 自动更新逻辑取反（严重）

**现象**：用户在界面上**关闭**"自动更新"后，程序仍然会自动下载并重启升级。

**根因**：条件写反了。

```csharp
// 优化前：AutoUpdate=false 时条件恒为 false → 不 return → 继续更新
if (Settings.AutoUpdate && elapsed < 24h) return;

// 优化后：关闭即不检查；开启时按间隔节流
if (!Settings.AutoUpdate) return;
if (elapsed < 24h) return;
```

---

### 3. 优化进度条永远到不了 100%

**现象**：只勾选"备用列表"和"低优先级备用列表"时，进度条卡在 **66%**。

**根因**：进度总数用 `BitArray` 统计枚举位，但优化器把两种备用列表合并为**一次**调度，因此实际事件数 ≠ 位数。

| 内存区域组合 | 实际进度事件 | 原计算总数 | 最终百分比 |
|---|---|---|---|
| 备用列表 + 低优先级备用列表 | 2 | 3 | **66%** |
| 默认配置（7 区域） | 8 | 8 | 100% |

**修复**：把"步数计算"收敛到 `ComputerService.GetOptimizationStepCount()`，与实际触发进度事件的代码放在一起，保证两者永远同步。

```csharp
// 备用列表无论优先级都是 1 步
if ((areas & (Areas.StandbyList | Areas.StandbyListLowPriority)) != 0)
    steps++;
```

---

### 4. 繁忙时 CPU 占用 100%（忙等自旋）

**现象**：优化进行中或界面繁忙时，应用把 CPU 打满。

**根因**：`continue` 跳过了本次循环末尾的等待，导致无限自旋。

```csharp
// 优化前
if (IsBusy) continue;                       // ← 直接回到循环头
if (token.WaitHandle.WaitOne(5000)) break;  // ← 永远执行不到

// 优化后：先等待，再判断
if (token.WaitHandle.WaitOne(5000)) break;
if (IsBusy) continue;
```

**验证**：`MonitorApp`、`MonitorComputer` 两个后台循环均已修正；实测运行 8 秒 CPU 占用 **0.01%**。

---

### 5. 内存单位换算的浮点误差

**现象**：1 TB 被显示为 `1023.99999999907 GB`。

**根因**：`Math.Log(value, 1024)` 存在精度误差。

```csharp
// 优化前
var mag = (int)Math.Log(obj, 1024);

// 优化后：整数迭代，无误差
var mag = 0; var bytes = obj;
while (mag < (int)Unit.YB - 1 && bytes >= 1024) { bytes /= 1024; mag++; }
```

**验证**：`1024^1..1024^6` 全部精确返回 `1 KB / 1 MB / 1 GB / 1 TB / 1 PB / 1 EB`。

---

### 6. 进程枚举的竞态崩溃

**现象**：进程在"枚举 → 读取进程名"的间隙退出时，`Process.ProcessName` 抛 `InvalidOperationException`，可能导致优化中断或进程下拉列表异常。

**根因**：LINQ 里直接取 `process.ProcessName`，无异常保护。

**修复**：改为显式循环，对名称读取做防御，失效进程直接跳过；同时修正了 `Process` 对象未释放的问题。

```csharp
foreach (var process in Process.GetProcesses())
    using (process)
        try { processName = process.ProcessName; }
        catch (InvalidOperationException) { continue; }  // 进程已退出
```

修复位置：`ComputerService.OptimizeProcessWorkingSets()`、`MainViewModel.Processes`。

---

## 三、架构优化：测试工程与发布产物分离

### 问题

原方案是**把测试代码编译进生产 EXE**，再用 `nunit-console` 跑 `WinMemoryCleaner.exe`：

- 发布产物含 **77 个测试类型**
- 发布产物**引用了 `nunit.framework.dll`**
- 产物体积多出约 78 KB

### 方案

新建独立测试工程 `src/WinMemoryCleaner.Tests/`：

```
src/WinMemoryCleaner.Tests/
├── WinMemoryCleaner.Tests.csproj   (Library, 引用主工程)
├── CommandTests.cs / ConverterTests.cs / CoreTests.cs
├── IntegrationTests.cs / ModelTests.cs / ServiceTests.cs
├── ViewModelTests.cs / Mocker.cs / TestCleanup.cs
```

关键处理：

1. **强名称**：测试工程用同一个 `WinMemoryCleaner.snk` 签名；
2. **内部可见性**：主工程加 `[assembly: InternalsVisibleTo("WinMemoryCleaner.Tests, PublicKey=...")]`，测试可访问 `internal` 成员（如 `ComputerService.GetNtStatusMessage`）；
3. **解决方案**：`.sln` 注册新工程，`msbuild` 一次构建两个产物；
4. **CI**：`ci.yml`、`release.yml` 的测试目标由 `WinMemoryCleaner.exe` 改为 `WinMemoryCleaner.Tests.dll`，并增加产物存在性检查。

### 结果

| 项 | 优化前 | 优化后 |
|---|---|---|
| 生产 EXE 中测试类型 | 77 | **0** |
| 生产 EXE 引用 NUnit | 是 | **否** |
| 生产 EXE 体积 | 501,760 B | **423,424 B** |
| 测试程序集 | 无（混在 EXE） | 86,016 B（独立） |

---

## 四、性能优化

### 1. 语言列表缓存

`Localizer.Languages` 每次访问都会 `GetCultures(AllCultures)`（915 个文化）并扫描程序集资源，实测 **5.6 ms/次**，而语言下拉框每次展开都会触发。

优化：首次计算后缓存结果（语言列表在运行期不会变化）。

```csharp
private static List<Language> _languages;   // 缓存
if (_languages != null) return _languages;
```

### 2. 设置写入去抖（Debounce）

`Settings.Save()` 会写入约 30 个注册表值，实测 **21.8 ms/次**；而 `FontSize`、`AutoOptimizationInterval`、`AutoOptimizationMemoryUsage` 三个属性直接绑定 Slider，**拖动一次会触发几十次写入**。

优化：新增 `Settings.SaveDebounced()`（500 ms 合并写入）与 `Settings.Flush()`（退出时强制落盘），三个滑杆属性改用去抖保存。

```csharp
50 次 SaveDebounced() → 立即返回，合并为 1 次写入
50 次 Save()          → 52 ms（同步阻塞 UI）
Flush()               → 1 ms
```

---

## 五、工程与构建卫生

| 项 | 处理 |
|---|---|
| `NoWarn` 使用了无效编号（`CS3001`/`CS3003`） | 移除，消除 CS1691 警告 |
| `<CodeAnalysisRuleSet>AllRules.ruleset` 指向不存在的文件 | 移除该引用（分析器走默认规则集） |
| `.gitignore` 未忽略 `TestResults.xml`（CI 实际产物名） | 已补上 |
| `Helper.GetVersion()` 在测试宿主下取到错误版本 | 改为取声明程序集版本，而非入口程序集 |
| 构建警告 | **0** |

---

## 六、验证结果

### 构建

```
msbuild src\WinMemoryCleaner.sln /t:Rebuild /p:Configuration=Release /p:IsCI=true
→ 0 error, 0 warning
→ WinMemoryCleaner.exe / WinMemoryCleaner.Tests.dll
```

### 测试

```
Tests run: 367, Errors: 0, Failures: 0, Inconclusive: 0
```

新增 14 个回归测试覆盖本次修复：

- `ToMemoryUnit_WithExact{Terabyte,Petabyte,Exabyte}_ReturnsExactlyOne...`
- `UsedPercentage_WithDifferentUnitsForUsedAndTotal_IsCalculatedInBytes`
- `UsedPercentage_WithSmallUsedValueAndLargeTotal_IsCalculatedInBytes`
- `UsedPercentage_WithOutOfRangeSuppliedValue_IsClamped`
- `GetOptimizationStepCount_*`（3 个）
- `SaveDebounced_DoesNotThrow` / `Flush_DoesNotThrow`

### 运行时冒烟测试

在**仅含单个 EXE** 的隔离目录（模拟真实发布资产）中启动：

```
进程存活：是
主窗口句柄：134756
8 秒 CPU 占用：0.01%
```

命令行静默优化（`/WorkingSet /StandbyList`）：

```json
{"level":"Information","message":"内存已优化",
 "data":{"reason":"手动","duration":"3.3 秒","released":"3.1 GB",
         "memoryAreas":[{"name":"备用列表","duration":"1.6 秒"},
                        {"name":"工作集","duration":"1.7 秒"}]}}
```

→ 退出码 0，实际释放 **3.1 GB**，日志中的 `released` 字段正确。

---

## 七、变更清单

**修改（生产代码）**

| 文件 | 内容 |
|---|---|
| `src/Model/Memory/MemoryStats.cs` | 百分比改为按字节计算 + 钳制 |
| `src/Core/ExtensionMethods.cs` | `ToMemoryUnit` 改整数进制换算 |
| `src/Core/Updater.cs` | 修正自动更新逻辑取反 |
| `src/Core/Localizer.cs` | 语言列表缓存 |
| `src/Core/Settings.cs` | `SaveDebounced()` / `Flush()` |
| `src/Core/Helper.cs` | `GetVersion()` 取声明程序集 |
| `src/Service/ComputerService.cs` | 新增步数计算、进程枚举防竞态 |
| `src/ViewModel/MainViewModel.cs` | 进度总数、忙等自旋、进程枚举、滑杆去抖保存 |
| `src/App.xaml.cs` | 退出时 `Settings.Flush()` |
| `src/Properties/AssemblyInfo.cs` | `InternalsVisibleTo` |
| `src/WinMemoryCleaner.csproj` | 移除测试文件、NUnit 引用、无效配置 |
| `src/WinMemoryCleaner.sln` | 注册测试工程 |

**新增**

- `src/WinMemoryCleaner.Tests/`（9 个测试文件 + 1 个工程文件）

**CI/CD**

- `.github/workflows/ci.yml`：测试目标改为 `WinMemoryCleaner.Tests.dll`
- `.github/workflows/release.yml`：同上 + 产物存在性校验

**文档**

- `CHANGELOG.md`：补充 3.0.9 的修复条目

> 说明：工作区中原有的未提交改动（日志系统重构、权限降级回退等）已完整保留，未做回退。

---

## 八、复现验证步骤

```powershell
# 1. 构建
& "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe" `
    src\WinMemoryCleaner.sln /t:Rebuild `
    /p:Configuration=Release /p:Platform="Any CPU" /p:IsCI=true

# 2. 跑测试（注意目标已变为 Tests.dll）
& src\packages\NUnit.Runners.2.6.4\tools\nunit-console.exe `
    src\WinMemoryCleaner.Tests\bin\Release\WinMemoryCleaner.Tests.dll

# 3. 检查发布产物是否干净
$asm = [Reflection.Assembly]::LoadFrom("$PWD\src\bin\Release\WinMemoryCleaner.exe")
($asm.GetTypes() | ? { $_.Namespace -like '*Test*' }).Count      # 期望 0
[bool]($asm.GetReferencedAssemblies() | ? { $_.Name -match 'nunit' })  # 期望 False
```

---

## 九、后续建议（未实施）

| 优先级 | 建议 | 说明 |
|---|---|---|
| 高 | 升级目标框架 | .NET Framework 4.0 已停止支持，无法使用 SDK 风格工程、C# 6+ 与新分析器 |
| 中 | 迁移 `packages.config` → `PackageReference` | 当前依赖被 vendor 到 `src/packages/`，且 `packages.lock.json` 未被工程引用 |
| 中 | 拆分 `MainViewModel` | 单文件约 2000 行，混有设置、命令、监控、优化编排四类职责 |
| 低 | 替换自研 DI 容器 | 当前通过反射 + 构造函数单参选取，缺少生命周期校验与循环依赖检测 |
| 低 | WinForms 托盘图标改 WPF 原生 | 目前 WPF + WinForms 双 UI 栈并存，增加依赖面 |
