# OPC UA CSV Logger — v1.7

A small OPC UA client that polls node values from an OPC UA server at a fixed interval and appends
them to CSV files (one per node), plus a WinForms config panel for editing the config and
starting/stopping the logger without typing commands. Built against a Beckhoff TwinCAT OPC UA
server, but works with any OPC UA server.

一个小型 OPC UA 客户端:按固定间隔轮询 OPC UA 服务器上的节点值,追加写入 CSV 文件(每个节点一个),
另附一个 WinForms 配置面板,用来编辑配置、启停记录器,不必敲命令。开发时针对倍福 TwinCAT OPC UA
服务器验证,但适用于任何 OPC UA 服务器。

📖 **Docs:** [English](README.en.md) · [中文](README.cn.md)

⬇️ **Downloads / 下载:** prebuilt Windows x64 builds on the
[releases page](https://github.com/K9wwh/opcua-csv-logger/releases) —
self-contained (~52 MB) or lite (~3 MB, needs the .NET 10 Desktop Runtime).

```powershell
git clone https://github.com/K9wwh/opcua-csv-logger.git
cd opcua-csv-logger
copy config.example.json config.json   # then edit config.json for your server
dotnet run
```

🛠 Field troubleshooting handbook / 现场故障排查手册: [TROUBLESHOOTING.md](TROUBLESHOOTING.md)
(written in Chinese, keyed by the program's English console messages).

📄 License: **GPL-2.0** — see [LICENSE](LICENSE).
