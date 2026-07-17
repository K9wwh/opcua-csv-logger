# 现场故障排查手册 (Field Troubleshooting Handbook)

适用对象:程序已验证可用之后的**现场使用阶段**。机台网络无外网,遇到问题先查本手册。
首次部署/首次连接的问题(证书信任、初次配置)见 [README.md](README.md)。

**两种运行方式,信息在不同地方**:
- **从配置面板启动**:没有控制台窗口。所有信息在面板下方的日志区,并且**同时保存在 `logs\日期.log` 文件里**。本手册说"控制台信息"时,就去看那里。
- **在 cmd 里手动运行** `OpcUaCsvLogger.exe`:信息在控制台窗口,关窗即失,注意及时复制。

---

## 1. 快速定位 (Quick diagnosis)

| 现象 (Symptom) | 去哪节 |
|---|---|
| 程序启动后立即退出 | §2 |
| 反复打印 `Connecting to ...` / `will retry` | §3 |
| 已连接,但 CSV 的 `_status` 列不是 `Good` | §4 |
| 数值看起来不对(小数点、数组、时间对不上) | §5 |
| CSV 文件问题(缺行、打不开、太大) | §6 |
| 运行了一段时间后自己停了 | §7 |
| 要带回去求助,先收集什么 | §8 |

---

## 2. 启动即退出 (Exits immediately)

程序启动就退出时,**一定**有一条错误信息,对照下表:

| 控制台信息 | 原因 | 处理 |
|---|---|---|
| `Config file not found` | 找不到 config.json,或不在程序目录下运行 | 确认 config.json 和 OpcUaCsvLogger.exe 在同一个文件夹里;或把配置文件路径作参数传入 |
| `Invalid JSON in config.json` | JSON 语法错误 (syntax error):缺逗号、缺引号、括号不配对 | 对照 config.example.json 里的示例逐行检查刚改过的那一行 |
| `config.json must define "EndpointUrl" ...` | 关键字段缺失或拼错 | 检查字段名大小写拼写 |
| `Invalid NodeId in config` | 节点地址 (NodeId) 格式错误 | 必须形如 `ns=4;s=GVL.MyVar`,从 UaExpert 的 Attributes 窗口原样复制 |
| `Cannot prepare CSV outputs: ...` | CSV 输出路径无效/无权限,或两个 NodeIds 生成了同名文件 | 检查 CsvPath;核对 NodeIds 是否重复 |
| `Cannot access private key ... BadConfigurationError` | 程序旁边的 `pki` 文件夹有问题:从别的电脑拷贝过来的(证书不能跨机复用),或程序放在 OneDrive 等同步目录里,私钥 (private key) 文件被同步软件破坏/占用 | 删除 exe 旁边的整个 `pki` 文件夹再运行(会自动生成新证书),然后在 PLC 侧重新信任新证书;程序放在纯本地目录(如 `C:\Tools\opcua_logger`),不要放 Documents/桌面等同步目录 |

---

## 3. 连不上服务器 (Cannot connect)

程序**不会崩**,会每秒重试一次并打印原因。按原因处理:

| 重试信息里的关键词 | 原因 | 处理 |
|---|---|---|
| `No such host is known` | 主机名 (hostname) 解析失败 | `EndpointUrl` 里改用设备 IP 地址;先 `ping` 一下确认通 |
| 超时 / `timed out` / `BadRequestTimeout` | 网络不通或端口被拦 | ① 确认已切到机台网络;② `ping` 设备;③ 防火墙 (firewall) 放行 4840 端口;④ 确认 PLC 在 Run 模式且 OPC UA Server (TF6100) 在运行 |
| `actively refused` / 拒绝连接 | **有主机在这个 IP 上,但 4840 端口没有服务**——多半 IP 填错了(是别的设备),或 PLC 的 OPC UA Server 没在该网口监听 | ① 在机台网络上 `ping <设备主机名>`,用它回应的 IP;② 用 UaExpert 对同一 IP 做 Custom Discovery 独立验证;③ 注意倍福 IPC 常有多个网口 (multiple NICs),子网可能不同 |
| `BadSecurityChecksFailed` | 服务器把本客户端证书 (client certificate) 拒了——证书重新生成过、或服务器端信任被清空时会复发 | 在 PLC/IPC 上找 TwinCAT OPC UA Server 的 PKI 文件夹(常见 `C:\TwinCAT\Functions\TF6100-OPC-UA\Win32\Server\PKI\CA`,不同版本位置略有差异),把 `rejected` 里的 `OpcUaCsvLogger*.der` 移到 `trusted\certs`;或用 TcOpcUaConfigurator 的 Certificates 页签操作 |
| `BadUserAccessDenied` / `BadIdentityTokenRejected` | 服务器要求登录 | 在 config.json 填 `Username` / `Password` |
| `BadTooManySessions` | 会话 (session) 数超限 | 关掉同时连着的 UaExpert 或其他客户端再试 |
| `BadTooManyOperations` | **一次读取的节点数超过服务器上限**——config.json 里 NodeIds 太多(程序每周期把全部节点打包成一次读取请求) | 减少 NodeIds 数量;或分成两份配置、开两个程序目录分别采集 |
| `Accepted server certificate despite BadCertificateTimeInvalid` | 不是故障——设备证书时间无效(PLC 时钟不准),程序已自动接受并继续 | 顺手校准一下 PLC 时钟更好 |

> 断线期间 CSV 不写行。恢复连接后自动续写,时间戳出现空档属正常,空档就是断线时段。

---

## 4. 已连接但 `_status` 列不是 Good (Bad quality)

值列可能是空的,看同名 `_status` 列:

| 状态码 (StatusCode) | 含义 | 处理 |
|---|---|---|
| `Good` | 数据可信 | — |
| `BadNodeIdUnknown` | 服务器上没有这个节点。**最常见原因:PLC 程序重新下载 (download) 后变量改名/删除,或命名空间序号 `ns=` 变了** | 用 UaExpert 重新浏览,核对 NodeId 后更新 config.json(注意 §2 的换文件提示) |
| `BadNotReadable` / `BadUserAccessDenied` | 变量存在但不允许读 | PLC 侧检查该变量的 OPC UA 发布属性 |
| `BadWaitingForInitialData` | 刚连上第一拍还没取到值 | 偶发一行,可忽略;持续出现才要查 |
| `Uncertain...` 开头 | 值可疑(如传感器故障时的保持值) | 值先别用,PLC/传感器侧排查 |
| `Bad_0x........`(十六进制) | 少见状态码 | 记下来带回查(见 §8) |

---

## 5. 数值看起来不对 (Data looks wrong)

- **每个节点一个文件,统一放在 `data\` 文件夹**:文件名 = 节点地址 + 基础名,如 `data\ns=4;s=GVL.MyVariable_data.csv`;同一秒写入的各文件时间戳相同,可按时间戳对齐拼表。
- **列顺序**:第 1 列时间戳,第 2 列 `status`(一眼可见数据质量),之后是数值列。
- **数组 (array) 拆成多列**:表头 `[0]`、`[1]`…每格一个元素;多维数组按行优先 (row-major) 展开为 `[0][0]`、`[0][1]`…。标量则只有一列 `value`。
- **节点文件迟迟没生成**:该节点从未读到值(状态一直 Bad)——文件在第一次成功读取时才创建,看 §6.4 排查状态。
- **小数点永远是 `.`**:程序固定用国际格式 (invariant culture) 写数,与 Windows 区域设置无关,Excel 里公式按此处理。
- **时间戳与 PLC/HMI 屏幕对不上**:时间戳是**这台电脑的本地时间**(整秒,`.000` 结尾),不是 PLC 时间。电脑时钟不准就校准电脑;要和 PLC 记录对齐,先把两边时钟对齐。
- **值一直不变**:程序每秒照常写行、状态是 `Good`,说明通信正常,是 PLC 变量本身没变——去 PLC 侧确认对应任务 (task) 在跑。

---

## 6. CSV 文件问题

| 现象 | 说明/处理 |
|---|---|
| 中间缺行 | 断线时段,正常。控制台会有对应的 `will retry` 记录 |
| `CSV write failed: ... — row lost` | 写盘失败(最常见:磁盘满)。连接不受影响,清出空间即恢复写入 |
| `was created with different columns ... will NOT be logged` | 该节点的数组大小变了(如 PLC 重新下载),与旧文件表头不符,**该节点**暂停记录,其他节点不受影响 → 把该节点的 CSV 改名/删除后重启 |
| `value now has N element(s) but ... M value column(s)` | 运行中途数组大小变了,程序按旧表头继续写(多出的元素丢弃、缺的留空)→ 想要新列数就把该文件改名后重启 |
| Excel 打开后程序报错 | Excel 会锁文件。**先复制一份再开**;或程序停了再开原文件 |
| Excel 里时间戳显示成 `41:29.0` | 数据没错(点单元格,编辑栏里是完整时间)。CSV 是纯文本 (plain text),不保存单元格格式,Excel 每次打开都对带小数秒的时间套自己的默认显示格式。处理:选中 A 列 → 设置单元格格式 (Format Cells) → 自定义 (Custom) → 输入 `yyyy-mm-dd hh:mm:ss.000`。注意每次重新打开 CSV 都要重设;要保留格式请另存为 .xlsx |
| 中文/符号乱码 | 文件是 UTF-8(带 BOM),Excel 双击打开正常;第三方工具乱码时手动选 UTF-8 编码 |
| 文件越来越大 | 每天固定 86,400 行,体积取决于数组长度(通常每天几 MB 到几十 MB)。长期采集建议每天停一次、改 `CsvPath` 换新文件再启动 |
| 缺行 + 日志出现 `ticks are being skipped` | **单轮耗时超过了轮询间隔**:节点太多或网络/磁盘太慢,来不及每秒采一次,部分秒被跳过。看面板"轮询计时"的`本轮`/`峰值`毫秒数(v1.6 起):逼近轮询间隔就是容量上限 → 减少 NodeIds 或加大 PollIntervalMs |

---

## 7. 运行一段时间后自己停了 (Stopped mid-run)

按可能性从高到低排查:

1. **电脑睡眠/休眠 (sleep/hibernate)** —— 最常见。电源设置:接通电源时"从不"睡眠;合盖时"不采取任何操作"。
2. **Windows 自动更新重启** —— 设置活跃时间,或长采集前手动检查更新。
3. **通过面板启动时**:关掉面板窗口**不会**停止记录——程序缩到任务栏右下角的托盘图标 (tray icon) 继续跑,双击图标可重新打开;右键图标 → 停止并退出 才是真正退出。如果托盘图标意外消失(面板被强制结束),重新打开面板会自动接管后台的记录器。
4. **在 cmd 里手动运行时**:关掉控制台窗口 = 程序结束;旧式控制台 (conhost) 的快速编辑模式 (QuickEdit) 下点选文字会暂停输出,按 `Esc` 恢复。
5. 以上都不是:看 `logs\` 里当天的日志文件最后几行,对照 §2/§3 的表,并按 §8 收集证据。

---

## 8. 带回去求助前,收集这些 (Evidence to collect)

机台网络没有外网,现场解决不了的,把下面几样带回来:

1. **日志**:面板启动的,直接拷走 `logs\` 里当天的 `.log` 文件(最有价值的证据);cmd 手动运行的,窗口里右键全选复制到记事本或截图(尤其最后 20 行)。
2. **data.csv 出问题时段的前后几行**(连同表头第一行)。
3. **时间线**:几点开始、几点出问题、当时做过什么(切网络、重下载 PLC 程序、重启设备、开过 Excel……)。
4. 若是连接类问题:`ping <设备IP>` 的结果截图。

---

## 附录:程序会打印哪些信息 (Console message reference)

| 信息 | 含义 |
|---|---|
| `Logging 3 node(s) every 1000 ms to ...` | 启动成功,开始工作 |
| `Connecting to opc.tcp://...` | 正在连接(每次重连都会打一条) |
| `Accepted server certificate despite ...` | 自动接受了服务器证书,附原因,不是错误 |
| `Connected to TcOpcUaServer@...` | 连接成功 |
| `[时间] N row(s) written, last: ...` | 心跳:第 1 行和之后每 60 行报一次最新值 |
| `[时间] ... — will retry.` | 本秒失败(附原因),下一秒自动重试 |
| `[时间] 节点 -> 文件名 (N value column(s)).` | 该节点首次读到值,创建了对应的 CSV 文件 |
| `[时间] 节点: CSV write failed: ... — row lost.` | 该节点该行丢弃,连接保留(见 §6) |
| `Stopping...` / `Done. N row(s) written per node.` | Ctrl+C 正常收尾,汇报总行数 |
