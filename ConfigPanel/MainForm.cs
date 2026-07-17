using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ConfigPanel;

internal sealed class LoggerConfig
{
    public string EndpointUrl { get; set; } = "opc.tcp://";
    public string[] NodeIds { get; set; } = [];
    public int PollIntervalMs { get; set; } = 1000;
    public string CsvPath { get; set; } = "data.csv";
    public bool UseSecurity { get; set; }
    public bool AutoAcceptServerCertificate { get; set; } = true;
    public string? Username { get; set; }
    public string? Password { get; set; }
}

internal sealed partial class MainForm : Form
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    [GeneratedRegex(@"^(ns=\d+;)?(s|i|g|b)=.+")]
    private static partial Regex NodeIdShape();

    private readonly string _workDir;
    private readonly string _configPath;
    private readonly object _logFileLock = new();
    private Process? _logger;
    private DateTime _startedAt;
    private bool _stopRequested;
    private bool _reallyExit;
    private bool _balloonShown;

    private readonly TextBox _endpoint = new() { Dock = DockStyle.Fill };
    private readonly TextBox _nodeIds = new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true, Height = 100 };
    private readonly NumericUpDown _interval = new() { Minimum = 50, Maximum = 3_600_000, Value = 1000, Increment = 50, Width = 120 };
    private readonly TextBox _csvPath = new() { Dock = DockStyle.Fill };
    private readonly Button _browse = new() { Text = "…", Width = 36 };
    private readonly CheckBox _useSecurity = new() { Text = "UseSecurity — 加密连接 (encrypted connection)", AutoSize = true };
    private readonly CheckBox _autoAccept = new() { Text = "自动信任服务器证书 (auto-accept server certificate)", AutoSize = true, Checked = true };
    private readonly TextBox _username = new() { Dock = DockStyle.Fill };
    private readonly TextBox _password = new() { Dock = DockStyle.Fill };
    private readonly Button _save = new() { Text = "保存 Save", AutoSize = true };
    private readonly Button _start = new() { Text = "启动 Start", AutoSize = true };
    private readonly Button _stop = new() { Text = "停止 Stop", AutoSize = true, Enabled = false };
    private readonly Label _status = new() { Text = "未运行 (stopped)", AutoSize = true, Padding = new Padding(10, 8, 0, 0) };
    private readonly Label _timing = new() { Text = TimingIdle, AutoSize = true, Padding = new Padding(0, 6, 0, 2) };
    private readonly TextBox _log = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false };
    private readonly NotifyIcon _tray = new() { Text = "OPC UA CSV Logger", Visible = false };

    public MainForm()
    {
        _workDir = FindWorkDir();
        _configPath = Path.Combine(_workDir, "config.json");

        Version? ver = typeof(MainForm).Assembly.GetName().Version;
        Text = $"OPC UA CSV Logger {(ver is null ? "" : $"v{ver.Major}.{ver.Minor} ")}配置面板 — {_workDir}";
        ClientSize = new Size(640, 680);
        MinimumSize = new Size(520, 560);

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(12),
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        int row = 0;
        void AddRow(string label, Control control)
        {
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 0, 0) }, 0, row);
            table.Controls.Add(control, 1, row);
            row++;
        }

        AddRow("服务器地址 Endpoint", _endpoint);
        AddRow("节点地址 NodeIds\r\n(每行一个 one per line)", _nodeIds);
        AddRow("轮询间隔 Interval (ms)", _interval);

        var csvRow = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0) };
        csvRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        csvRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        csvRow.Controls.Add(_csvPath, 0, 0);
        csvRow.Controls.Add(_browse, 1, 0);
        AddRow("输出路径 Output path", csvRow);

        AddRow("", _useSecurity);
        AddRow("", _autoAccept);
        AddRow("用户名 Username", _username);
        AddRow("密码 Password", _password);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 10, 0, 0) };
        buttons.Controls.AddRange([_save, _start, _stop, _status]);
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.SetColumnSpan(buttons, 2);
        table.Controls.Add(buttons, 0, row);
        row++;

        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.SetColumnSpan(_timing, 2);
        table.Controls.Add(_timing, 0, row);
        row++;

        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        table.SetColumnSpan(_log, 2);
        table.Controls.Add(_log, 0, row);

        Controls.Add(table);

        _tray.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("显示面板 Show", null, (_, _) => RestoreFromTray());
        trayMenu.Items.Add("停止并退出 Stop && Exit", null, (_, _) => { _reallyExit = true; Close(); });
        _tray.ContextMenuStrip = trayMenu;
        _tray.DoubleClick += (_, _) => RestoreFromTray();

        _browse.Click += (_, _) => BrowseCsv();
        _save.Click += (_, _) => TrySave();
        _start.Click += (_, _) => StartLogger();
        _stop.Click += (_, _) => StopLogger();

        LoadConfig();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Adopt only after the window handle exists — the Exited handler marshals via
        // BeginInvoke, which is silently swallowed before handle creation.
        TryAdoptRunningLogger();
    }

    // The logger resolves config.json, data.csv and pki relative to its working directory.
    // Anchor on the logger itself (exe in the handover layout, csproj in the dev layout), never
    // on config.json alone — a stray config.json in an ancestor folder must not hijack the
    // working directory.
    private static string FindWorkDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "OpcUaCsvLogger.exe")) ||
                File.Exists(Path.Combine(dir.FullName, "OpcUaCsvLogger.csproj")))
            {
                return dir.FullName;
            }
        }
        return AppContext.BaseDirectory;
    }

    private void LoadConfig()
    {
        if (!File.Exists(_configPath))
        {
            _status.Text = "config.json 不存在,保存时将创建 (will be created on save)";
            return;
        }
        try
        {
            LoggerConfig? cfg = JsonSerializer.Deserialize<LoggerConfig>(File.ReadAllText(_configPath), JsonOptions);
            if (cfg is null)
            {
                return;
            }
            _endpoint.Text = cfg.EndpointUrl;
            _nodeIds.Text = string.Join(Environment.NewLine, cfg.NodeIds ?? []);
            _interval.Value = Math.Clamp(cfg.PollIntervalMs, (int)_interval.Minimum, (int)_interval.Maximum);
            _csvPath.Text = cfg.CsvPath;
            _useSecurity.Checked = cfg.UseSecurity;
            _autoAccept.Checked = cfg.AutoAcceptServerCertificate;
            _username.Text = cfg.Username ?? string.Empty;
            _password.Text = cfg.Password ?? string.Empty;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"config.json 读取失败 (cannot read): {ex.Message}", "配置面板", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private bool TrySave()
    {
        string endpoint = _endpoint.Text.Trim();
        if (!endpoint.StartsWith("opc.tcp://", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "服务器地址必须以 opc.tcp:// 开头\r\n(EndpointUrl must start with opc.tcp://)", "配置面板", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        string[] nodes = _nodeIds.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        if (nodes.Length == 0)
        {
            MessageBox.Show(this, "至少填一个节点地址 (at least one NodeId)", "配置面板", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        string? badNode = nodes.FirstOrDefault(n => !NodeIdShape().IsMatch(n));
        if (badNode is not null)
        {
            MessageBox.Show(this, $"节点地址格式不对 (bad NodeId):\r\n{badNode}\r\n\r\n应形如 (expected like): ns=4;s=GVL.MyVar", "配置面板", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        string csv = _csvPath.Text.Trim();
        if (csv.Length == 0)
        {
            MessageBox.Show(this, "CSV 路径不能为空 (CsvPath required)", "配置面板", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        var cfg = new LoggerConfig
        {
            EndpointUrl = endpoint,
            NodeIds = nodes,
            PollIntervalMs = (int)_interval.Value,
            CsvPath = csv,
            UseSecurity = _useSecurity.Checked,
            AutoAcceptServerCertificate = _autoAccept.Checked,
            Username = NullIfEmpty(_username.Text),
            // Passwords are byte-exact — never trim them.
            Password = string.IsNullOrEmpty(_password.Text) ? null : _password.Text,
        };
        try
        {
            File.WriteAllText(_configPath, JsonSerializer.Serialize(cfg, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"保存失败 (save failed): {ex.Message}", "配置面板", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        _status.Text = $"已保存 saved {DateTime.Now:HH:mm:ss}";
        return true;
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // The logger writes one CSV per node; what the user picks is the FOLDER they land in.
    // The base file-name suffix stays editable as text (e.g. for daily file rolling).
    private void BrowseCsv()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "选择数据输出文件夹 (folder for the per-node CSV files)",
            UseDescriptionForTitle = true,
            SelectedPath = CurrentOutputDir(),
        };
        if (dlg.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }
        string baseName = Path.GetFileName(_csvPath.Text.Trim());
        if (string.IsNullOrEmpty(baseName))
        {
            baseName = "data.csv";
        }
        string full = Path.Combine(dlg.SelectedPath, baseName);
        string relative = Path.GetRelativePath(_workDir, full);
        _csvPath.Text = relative.StartsWith("..", StringComparison.Ordinal) ? full : relative;
    }

    // Mirrors the logger's rule: a bare file name goes into the data\ subfolder.
    private string CurrentOutputDir()
    {
        string text = _csvPath.Text.Trim();
        if (string.IsNullOrEmpty(text))
        {
            text = "data.csv";
        }
        string full = Path.GetFullPath(text, _workDir);
        string dir = Path.GetDirectoryName(full)!;
        return string.IsNullOrEmpty(Path.GetDirectoryName(text)) ? Path.Combine(dir, "data") : dir;
    }

    private string? FindLoggerExe()
    {
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "OpcUaCsvLogger.exe"),
            Path.Combine(_workDir, "bin", "Release", "net10.0", "OpcUaCsvLogger.exe"),
            Path.Combine(_workDir, "bin", "Debug", "net10.0", "OpcUaCsvLogger.exe"),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    // A logger may already be running without a window: this panel starts it hidden, so if a
    // previous panel was killed, the logger would otherwise be unreachable except via Task
    // Manager. Adopt it so Stop still works (its output cannot be captured retroactively).
    private void TryAdoptRunningLogger()
    {
        try
        {
            Process? existing = Process.GetProcessesByName("OpcUaCsvLogger").FirstOrDefault();
            if (existing is null)
            {
                return;
            }
            _logger = existing;
            _startedAt = DateTime.Now;
            HookExitedEvent(existing);
            if (existing.HasExited)
            {
                OnLoggerExited();
                return;
            }
            SetRunningUi(existing.Id);
            _status.Text = $"接管了已在运行的记录器 adopted running logger (PID {existing.Id}) — 日志不可见,可停止";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // A logger from another session or an elevated one — we cannot control it.
            _logger = null;
            _status.Text = "检测到无法接管的记录器进程 (unreachable logger — stop it via Task Manager)";
        }
    }

    private void StartLogger()
    {
        if (_logger is not null)
        {
            return;
        }
        if (!TrySave())
        {
            return;
        }
        string? exe = FindLoggerExe();
        if (exe is null)
        {
            MessageBox.Show(this, "找不到 OpcUaCsvLogger.exe,请先编译 (build the logger first):\r\ndotnet build", "配置面板", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = _workDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // Opt in to the per-poll "[timing] ..." feed; we filter it out of the logs ourselves.
        psi.Environment["OPCUA_CSV_LOGGER_TIMING_FEED"] = "1";

        Process? proc;
        try
        {
            proc = Process.Start(psi);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"启动失败 (failed to start): {ex.Message}", "配置面板", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        if (proc is null)
        {
            MessageBox.Show(this, "启动失败 (failed to start)", "配置面板", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        _logger = proc;
        _startedAt = DateTime.Now;
        _stopRequested = false;
        _log.Clear();
        _timing.Text = TimingIdle;

        proc.OutputDataReceived += (_, e) => OnLoggerLine(e.Data);
        proc.ErrorDataReceived += (_, e) => OnLoggerLine(e.Data);
        HookExitedEvent(proc);
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        if (proc.HasExited)
        {
            return; // Exited event handles the UI reset and the died-at-startup dialog.
        }
        SetRunningUi(proc.Id);
    }

    private void HookExitedEvent(Process proc)
    {
        proc.Exited += (_, _) =>
        {
            // Fires on a threadpool thread; the form may be mid-dispose when it arrives.
            try { BeginInvoke(OnLoggerExited); }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
        };
        proc.EnableRaisingEvents = true;
    }

    private void SetRunningUi(int pid)
    {
        _start.Enabled = false;
        _stop.Enabled = true;
        _status.Text = $"运行中 running (PID {pid})";
        _tray.Text = "OPC UA CSV Logger — 运行中 running";
    }

    private const string TimingIdle = "轮询计时 poll timing:—";
    private const string TimingPrefix = "[timing] ";

    private void OnLoggerLine(string? line)
    {
        if (line is null)
        {
            return;
        }
        if (line.StartsWith(TimingPrefix, StringComparison.Ordinal))
        {
            OnTimingLine(line);
            return; // one line per poll — the label shows it; keep it out of the log file and view
        }
        lock (_logFileLock)
        {
            try
            {
                string logDir = Path.Combine(_workDir, "logs");
                Directory.CreateDirectory(logDir);
                File.AppendAllText(Path.Combine(logDir, $"{DateTime.Now:yyyy-MM-dd}.log"), line + Environment.NewLine);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Disk problem — the live view below still shows the line.
            }
        }
        try { BeginInvoke(() => AppendLogLine(line)); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
    }

    // "[timing] last=42 max=58 up=3605 nodes=4" — the logger emits this every poll when we
    // opt in via env var. last/max = one poll cycle (batched read + CSV write) in ms;
    // up = seconds from the first successful poll to now (monotonic on the logger side).
    // A failed cycle (connection lost, read error) sends "fail=1" instead of "last=".
    private void OnTimingLine(string line)
    {
        long last = -1, max = -1, up = -1, nodes = -1, fail = 0;
        foreach (string part in line[TimingPrefix.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0 || !long.TryParse(part[(eq + 1)..], out long v))
            {
                continue;
            }
            switch (part[..eq])
            {
                case "last": last = v; break;
                case "max": max = v; break;
                case "up": up = v; break;
                case "nodes": nodes = v; break;
                case "fail": fail = v; break;
            }
        }
        // 100 years is the sanity ceiling — also keeps FormatUp far away from TimeSpan overflow.
        if (up < 0 || up > 3_155_760_000 || (fail == 0 && last < 0))
        {
            return;
        }
        string lastText = fail != 0 ? "读取失败,重试中" : $"{last} ms";
        // max=-1 until the second successful poll: 峰值 excludes the one-time first-poll cost.
        string maxText = max >= 0 ? $"{max} ms" : "—";
        string text = $"轮询计时 poll timing:本轮 {lastText} | 峰值 {maxText} | 采集时长 {FormatUp(up)}"
            + (nodes > 0 ? $"({nodes} 节点)" : "");
        try { BeginInvoke(() => _timing.Text = text); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
    }

    private static string FormatUp(long seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.Days > 0
            ? $"{t.Days}天 {t.Hours:D2}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"{t.Hours:D2}:{t.Minutes:D2}:{t.Seconds:D2}";
    }

    private void AppendLogLine(string line)
    {
        _log.AppendText(line + Environment.NewLine);
        if (_log.TextLength > 100_000)
        {
            string text = _log.Text;
            bool atEnd = _log.SelectionStart >= text.Length;
            // Cut on a line boundary so the view never starts mid-line.
            int cut = text.IndexOf('\n', text.Length - 50_000) + 1;
            _log.Text = text[cut..];
            if (atEnd)
            {
                _log.SelectionStart = _log.TextLength;
                _log.ScrollToCaret();
            }
        }
    }

    private void OnLoggerExited()
    {
        // Exited can fire before the last redirected-output callbacks are delivered; the
        // parameterless WaitForExit() blocks until stream EOF (near-instant on a dead process)
        // so the tail lines — usually the reason it died — reach the log first.
        try { _logger?.WaitForExit(); } catch (SystemException) { }
        int exitCode = 0;
        try { exitCode = _logger?.ExitCode ?? 0; } catch (SystemException) { }
        bool diedAtStartup = exitCode != 0 && !_stopRequested && DateTime.Now - _startedAt < TimeSpan.FromSeconds(3);

        _logger?.Dispose();
        _logger = null;
        _start.Enabled = true;
        _stop.Enabled = false;
        _status.Text = "未运行 (stopped)";
        _tray.Text = "OPC UA CSV Logger — 已停止 stopped";

        if (!Visible && _tray.Visible && !_stopRequested)
        {
            // Died in the background — the user believes it is still logging.
            _tray.ShowBalloonTip(5000, "OPC UA CSV Logger", "记录器已停止 (logger stopped) — 打开面板查看日志", ToolTipIcon.Warning);
        }
        if (diedAtStartup && Visible)
        {
            MessageBox.Show(this,
                "记录器启动后立刻退出了 (logger exited immediately) — 原因见下方日志。\r\n\r\n常见原因 (common causes):\r\n" +
                "• NodeId 写错,记录器解析失败\r\n" +
                "• CSV 输出路径无效,或两个 NodeIds 生成了同名文件\r\n" +
                "• pki 证书文件夹损坏 — 删除 pki 文件夹重试",
                "配置面板", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void StopLogger()
    {
        Process? proc = _logger;
        if (proc is null || proc.HasExited)
        {
            return;
        }
        _stopRequested = true;
        _stop.Enabled = false;
        _status.Text = "正在停止 stopping…";
        _status.Refresh();

        bool signalled = TrySendCtrlC(proc);
        bool exited = proc.WaitForExit(3000);
        if (signalled)
        {
            NativeMethods.SetConsoleCtrlHandler(nint.Zero, add: false);
        }
        if (!exited)
        {
            // Hard kill is data-safe: the logger flushes every CSV row as it is written.
            try { proc.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        // The Exited event resets the buttons and status.
    }

    private static bool TrySendCtrlC(Process proc)
    {
        if (!NativeMethods.AttachConsole((uint)proc.Id))
        {
            return false;
        }
        // Shield this GUI process (the Ctrl+C event goes to every process attached to that
        // console, including us), fire, and detach immediately — while attached, a user
        // closing the logger's console window would terminate the panel too (CTRL_CLOSE
        // cannot be shielded). The caller unshields after the wait, once delivery is over.
        NativeMethods.SetConsoleCtrlHandler(nint.Zero, add: true);
        NativeMethods.GenerateConsoleCtrlEvent(NativeMethods.CtrlCEvent, 0);
        NativeMethods.FreeConsole();
        return true;
    }

    private void RestoreFromTray()
    {
        _tray.Visible = false;
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        bool loggerRunning = _logger is not null && !_logger.HasExited;

        // Only the user's own X-click hides to the tray. Windows shutdown, logoff and Task
        // Manager closes must never be cancelled — stop the logger and let the close proceed.
        if (!_reallyExit && loggerRunning && e.CloseReason == CloseReason.UserClosing)
        {
            // Modern-app behavior: keep logging in the background, live in the tray.
            e.Cancel = true;
            Hide();
            _tray.Visible = true;
            if (!_balloonShown)
            {
                _tray.ShowBalloonTip(4000, "OPC UA CSV Logger", "仍在后台记录 (still logging in the background) — 双击图标重新打开", ToolTipIcon.Info);
                _balloonShown = true;
            }
        }
        else if (loggerRunning)
        {
            StopLogger();
        }

        if (!e.Cancel)
        {
            _tray.Visible = false;
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _tray.Dispose();
        base.OnFormClosed(e);
    }

    private static class NativeMethods
    {
        public const uint CtrlCEvent = 0;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AttachConsole(uint dwProcessId);

        [DllImport("kernel32.dll")]
        public static extern bool FreeConsole();

        [DllImport("kernel32.dll")]
        public static extern bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);

        [DllImport("kernel32.dll")]
        public static extern bool SetConsoleCtrlHandler(nint handlerRoutine, bool add);
    }
}
