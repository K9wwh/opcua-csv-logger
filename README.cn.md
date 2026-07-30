[English](README.en.md) · **中文**

# OPC UA CSV Logger — v1.7

一个小型 OPC UA 客户端 (client):按固定间隔轮询 OPC UA 服务器 (server) 上的节点值,并追加写入
CSV 文件(每个节点一个文件)。开发时针对倍福 TwinCAT OPC UA 服务器验证,但适用于任何 OPC UA
服务器。附带一个 WinForms 配置面板 (config panel),用来编辑配置、启停记录器,不必敲命令。

版本约定 (versioning):每次改代码版本号加 0.1(v1.7 → v1.8 → …),两个 `.csproj` 文件里都要改;
版本号显示在面板标题栏和记录器的启动行里。

## 下载现成可运行的构建 (download a ready-to-run build)

64 位 Windows 的预编译构建 (prebuilt build) 挂在
[releases 页面](https://github.com/K9wwh/opcua-csv-logger/releases)(当前 **v1.7.0**),现场电脑
不需要装编译工具:

| 文件 | 体积 | 目标电脑需要 |
|---|---|---|
| `OpcUaCsvLogger-v1.7.0-win-x64-selfcontained.zip` | ~52 MB | 什么都不用装 —— 自包含 (self-contained) |
| `OpcUaCsvLogger-v1.7.0-win-x64-lite.zip` | ~3 MB | 已装 .NET 10 **Desktop** Runtime(框架依赖 framework-dependent) |

解压到一个**可写**的文件夹(不要放 `C:\Program Files`),编辑 `config.json`,然后运行
`ConfigPanel.exe`(面板)或 `OpcUaCsvLogger.exe`(控制台版)。两个压缩包里都已经包含两个程序、
一份由 `config.example.json` 生成的 `config.json`,以及 TROUBLESHOOTING.md 的副本。

用 lite 版的话,在目标电脑上装一次运行时:

```powershell
winget install Microsoft.DotNet.DesktopRuntime.10
```

面板是 WinForms 程序,所以需要 **Desktop** 运行时,只装基础运行时不够。

## 运行 (run it)

```powershell
git clone https://github.com/K9wwh/opcua-csv-logger.git
cd opcua-csv-logger
copy config.example.json config.json   # then edit config.json for your server
dotnet run
```

按 **Ctrl+C** 停止。每个节点在 **`data\` 文件夹**里有自己的 CSV 文件,文件名为
`{NodeId}_{CsvPath}`(例如 `data\ns=4;s=GVL.MyVariable_data.csv`),每秒一行。某个节点的文件在它
**第一次读到带值的结果**时才创建,与状态码无关 —— 状态是 `Bad` 但仍带回了值的读取同样会创建文件 ——
因为那时才知道值的形状 (shape,标量还是数组、数组多长)。重启程序会接着往同一批文件追加。如果某个
节点的数组长度之后变了(比如 PLC 重新下载程序),记录器保持原有列不变并给出警告 —— 把该节点的文件
改名后重启,才会生成新的列。(如果启动时**重新打开**文件就发现
形状已经不一致,表头对不上,则**该节点在改名或删除其文件并重启之前不会被记录**;其他节点照常
工作。)

也可以指定别的配置文件:`dotnet run -- myconfig.json`。

## 配置 (config.json)

仓库里带了 [config.example.json](config.example.json) —— 复制成 `config.json`(已在 .gitignore
里),再填自己服务器的值:

| 配置项 | 含义 |
|---|---|
| `EndpointUrl` | 服务器地址 (server address),例如 `opc.tcp://MY-PLC:4840` 或 `opc.tcp://192.168.0.10:4840` |
| `NodeIds` | 要记录的节点地址 (node address) —— 从 UaExpert 的 *Attributes → NodeId* 窗格原样复制 |
| `PollIntervalMs` | 轮询间隔 (poll interval),单位毫秒,1000 = 每秒一次 |
| `CsvPath` | 每个节点输出文件的基础名。只写文件名时(常见情况)放进 `data\` 文件夹,即 `data\{NodeId}_{CsvPath}`;如果 `CsvPath` 里带了明确目录,则该目录按原样使用,只取其文件名作后缀(`out\daily.csv` → `out\{NodeId}_daily.csv`) |
| `UseSecurity` | `false` = SecurityPolicy None(与你的 UaExpert 会话一致);`true` = 用最安全的端点 (endpoint) |
| `AutoAcceptServerCertificate` | 自动信任服务器证书 (server certificate) |
| `Username` / `Password` | 留 `null` 表示匿名登录 (anonymous login) |

`PollIntervalMs` 最小为 `50`;低于这个值记录器会报错退出,面板里的数字框也被限制在
50 – 3,600,000 ms 之间。

仓库自带的示例,供对照:

```json
{
  "EndpointUrl": "opc.tcp://192.168.0.10:4840",
  "NodeIds": [
    "ns=4;s=GVL.MyVariable",
    "ns=4;s=GVL_IO.AnalogInputs",
    "ns=4;s=GVL_IO.DigitalOutputs",
    "ns=4;s=GVL.HeartbeatOk"
  ],
  "PollIntervalMs": 1000,
  "CsvPath": "data.csv",
  "UseSecurity": false,
  "AutoAcceptServerCertificate": true,
  "Username": null,
  "Password": null
}
```

## 配置面板 (config panel)

`ConfigPanel` 是一个小窗口,用来编辑 config.json 并启停记录器,不用敲命令:

```powershell
dotnet run --project ConfigPanel
```

填好各字段,然后点 **启动 Start**(会自动先保存,再在后台隐藏启动记录器)、**停止 Stop**
(优雅退出 graceful shutdown,等同于 Ctrl+C)。记录器打印的所有信息都实时出现在面板下方的日志区,
**并且**保存到 `logs\` 下的当天日志文件(例如 `logs\2026-07-08.log`)—— 出问题时要收集的证据就是
这个文件。

记录过程中关闭面板窗口**不会**停止记录器:程序会缩到任务栏右下角的托盘图标 (tray icon)。双击图标
重新打开;右键 → 停止并退出 (Stop & Exit) 才是真正退出。如果面板被强制结束、而隐藏的记录器还在跑,
重新打开面板即可 —— 它会接管 (adopt) 正在运行的记录器,停止按钮又能用了。

保存前面板会校验:`EndpointUrl` 必须以 `opc.tcp://` 开头、至少要有一个 NodeId、每个 NodeId 形如
`ns=4;s=GVL.MyVar`。输出路径旁的 **…** 按钮选的是放各节点 CSV 文件的*文件夹*;基础文件名仍然作为
文本可改(方便每天换一个新文件)。停止时先发 Ctrl+C,等 3 秒后再强杀 (force-kill) 进程 —— 这样不会
丢数据,因为每写一行 CSV 就立即刷盘 (flush)。如果记录器启动后 3 秒内就退出,面板会弹窗列出常见
原因(NodeId 写错、CSV 输出路径无效或两个 NodeIds 生成同名文件、`pki` 文件夹损坏)。

**轮询计时 (poll timing)** —— 按钮下方有一行实时显示,针对每个轮询周期给出:`本轮`(上一周期:
把所有节点打包成一次读取 + 写 CSV 的耗时,毫秒),`峰值`(到目前为止的最大值 ——
**不含第一次轮询**,第一次要付一次性的冷启动开销 (cold start):JIT 编译、创建 CSV 文件、杀毒软件
扫描新文件,必然超过轮询间隔,否则会把峰值永久钉在那个数上;第二次轮询之前显示 `峰值 —`,而第一次
的耗时仍能在 `本轮` 里看到),以及 `采集时长`(从第一次成功轮询到最近一次的已用时间)。这一行回答的是
容量 (capacity) 问题:记录器**每个周期只发一个打包的读取请求**,所以只要周期耗时远低于
`PollIntervalMs`,就还有余量再加 NodeIds。不会提前预警:只有当某个周期耗时真正达到或超过轮询间隔
时,记录器才会记一条警告,说明有节拍被跳过(会缺行),而且这条警告最多每 60 秒记一次。有两个上限:
周期耗时(看这行显示)和服务器自身对单次请求节点数的上限 —— 超过后者会看到
`BadTooManyOperations` 重试错误。断线期间这一行显示 `本轮 读取失败,重试中`,而不是冻结在
最后一组正常数字上;`采集时长` 继续走(它用单调时钟 monotonic clock —— 不受改系统时间影响)。
这个每秒一条的计时数据流是一个需要显式开启的机器接口 (machine contract):面板启动记录器时会设置
`OPCUA_CSV_LOGGER_TIMING_FEED=1`,并把 `[timing]` 行从 `logs\*.log` 和显示里过滤掉;其他任何运行
方式(手动 cmd、任务计划程序 Task Scheduler、`> file` 重定向)都不会输出这些行,而是通过周期性的
状态行了解计时。

注意:从面板保存会重写 config.json,原有的注释说明会丢失 —— 字段含义见上文。在 cmd 窗口里手动运行
`OpcUaCsvLogger.exe` 和以前完全一样(有可见控制台、不写日志文件)。

## 发布独立构建 (publishing standalone builds)

两个发布脚本,按目标机器选:

| 脚本 | 输出 | 体积 | 目标电脑需要 |
|---|---|---|---|
| `.\publish.ps1` | `handover\` | ~128 MB | 什么都不用装 —— 自包含 (self-contained) |
| `.\publish-lite.ps1` | `handover-lite\` | ~11 MB | 已装 .NET 10 **Desktop** Runtime(框架依赖 framework-dependent) |

两个脚本都会打包这两个程序和 TROUBLESHOOTING.md。把文件夹压缩后分发即可。
目标机器上的要求(两个版本相同):

1. 64 位 Windows 10/11,从可写文件夹运行(不要放 `C:\Program Files`)。
2. 能访问设备网络(机台网络,4840 端口);如果对方网络下设备是另一个 IP,要改 `EndpointUrl`。
3. PLC 侧一次性信任证书:程序**在每台新电脑上都会生成新的客户端证书 (client certificate)**,
   服务器在信任之前会拒绝它(见下面的故障排查)。

分发的构建里**不要**带上你自己的 `pki\` 文件夹或已记录的 `data.csv` 文件。

## CSV 格式 (CSV format)

每个节点一个文件,都在 `data\` 文件夹里。列:`timestamp`(本机墙上时钟时间 wall-clock time,取整秒:
`yyyy-MM-dd HH:mm:ss.000`),然后是 `status`(数据质量 data quality:`Good` 表示值可信,以 `Bad`
开头的都表示数值单元格不可信),之后是数值列。

数值列由节点类型决定:
- 标量 (scalar) → 只有一列 `value`
- 一维数组 (1-D array) → 每个元素一列:`[0]`、`[1]`、…
- 矩阵/多维数组 (matrix) → 按行优先 (row-major) 每个元素一列:`[0][0]`、`[0][1]`、…

同一次轮询写出的所有文件共用同一个时间戳,方便事后按时间戳拼表。小数点永远用 `.`,与 Windows
区域设置无关。

文件创建时使用 UTF-8 带 BOM,所以在 Excel 里双击能正常打开。含逗号、引号或换行的字段按 CSV
通行规则加引号并转义 (escape)。

## 机台网络上的故障排查(离线)

> 日常现场使用中的问题(状态码不正常、断线、CSV 问题、长时间运行的稳定性),见专门的手册:
> [TROUBLESHOOTING.md](TROUBLESHOOTING.md)(现场故障排查手册)。该手册用中文写成,面向机台现场
> 的操作人员;表格以程序打印的英文控制台信息和状态码为索引。下面几条只覆盖首次部署的问题。

**刚启动就报 "BadNotConnected / could not resolve host / timeout"**
端点不可达。把 `EndpointUrl` 里的主机名换成设备 IP 试试(在 UaExpert 里看服务器的 discovery URL,
或者问管 PLC 的人)。确认在同一台电脑上能 `ping` 通设备。

**"BadSecurityChecksFailed" 或服务器侧的证书错误**
*服务器*可能会拒绝未知的客户端。倍福 TwinCAT 上:TcOpcUaServer 把被拒的客户端证书放在它的
`PKI\CA\rejected` 文件夹 —— 把本程序的证书(`OpcUaCsvLogger`)移到 `PKI\CA\trusted\certs`,
或者用 TcOpcUaConfigurator 去信任它。这是首次运行最常见的失败原因。UaExpert 能连上,是因为它的
证书早就用同样的方式被信任过了。

**"BadUserAccessDenied"**
服务器要求用户名/密码 —— 在 config.json 里填 `Username`/`Password`。

**status 列出现 "BadNodeIdUnknown"**
NodeId 字符串写错了,或者 PLC 程序变了。在 UaExpert 里重新核对准确的 NodeId(命名空间序号
(namespace index)`ns=` 是有意义的,PLC 重新下载后可能会变)。

**有行写出来,但数值是空的且 status ≠ Good**
连接是通的;是那个 PLC 变量读不了(检查变量是否还存在,以及是否被 TwinCAT OPC UA 服务器配置发布出来)。

**运行中途断线**
正常 —— 记录器每个轮询间隔都会重试,服务器恢复可达后自动接着追加。时间戳里的空档就是断线时段。

**记录期间 Excel 打不开文件**
文件是以共享读 (shared-read) 方式写的,所以打开一份*副本*总是可行。Excel 可能会锁住原文件;
如果出现文件访问错误,重启记录器前先把 Excel 关掉。

## 许可证 (license)

GPL-2.0 —— 见 [LICENSE](LICENSE)。本项目依赖
[OPC Foundation .NET Standard UA stack](https://github.com/OPCFoundation/UA-.NETStandard),
该库采用双重许可 (dual-licensed)(OPC Foundation 会员适用 RCL / 其他所有人适用 GPL-2.0);
本项目源码以 GPL-2.0 分发正是遵循这一许可安排。
