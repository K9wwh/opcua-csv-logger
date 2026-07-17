using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

namespace OpcUaCsvLogger;

internal sealed record LoggerConfig(
    string EndpointUrl,
    string[] NodeIds,
    int PollIntervalMs = 1000,
    string CsvPath = "data.csv",
    bool UseSecurity = false,
    bool AutoAcceptServerCertificate = true,
    string? Username = null,
    string? Password = null);

internal static class Program
{
    private const string ApplicationName = "OpcUaCsvLogger";

    private static async Task<int> Main(string[] args)
    {
        string configPath = args.Length > 0 ? args[0] : "config.json";
        if (!File.Exists(configPath))
        {
            Console.Error.WriteLine($"Config file not found: {Path.GetFullPath(configPath)}");
            return 1;
        }

        LoggerConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<LoggerConfig>(File.ReadAllText(configPath), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"Invalid JSON in {configPath}: {ex.Message}");
            return 1;
        }

        if (config is null || string.IsNullOrWhiteSpace(config.EndpointUrl) || config.NodeIds is not { Length: > 0 })
        {
            Console.Error.WriteLine("config.json must define \"EndpointUrl\" and at least one entry in \"NodeIds\".");
            return 1;
        }
        if (config.PollIntervalMs < 50)
        {
            Console.Error.WriteLine("PollIntervalMs must be at least 50.");
            return 1;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("Stopping...");
            cts.Cancel();
        };

        var nodesToRead = new ReadValueIdCollection();
        try
        {
            foreach (string id in config.NodeIds)
            {
                nodesToRead.Add(new ReadValueId { NodeId = NodeId.Parse(id), AttributeId = Attributes.Value });
            }
        }
        catch (ServiceResultException ex)
        {
            Console.Error.WriteLine($"Invalid NodeId in config: {ex.Message}");
            return 1;
        }

        ApplicationConfiguration appConfig = await BuildApplicationConfigurationAsync(config);

        NodeCsvSink[] sinks;
        try
        {
            sinks = NodeCsvSink.CreateAll(config.CsvPath, config.NodeIds);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            Console.Error.WriteLine($"Cannot prepare CSV outputs: {ex.Message}");
            return 1;
        }

        Console.WriteLine($"OpcUaCsvLogger {AppVersion()} — logging {config.NodeIds.Length} node(s) every {config.PollIntervalMs} ms, one CSV per node in {Path.GetDirectoryName(Path.GetFullPath(config.CsvPath))}");
        Console.WriteLine("Press Ctrl+C to stop.");

        Session? session = null;
        long rowCount = 0;
        long lastPollMs = 0, maxPollMs = -1; // -1 = no steady-state sample yet
        // Monotonic clocks on purpose: field PCs get NTP/manual clock steps, which must not
        // corrupt the elapsed-time feed or mute the overrun warning.
        Stopwatch? upTimer = null;
        long? lastOverrunWarnTick = null;
        // The per-tick timing line is a machine-readable feed for the config panel, which
        // opts in via this env var when launching us. Keying off output redirection instead
        // would flood any other captured run (Task Scheduler, `> file`) with one line per poll.
        bool emitTimingFeed = Environment.GetEnvironmentVariable("OPCUA_CSV_LOGGER_TIMING_FEED") == "1";
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(config.PollIntervalMs));
        try
        {
            do
            {
                try
                {
                    session ??= await ConnectAsync(appConfig, config, cts.Token);

                    var poll = Stopwatch.StartNew();
                    ReadResponse response = await session.ReadAsync(
                        null, 0, TimestampsToReturn.Source, nodesToRead, cts.Token);

                    if (response.Results is null || response.Results.Count != nodesToRead.Count)
                    {
                        throw new InvalidOperationException(
                            $"Server returned {response.Results?.Count ?? 0} value(s) for {nodesToRead.Count} node(s) — row skipped.");
                    }

                    string stamp = RowTimestamp();
                    for (int i = 0; i < sinks.Length; i++)
                    {
                        sinks[i].Write(stamp, response.Results[i]);
                    }
                    poll.Stop();
                    lastPollMs = poll.ElapsedMilliseconds;
                    // The very first poll pays one-time costs (JIT of the read/decode path,
                    // CSV file creation, antivirus scanning the new files) and always dwarfs
                    // steady state — keep it out of 峰值 and out of the overrun warning, or
                    // both would cry wolf on every startup.
                    bool firstPoll = upTimer is null;
                    if (!firstPoll)
                    {
                        maxPollMs = Math.Max(maxPollMs, lastPollMs);
                    }
                    upTimer ??= Stopwatch.StartNew();
                    rowCount++;
                    if (emitTimingFeed)
                    {
                        Console.WriteLine($"[timing] last={lastPollMs} max={maxPollMs} up={(long)upTimer.Elapsed.TotalSeconds} nodes={nodesToRead.Count}");
                    }
                    if (rowCount == 1 || rowCount % 60 == 0)
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {rowCount} row(s) written, last: {Describe(response.Results)} — poll {lastPollMs} ms{(maxPollMs >= 0 ? $" (max {maxPollMs} ms)" : "")}");
                    }
                    if (!firstPoll && lastPollMs >= config.PollIntervalMs
                        && (lastOverrunWarnTick is null || Environment.TickCount64 - lastOverrunWarnTick.Value >= 60_000))
                    {
                        lastOverrunWarnTick = Environment.TickCount64;
                        Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] Poll took {lastPollMs} ms — longer than the {config.PollIntervalMs} ms interval, so ticks are being skipped (rows will be missing). Reduce NodeIds or increase PollIntervalMs.");
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] {ex.Message} — will retry.");
                    if (session is not null)
                    {
                        try { session.Dispose(); } catch { /* already broken */ }
                        session = null;
                    }
                    if (emitTimingFeed && upTimer is not null)
                    {
                        // Without this the panel's timing label would freeze at the last
                        // healthy numbers for the whole outage and look fine while rows drop.
                        Console.WriteLine($"[timing] fail=1 max={maxPollMs} up={(long)upTimer.Elapsed.TotalSeconds} nodes={nodesToRead.Count}");
                    }
                }
            }
            while (await timer.WaitForNextTickAsync(cts.Token));
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C during the wait — normal shutdown.
        }

        if (session is not null)
        {
            try { await session.CloseAsync(CancellationToken.None); } catch { /* best effort */ }
            session.Dispose();
        }
        foreach (NodeCsvSink sink in sinks)
        {
            sink.Dispose();
        }
        Console.WriteLine($"Done. {rowCount} row(s) written per node.");
        return 0;
    }

    private static async Task<ApplicationConfiguration> BuildApplicationConfigurationAsync(LoggerConfig config)
    {
        var appConfig = new ApplicationConfiguration
        {
            ApplicationName = ApplicationName,
            ApplicationUri = Utils.Format("urn:{0}:{1}", System.Net.Dns.GetHostName(), ApplicationName),
            ApplicationType = ApplicationType.Client,
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = "pki/own",
                    SubjectName = $"CN={ApplicationName}",
                },
                TrustedIssuerCertificates = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = "pki/issuer" },
                TrustedPeerCertificates = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = "pki/trusted" },
                RejectedCertificateStore = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = "pki/rejected" },
                AutoAcceptUntrustedCertificates = config.AutoAcceptServerCertificate,
                AddAppCertToTrustedStore = true,
            },
            TransportQuotas = new TransportQuotas { OperationTimeout = 15000 },
            ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60000 },
            TraceConfiguration = new TraceConfiguration(),
        };
        await appConfig.ValidateAsync(ApplicationType.Client);

        appConfig.CertificateValidator.CertificateValidation += (_, e) =>
        {
            if (config.AutoAcceptServerCertificate)
            {
                // The validator rejects unsuppressible errors (revoked/forged certs) before this event
                // fires, so AcceptAll only ever covers the suppressible set (untrusted, time-invalid, ...).
                e.AcceptAll = true;
                Console.WriteLine($"Accepted server certificate despite {e.Error.StatusCode}: {e.Certificate.Subject}");
            }
        };

#pragma warning disable CS0618 // non-obsolete replacement requires the new ITelemetryContext plumbing; package pinned to 1.5.378
        var application = new ApplicationInstance
        {
            ApplicationName = ApplicationName,
            ApplicationType = ApplicationType.Client,
            ApplicationConfiguration = appConfig,
        };
#pragma warning restore CS0618
        await application.CheckApplicationInstanceCertificatesAsync(silent: true);
        return appConfig;
    }

    private static async Task<Session> ConnectAsync(ApplicationConfiguration appConfig, LoggerConfig config, CancellationToken ct)
    {
        Console.WriteLine($"Connecting to {config.EndpointUrl} (security: {(config.UseSecurity ? "best available" : "none")})...");
#pragma warning disable CS0618 // non-obsolete replacement requires the new ITelemetryContext plumbing; package pinned to 1.5.378
        EndpointDescription endpointDescription = await CoreClientUtils.SelectEndpointAsync(appConfig, config.EndpointUrl, config.UseSecurity, 15000, ct);
#pragma warning restore CS0618
        var endpoint = new ConfiguredEndpoint(null, endpointDescription, EndpointConfiguration.Create(appConfig));
        IUserIdentity identity = string.IsNullOrEmpty(config.Username)
            ? new UserIdentity()
            : new UserIdentity(config.Username, Encoding.UTF8.GetBytes(config.Password ?? string.Empty));

#pragma warning disable CS0618 // non-obsolete replacement requires the new ITelemetryContext plumbing; package pinned to 1.5.378
        Session session = await Session.Create(
            appConfig, endpoint,
            updateBeforeConnect: false,
            checkDomain: false,
            sessionName: ApplicationName,
            sessionTimeout: 60000,
            identity: identity,
            preferredLocales: null,
            ct: ct);
#pragma warning restore CS0618

        Console.WriteLine($"Connected to {endpointDescription.Server.ApplicationName}.");
        return session;
    }

    private static string AppVersion()
    {
        Version? v = typeof(Program).Assembly.GetName().Version;
        return v is null ? "v?" : $"v{v.Major}.{v.Minor}";
    }

    // Rounded to the whole second so the column is always yyyy-MM-dd HH:mm:ss.000.
    private static string RowTimestamp()
    {
        DateTime now = DateTime.Now.AddMilliseconds(500);
        DateTime stamp = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerSecond));
        return stamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
    }

    // Column labels derived from the first received value: scalars get a single "value"
    // column, arrays one column per element ("[0]", "[1]", ...), matrices one column per
    // element in row-major order ("[0][0]", "[0][1]", ...).
    private static string[] ColumnLabels(object value) => value switch
    {
        Matrix matrix => MatrixLabels(matrix.Dimensions),
        Array array => [.. Enumerable.Range(0, array.Length).Select(i => $"[{i}]")],
        _ => ["value"],
    };

    private static string[] MatrixLabels(int[] dimensions)
    {
        int total = 1;
        foreach (int d in dimensions)
        {
            total *= d;
        }
        if (dimensions.Length == 0 || total <= 0)
        {
            return [];
        }
        var labels = new string[total];
        var index = new int[dimensions.Length];
        for (int n = 0; n < total; n++)
        {
            labels[n] = string.Concat(index.Select(i => $"[{i}]"));
            for (int d = dimensions.Length - 1; d >= 0; d--)
            {
                if (++index[d] < dimensions[d])
                {
                    break;
                }
                index[d] = 0;
            }
        }
        return labels;
    }

    // Exactly columnCount cells: missing elements become empty cells, extras are dropped.
    private static string[] ValueCells(object? value, int columnCount, out int actualCount)
    {
        object?[] elements = value switch
        {
            null => [],
            Matrix matrix => [.. matrix.Elements.Cast<object?>()],
            Array array => [.. array.Cast<object?>()],
            _ => [value],
        };
        actualCount = elements.Length;
        var cells = new string[columnCount];
        for (int i = 0; i < columnCount; i++)
        {
            cells[i] = i < elements.Length ? FormatValue(elements[i]) : string.Empty;
        }
        return cells;
    }

    private static string SanitizeForFileName(string nodeId)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return new string([.. nodeId.Select(c => invalid.Contains(c) ? '_' : c)]);
    }

    // One CSV file per node. The file (and its header) is created on the node's first
    // received value, because array/matrix column counts are only known at runtime.
    private sealed class NodeCsvSink : IDisposable
    {
        private readonly string _nodeIdText;
        private readonly string _path;
        private StreamWriter? _writer;
        private int _valueColumnCount;
        private bool _disabled;
        private bool _shapeWarned;
        private bool _noValueWarned;
        private bool _failing;
        private long _lostRows;
        private int _consecutiveFailures;
        private DateTime _lastFailureLog;

        private NodeCsvSink(string nodeIdText, string path)
        {
            _nodeIdText = nodeIdText;
            _path = path;
        }

        public static NodeCsvSink[] CreateAll(string csvPath, string[] nodeIds)
        {
            string fullPath = Path.GetFullPath(csvPath);
            string dir = Path.GetDirectoryName(fullPath)!;
            string baseName = Path.GetFileName(fullPath);
            // A bare file name (the normal case) goes into a dedicated data\ folder;
            // an explicit directory in CsvPath is respected as-is.
            if (string.IsNullOrEmpty(Path.GetDirectoryName(csvPath)))
            {
                dir = Path.Combine(dir, "data");
            }
            Directory.CreateDirectory(dir);

            NodeCsvSink[] sinks = [.. nodeIds.Select(id =>
                new NodeCsvSink(id, Path.Combine(dir, $"{SanitizeForFileName(id)}_{baseName}")))];

            var duplicate = sinks.GroupBy(s => s._path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(g => g.Count() > 1);
            if (duplicate is not null)
            {
                throw new InvalidOperationException($"Two NodeIds map to the same CSV file name: {duplicate.Key}");
            }
            return sinks;
        }

        public void Write(string timestamp, DataValue? result)
        {
            if (_disabled)
            {
                return;
            }
            try
            {
                WriteCore(timestamp, result);
                if (_failing)
                {
                    Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] {_nodeIdText}: CSV writing recovered — {_lostRows} row(s) were lost.");
                    _failing = false;
                    _lostRows = 0;
                }
                _consecutiveFailures = 0;
            }
            catch (HeaderMismatchException ex)
            {
                _disabled = true;
                Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] {_nodeIdText}: {ex.Message} This node will NOT be logged until restart.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                _lostRows++;
                _consecutiveFailures++;
                if (!_failing)
                {
                    _failing = true;
                    _lastFailureLog = DateTime.Now;
                    Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] {_nodeIdText}: CSV write failed: {ex.Message} — row lost (repeats are summarized once per minute).");
                }
                else if (DateTime.Now - _lastFailureLog >= TimeSpan.FromMinutes(1))
                {
                    _lastFailureLog = DateTime.Now;
                    Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] {_nodeIdText}: CSV writes still failing ({_lostRows} row(s) lost so far): {ex.Message}");
                }
                if (_consecutiveFailures >= 5 && _writer is not null)
                {
                    // A stale handle (network share / removable drive) never recovers by itself:
                    // drop the writer so the next tick reopens the file from scratch.
                    try { _writer.Dispose(); } catch { /* already broken */ }
                    _writer = null;
                }
            }
        }

        private void WriteCore(string timestamp, DataValue? result)
        {
            object? value = result?.Value;
            if (_writer is null)
            {
                if (value is null)
                {
                    if (!_noValueWarned && result is not null)
                    {
                        _noValueWarned = true;
                        Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] {_nodeIdText}: no value yet (status {FormatStatus(result.StatusCode)}) — its CSV will be created on the first good read.");
                    }
                    return;
                }
                Open(value);
            }

            string[] cells = ValueCells(value, _valueColumnCount, out int actualCount);
            if (value is not null && actualCount != _valueColumnCount && !_shapeWarned)
            {
                _shapeWarned = true;
                Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] {_nodeIdText}: value now has {actualCount} element(s) but {Path.GetFileName(_path)} has {_valueColumnCount} value column(s) — extras are dropped, missing cells left empty. Rename the file and restart to re-create the columns.");
            }
            string status = result is null ? "NoData" : FormatStatus(result.StatusCode);
            var fields = new List<string>(cells.Length + 2) { timestamp, CsvEscape(status) };
            fields.AddRange(cells.Select(CsvEscape));
            _writer!.WriteLine(string.Join(",", fields));
        }

        private void Open(object firstValue)
        {
            string[] labels = ColumnLabels(firstValue);
            string header = labels.Length > 0
                ? $"timestamp,status,{string.Join(",", labels.Select(CsvEscape))}"
                : "timestamp,status";

            bool isNewFile = !File.Exists(_path) || new FileInfo(_path).Length == 0;
            bool needsNewline = false;
            if (!isNewFile)
            {
                using (var reader = new StreamReader(_path))
                {
                    if (reader.ReadLine() != header)
                    {
                        throw new HeaderMismatchException(
                            $"{Path.GetFileName(_path)} was created with different columns (the value shape changed). Delete or rename it.");
                    }
                }
                // A crash/power loss can leave a partial last row; terminate it so rows never merge.
                using var tail = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (tail.Length > 0)
                {
                    tail.Seek(-1, SeekOrigin.End);
                    needsNewline = tail.ReadByte() != '\n';
                }
            }

            // Publish to fields only after the header is safely on disk, so a failed open is
            // fully rolled back and simply retried on the next tick.
            FileStream? stream = null;
            StreamWriter? writer = null;
            try
            {
                stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
                writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: isNewFile)) { AutoFlush = true };
                stream = null;
                if (isNewFile)
                {
                    writer.WriteLine(header);
                }
                else if (needsNewline)
                {
                    writer.WriteLine();
                }
            }
            catch
            {
                writer?.Dispose();
                stream?.Dispose();
                if (isNewFile)
                {
                    try { File.Delete(_path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
                throw;
            }
            _writer = writer;
            _valueColumnCount = labels.Length;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {_nodeIdText} -> {Path.GetFileName(_path)} ({labels.Length} value column(s)).");
        }

        public void Dispose()
        {
            _writer?.Dispose();
        }

        // Distinct type so the disable-forever decision can never be triggered by stream
        // failures (ObjectDisposedException derives from InvalidOperationException).
        private sealed class HeaderMismatchException(string message) : Exception(message);
    }

    private static string Describe(DataValueCollection results) =>
        string.Join(" | ", results.Select(r => $"{FormatValue(r?.Value)} ({(r is null ? "NoData" : FormatStatus(r.StatusCode))})"));

    private static string FormatValue(object? value) => value switch
    {
        null => string.Empty,
        // Multi-dimensional PLC arrays arrive as Opc.Ua.Matrix, which is not a System.Array.
        Matrix matrix => FormatValue(matrix.Elements),
        Array array => string.Join(";", array.Cast<object?>().Select(FormatValue)),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static string FormatStatus(StatusCode status)
    {
        // StatusCode.ToString() renders raw hex; the symbolic lookup gives "Good", "BadNodeIdUnknown", ...
        string symbol = status.SymbolicId;
        if (!string.IsNullOrEmpty(symbol))
        {
            return symbol;
        }
        if (StatusCode.IsGood(status))
        {
            return "Good";
        }
        return (StatusCode.IsUncertain(status) ? "Uncertain_0x" : "Bad_0x")
            + status.Code.ToString("X8", CultureInfo.InvariantCulture);
    }

    private static string CsvEscape(string field) =>
        field.Contains(',') || field.Contains('"') || field.Contains('\n') || field.Contains('\r')
            ? $"\"{field.Replace("\"", "\"\"")}\""
            : field;
}
