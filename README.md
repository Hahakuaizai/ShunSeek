# 瞬觅 ShunSeek

不依赖 Everything 的 Windows 全盘搜索工具：热键呼出一个 DeskBox 式的小悬浮窗
（Fluent/Mica、700×500），文件、文件夹、快捷方式、已安装软件、软件安装目录、
图片、视频、音频、文档等全部可搜，自动分类、可按分类筛选。
自建索引：直读 NTFS MFT 建索引（秒级）+ USN 日志增量更新，点开即用，不预热数据库。

**设计第一性原则：搜索速度是核心。** 引擎与 UI 完全解耦，引擎可在任何平台单独编译和跑基准。

## 结构

```
ShunSeek.sln
├─ src/ShunSeek.Core/            搜索引擎（net8.0，无 UI 依赖）
│  ├─ Search/                    查询侧
│  │  ├─ SearchIndex             一次搜索看到的快照 = 基础 + 增量 + 墓碑（不可变，可并发读）
│  │  ├─ SearchEngine            分片并行 Span.IndexOf 扫描 + 打分排序 + 原子换快照
│  │  ├─ PinyinInitials          拼音首字母（GB2312 边界字表法），"wzry" → 王者荣耀
│  │  ├─ CategoryClassifier      按扩展名分类（图片/视频/音频/文档/压缩包/代码…）
│  │  ├─ EntryCategory           分类枚举与中文名
│  │  ├─ RawEntry / IIndexSource 数据源与索引之间的契约
│  ├─ Index/                     索引侧
│  │  ├─ IndexStore              平坦内存索引：char 池 + 并行数组
│  │  ├─ IndexBuilder            多源汇总 + MFT 父链拼路径 + 目录引用号表
│  │  ├─ DeltaIndex              增量表：新增/改名进小表，删除记墓碑
│  │  ├─ IndexService            建索引 → 增量监听 → 必要时重建（UI 只订阅状态）
│  │  └─ UsageStore              打开频次（JSON 落盘）+ "最近使用"
│  └─ Sources/                   数据来源
│     ├─ MftEnumerator           FSCTL_ENUM_USN_DATA 直读 MFT
│     ├─ UsnWatcher              FSCTL_READ_USN_JOURNAL 增量监听
│     ├─ DirectoryUpdateWatcher  FileSystemWatcher 兜底增量
│     ├─ AppScanner              开始菜单/桌面快捷方式 + 注册表 Uninstall
│     └─ DirectoryWalker         非 NTFS/无权限时的兜底遍历
├─ src/ShunSeek.Bench/           速度基准：合成 200 万条目 → 建索引 → 跑查询集
├─ src/ShunSeek.App/             WPF 界面壳
│  ├─ App.xaml(.cs)              单实例、托盘、热键、崩溃日志
│  ├─ Views/                     MainWindow（图标网格 + 搜索栏）
│  ├─ ViewModels/                MainViewModel（结果、状态、异步图标）
│  ├─ Services/                  IconCache / HotkeyManager / TrayIconManager
│  └─ GlobalUsings.cs            WPF 与 WinForms 同名类型的别名
├─ scripts/                      启动脚本
│  ├─ run.ps1                    pwsh 7 脚本，主入口
│  └─ run.cmd                    双击用的小壳子（转给 run.ps1）
└─ docs/
   ├─ 开发流程.md                分 6 阶段的开发指导
   └─ ui-mockup.html             弹窗 UI 目标稿
```

## 跑起来

```powershell
pwsh -File .\run.ps1            # Debug 编译并启动（托盘常驻，Alt+Space 呼出）
pwsh -File .\run.ps1 -Show      # 启动后直接把搜索窗弹出来
pwsh -File .\run.ps1 -Release   # Release
pwsh -File .\run.ps1 -Bench     # 只跑速度基准
```

双击 `scripts
un.cmd` 等价于 `scripts
un.ps1`。脚本会自动找 dotnet（PATH → `%ProgramFiles%\dotnet` →
`%USERPROFILE%\.dotnet`），并给子进程补 `DOTNET_ROOT`——dotnet-install 装在用户目录的 SDK
没登记进系统环境变量，不补这个，双击 exe 会报 "You must install .NET"。

在 VS 2022 里直接 F5 也行（.NET 桌面开发工作负载 + 自动还原 WPF-UI）。

### 界面

- 一屏就是"搜索框 + 图标网格"：图标全部取系统原版（文件夹就是 Windows 的文件夹，
  程序就是它自己的图标），取不到时只放一块淡占位，不画色块不写字。
- 第一屏只放最近打开过的 8 条；什么都没有就一句提示，不把整盘东西铺上来。
- 类型筛选收进漏斗按钮里的**多级菜单**：应用程序（软件/快捷方式/安装目录）、
  文件（文档/图片/视频/音频/压缩包/代码/程序）、文件夹、其他。
- 键盘：↑↓←→ 走格子、Enter 打开、Ctrl+Enter 定位、Ctrl+C 复制路径、Esc 收起。

### 两种索引档位

| | 全盘（管理员） | 便携（普通权限，默认） |
|---|---|---|
| 数据源 | MFT 直读全部 NTFS 卷 | 用户目录 + 开始菜单 + 桌面 |
| 增量 | USN 日志（内核级，不丢事件） | FileSystemWatcher |
| 首次建索引 | 秒级 | 十几秒~两分钟（取决于用户目录大小） |
| 怎么进 | 托盘 → 以管理员身份重启 | 直接运行即是 |

两种档位都会额外扫「软件/快捷方式/安装目录」（读注册表 Uninstall + 开始菜单，不需要管理员）。
状态栏会写明当前是哪一档、索引了多少条、增量多少条、最后一次更新是什么时候。

## 索引与增量更新（初版的核心机制）

搜索不能为了"新建了一个文件"去重建百万条索引，所以拆成三层：

1. **基础索引（`IndexStore`）** —— 建好即冻结的连续数组，搜索线程随便并发读。
2. **增量表（`DeltaIndex`）** —— 启动后新增/改名的条目进小表（NTFS 用文件引用号去重，
   兜底模式用路径去重）。每 300ms 成批应用一次，应用只是"重冻增量表 + 换快照"，
   微秒级，所以文件刚建好就能搜到。
3. **墓碑** —— 删除不从数组里挖洞，而是记引用号/路径/目录前缀，扫描命中后一查就跳过。
   目录删除只记前缀（子树整体失效）；**目录改名**必须整体重建（子条目路径全变了，
   USN 不会逐个通知），这是唯一代价大一点的情况。

换快照用 `SearchEngine.Swap(SearchIndex)`，属于原子换引用：正在跑的旧查询用旧快照跑完，
新查询立刻看到新数据，全程没有锁。

**自动重建的触发条件**：墓碑超过 2 万条、失效目录前缀超过 32 个、USN 日志被卷满覆盖
（`ERROR_JOURNAL_ENTRY_DELETED`）、FileSystemWatcher 缓冲区溢出、或用户在托盘点"重建索引"。
重建在后台跑，期间搜索照常可用（只是结果暂时是旧的）。

USN 起点是在建索引**之前**记下的（`UsnWatcher.TryQueryNextUsn`），建完从那个位置开始读，
把建索引那几秒里发生的变更补回来——重放变更本身是幂等的，多读几条不会出错。

## 跑基准（验证速度，任何平台）

```bash
dotnet run --project src/ShunSeek.Bench -c Release
```

### 本仓库实测

**基准（2,000,002 条合成条目，Windows 11 / 16 线程 / Release）**

| 指标 | 结果 |
|---|---|
| 建索引 | **887 ms** |
| 索引内存粗估 | 411 MB（⚠ 仍超阶段 1 的 200MB 线，优化路线见下） |
| 查询「报告」 | 首次 27 ms（含 JIT），平均 **10.7 ms** |
| 查询「合同」 | 平均 **10.2 ms** |
| 查询「report」 | 平均 **20.7 ms** |
| 查询「wzry」（拼音首字母） | 平均 **17.1 ms**，正确命中王者荣耀 |
| 查询「mp4」+ 视频分类 | 平均 **5.6 ms** |
| 查询「2024」（大命中面） | 平均 **11.7 ms** |

**真机索引（便携模式，本机用户目录）**

| 指标 | 结果 |
|---|---|
| 建索引 | 109 s / **777,441 条** |
| 查询 | 1.4 ~ 13.5 ms |
| 新建文件 → 可搜到 | **< 1.2 s** |
| 改名 / 删除 → 结果更新 | **< 1.2 s** |
| 新建目录 + 目录内文件 → 可搜到 | **< 1.5 s** |

已知短板与对策（都写进了 docs/开发流程.md 阶段 1）：
内存大头是每条目两个 .NET string（全路径 + 显示名）。对策：① 全路径拆「目录串驻留表 + 文件名」；
② 显示名也拼进 char 池。做完这两步目标 < 200MB。

## 完成度清单（诚实版）

**真实现（已编译/已跑通验证）**

- [x] IndexStore 平坦索引 + IndexBuilder（含 MFT 父链拼路径、目录引用号表）
- [x] SearchIndex：基础 + 增量 + 墓碑的不可变快照；SearchEngine 原子换快照
- [x] SearchEngine 并行扫描、打分排序、分类过滤、查询取消
- [x] **增量更新**：DeltaIndex + UsnWatcher（阻塞读、日志覆盖异常）+ DirectoryUpdateWatcher
- [x] **自动重建**：墓碑阈值 / 日志覆盖 / 监听溢出 / 目录改名 / 手动触发（防抖合并）
- [x] IndexService：两种档位自动选择、建索引进度、状态广播、开机自启
- [x] 拼音首字母（GB2312 边界字表法，Bench 实测命中）
- [x] 扩展名分类器（11 个类别）
- [x] UsageStore 频次存取、排序加分、"最近使用"
- [x] AppScanner：快捷方式 + 注册表 Uninstall（含 DisplayIcon 取启动路径、64/32 位去重）
- [x] DirectoryWalker 兜底遍历（防环/容错/排除缓存目录）
- [x] WPF 界面壳：热键（含句柄预创建）、托盘菜单、分类胶囊、键盘流、Esc/失焦收起、状态栏
- [x] 崩溃日志 + 诊断日志（%LocalAppData%\ShunSeek\logs）
- [x] Bench 与真机全流程实测（数字见上表）

**骨架（结构与签名齐全，落地细节留了 TODO 注释）**

- [ ] UWP 应用枚举（shell:AppsFolder）、.lnk 目标解析（接入点已在 AppScanner 注释）
- [ ] 内存优化到 200MB 以内（目录串驻留 + 显示名入 char 池，阶段 1）
- [ ] 输入法 composing 保护（阶段 2）、真实文件图标异步缓存（阶段 4）
- [ ] 命名管道 + 常驻服务（方案 A 的全盘能力，免提权）
- [ ] 正式图标、单文件发布、代码签名（阶段 5）

## 开发路线

照着 [docs/开发流程.md](docs/开发流程.md) 一阶段一阶段走：
阶段 0 环境 → 1 MFT 引擎与权限两档方案 → 2 搜索与排序（含输入法保护）→
3 数据源补全与增量 → 4 UI 打磨 → 5 打包发布。
