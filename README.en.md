**English** · [中文](README.cn.md)

# OPC UA CSV Logger — v1.7

A small OPC UA client (客户端) that polls node values from an OPC UA server (服务器) at a fixed
interval and appends them to CSV files (one per node). Built against a Beckhoff TwinCAT OPC UA
server, but works with any OPC UA server. A small WinForms config panel (配置面板) ships alongside
it for editing the configuration and starting/stopping the logger without typing commands.

Versioning (版本约定): every code change bumps the version by 0.1 (v1.7 → v1.8 → …), set in both
`.csproj` files and shown in the panel title bar and the logger's startup line.

## Download a ready-to-run build (下载)

Prebuilt 64-bit Windows builds are attached to the
[releases page](https://github.com/K9wwh/opcua-csv-logger/releases) (currently **v1.7.0**), so a
field PC needs no build tools:

| Asset | Size | Target PC needs |
|---|---|---|
| `OpcUaCsvLogger-v1.7.0-win-x64-selfcontained.zip` | ~52 MB | Nothing — self-contained (自包含) |
| `OpcUaCsvLogger-v1.7.0-win-x64-lite.zip` | ~3 MB | .NET 10 **Desktop** Runtime already installed (框架依赖) |

Unzip into a **writable** folder (not `C:\Program Files`), edit `config.json`, then run
`ConfigPanel.exe` for the panel or `OpcUaCsvLogger.exe` for the console version. Both zips already
contain the two programs, a `config.json` seeded from `config.example.json`, and a copy of
TROUBLESHOOTING.md.

For the lite build, install the runtime once on the target PC:

```powershell
winget install Microsoft.DotNet.DesktopRuntime.10
```

The panel is WinForms, so it needs the **Desktop** runtime, not just the base runtime.

## Run it

```powershell
git clone https://github.com/K9wwh/opcua-csv-logger.git
cd opcua-csv-logger
copy config.example.json config.json   # then edit config.json for your server
dotnet run
```

Stop with **Ctrl+C**. Each node gets its own CSV file in the **`data\` folder**, named
`{NodeId}_{CsvPath}` (e.g. `data\ns=4;s=GVL.MyVariable_data.csv`), one row per second. A node's file
is created on the **first read that carries a value**, whatever the status — a read with a `Bad`
status that still returns a value creates it too — because that is when the value's shape
(scalar / array size) becomes known. Restarting appends to the same files. If a node's array size
later changes (e.g. after a PLC redeploy), the logger keeps the original columns and warns — rename
that node's file and restart to get fresh columns. (If the shape already differs when the file is *reopened* at
startup, the header no longer matches and **that node is not logged until you rename or delete its
file and restart**; the other nodes keep going.)

You can also pass a different config file: `dotnet run -- myconfig.json`.

## Configuration (config.json)

The repo ships [config.example.json](config.example.json) — copy it to `config.json` (gitignored)
and fill in your own server's values:

| Setting | Meaning |
|---|---|
| `EndpointUrl` | Server address (服务器地址), e.g. `opc.tcp://MY-PLC:4840` or `opc.tcp://192.168.0.10:4840` |
| `NodeIds` | Node addresses (节点地址) to log — copy from UaExpert's *Attributes → NodeId* pane |
| `PollIntervalMs` | Poll interval in milliseconds (轮询间隔), 1000 = every second |
| `CsvPath` | Base name for the per-node output files. A bare file name (the normal case) goes into the `data\` folder as `data\{NodeId}_{CsvPath}`; if `CsvPath` contains an explicit directory, that directory is used as-is and only its file name becomes the suffix (`out\daily.csv` → `out\{NodeId}_daily.csv`) |
| `UseSecurity` | `false` = SecurityPolicy None (matches your UaExpert session); `true` = most secure endpoint |
| `AutoAcceptServerCertificate` | Trust the server certificate (服务器证书) automatically |
| `Username` / `Password` | Leave `null` for anonymous login (匿名登录) |

`PollIntervalMs` must be at least `50`; the logger exits with an error message below that, and the
panel's spinner is clamped to 50 – 3,600,000 ms.

The shipped example, for reference:

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

## Config panel (配置面板)

`ConfigPanel` is a small window for editing config.json and starting/stopping the logger without
typing commands:

```powershell
dotnet run --project ConfigPanel
```

Edit the fields, then **启动 Start** (saves automatically, then launches the logger hidden in the
background) and **停止 Stop** (graceful shutdown, same as Ctrl+C). Everything the logger prints
appears live in the panel's log area **and** is saved to a daily file under `logs\` (e.g.
`logs\2026-07-08.log`) — that file is the evidence to collect when something goes wrong.

Closing the panel while logging does **not** stop the logger: the app minimizes to a tray icon
(托盘图标) in the taskbar's bottom-right corner. Double-click the icon to reopen; right-click →
停止并退出 (Stop & Exit) to actually quit. If the panel is ever killed while a hidden logger runs,
just reopen it — it adopts the running logger so Stop works again.

Before saving, the panel checks that `EndpointUrl` starts with `opc.tcp://`, that there is at least
one NodeId, and that each NodeId looks like `ns=4;s=GVL.MyVar`. The **…** button next to the output
path picks the *folder* for the per-node CSV files; the base file name stays editable as text (handy
for rolling to a new file each day). Stop sends Ctrl+C and waits 3 seconds before force-killing the
process — that is data-safe, because every CSV row is flushed as it is written. If the logger dies
within 3 seconds of starting, the panel pops up the common causes (bad NodeId, invalid CSV output
path or two NodeIds mapping to the same file name, damaged `pki` folder).

**Poll timing (轮询计时)** — a live line below the buttons shows, for each poll cycle: `本轮`
(last cycle: the batched read of all nodes + the CSV writes, in ms), `峰值` (max so far —
**excluding the very first poll**, which pays one-time cold-start costs of JIT compilation, CSV
file creation and antivirus scanning, always exceeds the interval, and would otherwise pin the
max forever; it shows as `峰值 —` until the second poll, and the first-poll cost is still visible
in `本轮`), and `采集时长` (elapsed time from the first successful poll to the latest). This answers the capacity
question: the logger sends **one batched read request per cycle**, so as long as the cycle time
stays well below `PollIntervalMs` there is headroom to add more NodeIds. There is no advance
warning: only once a cycle actually reaches or exceeds the interval does the logger log a warning
that ticks are being skipped (missing rows), and that warning repeats at most once every 60
seconds. Two limits exist: cycle time (watch this display) and the server's own per-request node
cap — exceeding that
one shows `BadTooManyOperations` retry errors. During an outage the line shows
`本轮 读取失败,重试中` instead of freezing at the last healthy numbers; `采集时长` keeps counting
(it is monotonic — immune to clock adjustments). The per-second timing feed is an opt-in machine
contract: the panel sets `OPCUA_CSV_LOGGER_TIMING_FEED=1` when launching the logger and filters
the `[timing]` lines out of `logs\*.log` and the view; any other run (manual cmd, Task Scheduler,
`> file` redirects) never emits them and gets timing via the periodic status line instead.

Note: saving from the panel rewrites config.json without the explanatory comments — the field
meanings are documented above. Running `OpcUaCsvLogger.exe` manually in a cmd window still works
exactly as before (visible console, no log file).

## Publishing standalone builds (发布)

Two publish scripts, pick by target machine:

| Script | Output | Size | Target PC needs |
|---|---|---|---|
| `.\publish.ps1` | `handover\` | ~128 MB | Nothing — self-contained (自包含) |
| `.\publish-lite.ps1` | `handover-lite\` | ~11 MB | .NET 10 **Desktop** Runtime already installed (框架依赖) |

Both bundle the two programs and TROUBLESHOOTING.md. Zip the folder to distribute it.
Requirements on the target machine (same for both versions):

1. 64-bit Windows 10/11, running from a writable folder (not `C:\Program Files`).
2. Network access to the device (machine network, port 4840); update `EndpointUrl` if their
   network sees the device under a different IP.
3. One-time certificate trust on the PLC side: the program generates a **new client certificate
   on each new PC**, which the server will reject until trusted (see Troubleshooting below).

Do **not** include your `pki\` folder or logged `data.csv` files in a distributed build.

## CSV format

One file per node, in the `data\` folder. Columns: `timestamp` (local wall-clock time, whole
seconds: `yyyy-MM-dd HH:mm:ss.000`), then `status` (data quality 数据质量: `Good` means
trustworthy, anything starting with `Bad` means the value cells are not), then the value columns.

Value columns depend on the node's type:
- scalar (标量) → a single `value` column
- 1-D array (数组) → one column per element: `[0]`, `[1]`, …
- matrix (矩阵/多维数组) → one column per element in row-major order (行优先): `[0][0]`, `[0][1]`, …

All files written in the same poll share the same timestamp, so they can be joined on it later.
Decimals always use `.` regardless of Windows locale.

Files are created as UTF-8 with a BOM, so Excel opens them correctly on a double-click. Fields
containing a comma, a quote or a line break are quoted and escaped the usual CSV way.

## Troubleshooting on the machine network (offline)

> For day-to-day field operation problems (bad status codes, dropped connections, CSV issues,
> long-run stability), see the dedicated handbook: [TROUBLESHOOTING.md](TROUBLESHOOTING.md) (现场故障排查手册).
> **Note:** that handbook is written in Chinese, because it is used by the operators on the machine
> floor — the tables are keyed by the program's English console messages and status codes, so they
> are still usable with a translator. The entries below cover first-time setup.

**"BadNotConnected / could not resolve host / timeout" right at startup**
The endpoint is unreachable. Try the device IP instead of the hostname in `EndpointUrl`
(find it in UaExpert under the server's discovery URL, or ask whoever manages the PLC).
Check you can `ping` the device from the same PC.

**"BadSecurityChecksFailed" or certificate errors from the server side**
The *server* may reject unknown clients. On Beckhoff TwinCAT: TcOpcUaServer keeps rejected
client certificates in its `PKI\CA\rejected` folder — move this app's certificate
(`OpcUaCsvLogger`) to `PKI\CA\trusted\certs`, or use the TcOpcUaConfigurator to trust it.
This is the single most common first-run failure. UaExpert worked because its certificate
was already trusted the same way.

**"BadUserAccessDenied"**
The server requires a username/password — fill in `Username`/`Password` in config.json.

**"BadNodeIdUnknown" in the status column**
The NodeId string is wrong or the PLC program changed. Re-check the exact NodeId in UaExpert
(namespace index `ns=` matters and can change after a PLC redeploy).

**Rows appear but values are empty with status ≠ Good**
The connection works; the PLC variable is not readable (check that the variable still exists
and is published by the TwinCAT OPC UA server configuration).

**Connection drops mid-run**
Normal — the logger keeps retrying every poll interval and resumes appending automatically
when the server is reachable again. Gaps in the timestamps show the outage window.

**File won't open in Excel while logging**
The file is written with shared-read access, so opening a *copy* always works. Excel may lock
the original; close it before restarting the logger if you see file-access errors.

## License

GPL-2.0 — see [LICENSE](LICENSE). This project depends on the
[OPC Foundation .NET Standard UA stack](https://github.com/OPCFoundation/UA-.NETStandard), which is
dual-licensed (RCL for OPC Foundation members / GPL-2.0 for everyone else); distributing this source
under GPL-2.0 follows that licensing.
