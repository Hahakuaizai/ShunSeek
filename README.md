# ShunSeek

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
│  │  ├─ PinyinFull              全拼（TinyPinyin），"weixin" → 微信
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
│  ├─ Views/                     MainWindow（液态玻璃 + 图标网格）
│  ├─ Assets/app.ico             应用图标（scripts/make-icon.ps1 生成的手绘线稿）
│  ├─ Controls/                  LiquidPanel（椭圆角/连续曲率的玻璃面板）
│  ├─ ViewModels/                MainViewModel（结果、首页固定项、状态、异步图标）
│  ├─ Services/                  IconCache / PinnedStore / HotkeyManager / TrayIconManager
│  └─ GlobalUsings.cs            WPF 与 WinForms 同名类型的别名
├─ scripts/                      启动脚本
│  └─ run.bat                    双击即用（纯批处理，不依赖 PowerShell / bash）
└─ docs/
   ├─ 开发流程.md                分 6 阶段的开发指导
   └─ ui-mockup.html             弹窗 UI 目标稿
```

## 跑起来

```bat
rem 双击 scripts/run.bat         编译 + 启动，并直接把搜索窗弹出来
scripts/run.bat -Release        用 Release 配置
scripts/run.bat -NoBuild        不编译，直接启动上次的产物
scripts/run.bat -BuildOnly      只编译
scripts/run.bat -Bench          只跑速度基准
scripts/run.bat -NoShow         启动但不弹窗（只驻留托盘）
```

脚本自己找 dotnet（PATH → `%ProgramFiles%\dotnet` → `%USERPROFILE%\.dotnet`）并补上
`DOTNET_ROOT`——dotnet-install 装在用户目录的 SDK 没登记进系统环境变量，不补这个，
双击 exe 会报 "You must install .NET"。启动前会先结束已在跑的实例（程序是单实例的）。

在 VS 2022 里直接 F5 也行（.NET 桌面开发工作负载 + 自动还原 WPF-UI）。

### 界面

两套主题：**墨本深色**（近黑渐变墨底、冷灰层叠表面、朱红点睛）和**冷调纸白**（纯白到
灰绿的层叠纸面，绝不米黄）。顶栏一枚日/月按钮切换，选择落到 `%LocalAppData%\ShunSeek\theme.txt`，
下次启动沿用；切换时资源整批换新 + 窗口一次极短淡化，不闪。

所有圆角都由 `Controls/LiquidPanel.cs` 自绘**超椭圆**（|x|ⁿ+|y|ⁿ=rⁿ，n≈4，默认 12px）并裁切子元素：
曲率从直边平滑过渡到角点，不是正圆弧那种"方中带硬"。窗口留 12px 透明边给投影，
最大化时自动收掉投影、抹平圆角。

- 图标全部取系统原版（文件夹就是 Windows 的文件夹，程序就是它自己的图标），
  取不到时只放一块淡占位，不画色块不写字。
- 拼音三条通道：名字直接匹配 → 首字母（`wx` → 微信）→ 全拼（`weixin` → 微信）。
  全拼只在前面凑不够结果时才扫（串长两三倍，不想让常见输入为它买单）。
- 排序按"命中档位 → 条目类型 → 路径干净程度"三层叠：
  前缀命中 > 词首命中 > 子串；应用/快捷方式 +38；短名 +8；整个名字等于查询 +30；
  路径越深扣得越多（>3 层每层 −3，最多 −15），AppData 里再 −6，回收站 −10；
  哈希样式的名字（`wx1c36464bbee2507a`）−30。
- **首页 = 已固定 + 最近使用**：标题行显示「已固定 N · 最近使用 M」，固定项带别针标记。
- 结果上右键：打开 / 打开所在位置 / 复制路径 / **添加到首页**（或从首页移除）。
  "从首页移除"会同时清掉它的最近使用记录——不然取消固定后它又会以"最近使用"的身份
  冒出来，看起来像只挪了个位置、没真移除。
- 右侧标签先回答"**它到底是什么**"：`Word 文档`、`Python 源码`、`PNG 图片`、
  `ZIP 压缩包`、`文件夹`，认不出来的才写 `XYZ 文件`；左边一个小圆点表示重要级
  （朱红=应用/文档/文件夹，灰=普通文件，浅灰=日志/缓存）。
- **不重要的一律折叠**：日志、缓存、临时文件超过 4 条就叠成一行「其他 N 项 · 点击展开」，
  点它或按 Enter 才铺开——不然搜 `log` 会被几百条 `LOG` 刷屏。
- 托盘右键菜单是自绘面板（不是系统灰白菜单），跟当前主题同色；**点菜单外任意空白、
  按 Esc 都会收起**——WPF 自带的 StaysOpen 在透明独立 Popup 上收不到外部点击，
  这里补了一个低级鼠标/键盘钩子（只判断点在不在菜单矩形内，不吞点击）。
- 右键分两种：**点在条目上**给打开/定位/复制路径/添加到首页；**点在空白处**给
  「添加文件夹到首页…」「添加文件到首页…」和索引维护（刷新/重建）。
- 类型筛选是漏斗按钮下的**扁平芯片面板**（3 列 13 项）：全部、应用、快捷方式、安装目录、
  文件夹、文档、图片、视频、音频、压缩包、代码、程序、其他。单选，点一项立即生效并收起；
  再点漏斗可改选，面板底部显示"当前：xx类型"，有筛选时漏斗右上角带一个朱点。
- 键盘：↑↓←→ 走格子、Enter 打开、Ctrl+Enter 定位、Ctrl+C 复制路径、Esc 收起
  （筛选面板开着时 Esc 先关面板；有查询时先清空，再按一次才收起窗口）。
- **外观**：墨本深色与冷调纸白双主题，都有渐变底 + 多层表面色 + 发丝线 + 投影，
  不是单色平涂；宋体书眉与条目名，朱红只用在焦点、选中与更新标记上。
  右键菜单、滚动条、提示框、托盘菜单、更新弹窗都跟同一套主题。
- **交互动画**：窗口呼出淡入上浮、收起淡出下沉；磁贴悬停浮层+3% 放大、选中浮出朱边；
  结果行悬停/选中浮层与朱线淡入；顶栏与菜单项悬停淡入；筛选面板缩放展开；
  主题切换整窗柔化过渡。全部 100–260ms，不拖沓。
- **内存**：索引热数据只存一份（纯英文名的"首字母/全拼"与名字相同就不再重复存，
  显示名由全路径现算），447 万条目量级实测常驻约 180MB 托管堆。
- **首页**：固定的项目不限数量；最近使用最多 24 条。搜索结果右键可"添加到首页"，
  首页空白处右键也能直接挑文件夹/文件。
- **启动即用**：索引会存一份磁盘缓存（`%LocalAppData%\ShunSeek\index.cache`），
  下次启动直接读回来，窗口约 1 秒出现、索引立刻可搜；缓存超过一天才在后台重扫。
- `--shot <png>` 可离屏渲染成图片后退出（改样式时验证用，不弹窗）；
  `--update-preview [png]` 单独预览更新说明窗；`--dev-update` 直接跑一遍热更新。
- 双击图标直接弹窗（开机自启带 `--tray`，安静待托盘）；再双击一次会把已运行的窗口叫到前面。
- 窗口是正常窗口：**不置顶**，可以被其它软件盖住（只有最小化才会让开位置）；
  顶栏空白处可拖动（双击最大化/还原），四边可拉伸，右上角有最小化/最大化/关闭。
  不再"失焦自动隐藏"——那样没法拖着改大小。

### 两种索引档位

| | 全盘（管理员） | 便携（普通权限，默认） |
|---|---|---|
| 数据源 | MFT 直读全部 NTFS 卷 | 用户目录 + 开始菜单 + 桌面 |
| 增量 | USN 日志（内核级，不丢事件） | FileSystemWatcher |
| 首次建索引 | 秒级 | 十几秒~两分钟（取决于用户目录大小） |
| 怎么进 | 托盘 → 以管理员身份重启 | 直接运行即是 |

两种档位都会额外扫「软件/快捷方式/安装目录」（读注册表 Uninstall + 开始菜单，不需要管理员）。
状态栏会写明当前是哪一档、索引了多少条、增量多少条、最后一次更新是什么时候。

## 打包

发布用**文件夹**方式（不是单文件 exe）：160MB 的单文件每次启动都要把原生库解压到临时目录，
冷启动要好几秒；文件夹 + ReadyToRun 冷启动约 1 秒。

```powershell
dotnet publish src/ShunSeek.App -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=false -p:PublishReadyToRun=true -o publish/win-x64-app
```

把 `publish/win-x64-app/*` 整个复制到 `E:\Apps\ShunSeek\`，快捷方式仍指向其中的 `ShunSeek.exe`。
与安装

```bat
rem 自包含单文件（目标机器不用装 .NET），产物在 publish\win-x64\ShunSeek.exe
dotnet publish src\ShunSeek.App -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish\win-x64
```

已经装好的一份在 `E:\Apps\ShunSeek\ShunSeek.exe`，桌面快捷方式「瞬觅 ShunSeek」指向它。

- 图标是手绘线稿（透明底 + 墨线放大镜），由 `scripts/make-icon.ps1` 生成，
  256/96/64/48/32/16 六个尺寸各画一遍打进一个 ICO，小尺寸不叠笔画所以不糊。
- 换图标后 Windows 有图标缓存，必要时刷新：`ie4uinit.exe -show`，或删掉快捷方式重建成。
- 单文件自包含版约 160MB（WPF 运行时全打包进去），首次启动会解压到临时目录，稍慢几秒。

## 开发目录一键更新（改了代码不用手动拷）

设置菜单里有一项「检查开发目录改动并更新（重启生效）」，也可以点页脚右下角的
「有新改动」小红标（启动 2.5 秒后自动比对，开发目录比安装目录新才亮）。

流程：

1. 比对 `src/` 下 `.cs/.xaml/.csproj` 等源码的修改时间与安装目录 `ShunSeek.dll` 的时间，
   没有更新的文件就只弹一句提示，不做多余动作；
2. 有新改动就执行 `dotnet publish`（与下方打包命令同一套参数）编译到
   `%LocalAppData%\ShunSeek\update-staging`；
3. 生成 `%LocalAppData%\ShunSeek\apply-update.ps1`，由 **pwsh 7** 在后台等本进程退出，
   再 `robocopy` 覆盖 `E:\Apps\ShunSeek`，最后拉起 `ShunSeek.exe --updated`；
4. 新版启动后读 `pending-update.json`，弹一张书页样式的更新说明——
   条目取「上次部署时间」之后的 git 提交标题，没有 git 就退回「本次更新包含 N 个文件」。

开发目录默认 `E:\project_开\ShunSeek\ShunSeek`，可在菜单里「更改开发目录…」重新指定，
记录在 `%LocalAppData%\ShunSeek\devroot.txt`；替换与更新的日志在
`%LocalAppData%\ShunSeek\logs\update.log`。

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
| 建索引 | **1557 ms**（含全拼转换） |
| 索引内存粗估 | 481 MB（含全拼 char 池；⚠ 仍超阶段 1 的 200MB 线，优化路线见下） |
| 查询「报告」 | 首次 27 ms（含 JIT），平均 **10.7 ms** |
| 查询「合同」 | 平均 **10.2 ms** |
| 查询「report」 | 平均 **20.7 ms** |
| 查询「wzry」（拼音首字母） | 平均 **17.1 ms**，正确命中王者荣耀 |
| 查询「mp4」+ 视频分类 | 平均 **5.6 ms** |
| 查询「2024」（大命中面） | 平均 **11.7 ms** |

**真机索引（便携模式，本机用户目录）**

| 指标 | 结果 |
|---|---|
| 建索引 | 109 s（排除缓存目录前）/ 现在 **446,145 条** |
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
