using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Utils;


namespace SPT_EasyChecker_Server;

public class SqLiteConfigure
{
    private sealed record AccessEvent(
        string EventType,
        string Decision,
        string SessionId,
        string? ProfileId,
        string? IpAddress,
        string? ForwardedFor,
        string? Method,
        string? Path,
        string? QueryString,
        string? Host,
        string? UserAgent,
        string? TraceId,
        string? Reason);
    
    
    private const int MaxTextLength = 2048;
    
    private static ISptLogger<EasyCheckerBootstrap>? _logger;
    
    private const string DatabaseFileName = "data.sqlite";
    
    private static readonly Lock SyncRoot = new();
    
    private static string? _connectionString;
    
    private static bool _initialized;

    /**
     * 配置
     */
    public static void Configure(ISptLogger<EasyCheckerBootstrap> logger)
    {
        _logger = logger;

        // 数据库放在当前服务端模组 DLL 所在目录，方便随模组一起备份/迁移。
        var assemblyDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        var databaseDirectory = string.IsNullOrWhiteSpace(assemblyDirectory)
            ? AppContext.BaseDirectory
            : assemblyDirectory;

        // 创建指定路径中的所有目录和子目录，除非它们已经存在。
        Directory.CreateDirectory(databaseDirectory);

        // 确认数据库路径
        var databasePath = Path.Combine(databaseDirectory, DatabaseFileName);

        // 初始化连接字符串
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();


        lock (SyncRoot)
        {
            if (_initialized) return;

            using var connection = OpenConnection();

            ExecuteNonQuery(connection, "PRAGMA journal_mode=WAL;");
            ExecuteNonQuery(connection, "PRAGMA busy_timeout=5000;");
            ExecuteNonQuery(
                connection,
                """
                CREATE TABLE IF NOT EXISTS access_events (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    created_at TEXT NOT NULL,
                    event_type TEXT NOT NULL,
                    decision TEXT NOT NULL,
                    session_id TEXT NOT NULL,
                    profile_id TEXT NULL,
                    ip_address TEXT NULL,
                    forwarded_for TEXT NULL,
                    method TEXT NULL,
                    path TEXT NULL,
                    query_string TEXT NULL,
                    host TEXT NULL,
                    user_agent TEXT NULL,
                    trace_id TEXT NULL,
                    reason TEXT NULL
                );
                """);

            ExecuteNonQuery(connection,
                "CREATE INDEX IF NOT EXISTS ix_access_events_created_at ON access_events(created_at);");
            ExecuteNonQuery(connection,
                "CREATE INDEX IF NOT EXISTS ix_access_events_session_id ON access_events(session_id);");
            ExecuteNonQuery(connection,
                "CREATE INDEX IF NOT EXISTS ix_access_events_profile_id ON access_events(profile_id);");
            ExecuteNonQuery(connection,
                "CREATE INDEX IF NOT EXISTS ix_access_events_ip_address ON access_events(ip_address);");

            _initialized = true;
        }
    }
    
    /**
     * 执行非查询
     */
    private static void ExecuteNonQuery(SqliteConnection connection, string commandText)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }
    
    /**
     * 打开连接
     */
    private static SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
    
    /**
     * 添加NULL
     */
    private static void AddNullable(SqliteCommand command, string name, string? value)
    {
        command.Parameters.AddWithValue(name, string.IsNullOrWhiteSpace(value) ? DBNull.Value : value);
    }
    
    /**
     * 执行插入请求
     */
    private static void Enqueue(AccessEvent accessEvent)
    {
        // 拦截补丁运行在请求线程上，写库失败不能影响游戏请求处理，所以这里用后台任务并在内部吞掉异常。
        _ = Task.Run(() => Insert(accessEvent));
    }
    
    /**
     * 执行插入
     */
     private static void Insert(AccessEvent accessEvent)
    {
        try
        {
            if (!_initialized || string.IsNullOrWhiteSpace(_connectionString)) return;

            lock (SyncRoot)
            {
                using var connection = OpenConnection();
                using var command = connection.CreateCommand();

                command.CommandText =
                    """
                    INSERT INTO access_events (
                        created_at,
                        event_type,
                        decision,
                        session_id,
                        profile_id,
                        ip_address,
                        forwarded_for,
                        method,
                        path,
                        query_string,
                        host,
                        user_agent,
                        trace_id,
                        reason
                    ) VALUES (
                        $created_at,
                        $event_type,
                        $decision,
                        $session_id,
                        $profile_id,
                        $ip_address,
                        $forwarded_for,
                        $method,
                        $path,
                        $query_string,
                        $host,
                        $user_agent,
                        $trace_id,
                        $reason
                    );
                    """;

                command.Parameters.AddWithValue("$created_at", DateTimeOffset.UtcNow.ToString("O"));
                command.Parameters.AddWithValue("$event_type", accessEvent.EventType);
                command.Parameters.AddWithValue("$decision", accessEvent.Decision);
                command.Parameters.AddWithValue("$session_id", accessEvent.SessionId);
                AddNullable(command, "$profile_id", accessEvent.ProfileId);
                AddNullable(command, "$ip_address", accessEvent.IpAddress);
                AddNullable(command, "$forwarded_for", accessEvent.ForwardedFor);
                AddNullable(command, "$method", accessEvent.Method);
                AddNullable(command, "$path", accessEvent.Path);
                AddNullable(command, "$query_string", accessEvent.QueryString);
                AddNullable(command, "$host", accessEvent.Host);
                AddNullable(command, "$user_agent", accessEvent.UserAgent);
                AddNullable(command, "$trace_id", accessEvent.TraceId);
                AddNullable(command, "$reason", accessEvent.Reason);

                command.ExecuteNonQuery();
            }
        }
        catch (Exception exception)
        {
            _logger?.Warning($"[MilkAntiCheatExpert] Failed to write SQLite audit event: {exception.Message}");
        }
    }
     
    /**
     * 处理请求头
     */
    private static string? ReadHeader(HttpContext context, string name)
    {
        return context.Request.Headers.TryGetValue(name, out var value) ? value.ToString() : null;
    }
    
    /**
     * 处理请求
     */
    private static string? ReadQuery(HttpContext context, string name)
    {
        return context.Request.Query.TryGetValue(name, out var value) ? value.ToString() : null;
    }
    
    /**
     * 记录请求
     */
    public static void RecordHttpRequest(MongoId sessionId, HttpContext context, string decision, string? reason = null)
    {
        var sessionKey = sessionId.ToString();

        Enqueue(
            new AccessEvent(
                EventType: "http_request",
                Decision: decision,
                SessionId: sessionKey,
                ProfileId: ResolveProfileId(sessionKey, context),
                IpAddress: Limit(context.Connection.RemoteIpAddress?.ToString()),
                ForwardedFor: Limit(ReadHeader(context, "X-Forwarded-For")),
                Method: Limit(context.Request.Method),
                Path: Limit(context.Request.Path.Value),
                QueryString: Limit(context.Request.QueryString.Value),
                Host: Limit(context.Request.Host.Value),
                UserAgent: Limit(ReadHeader(context, "User-Agent")),
                TraceId: Limit(context.TraceIdentifier),
                Reason: Limit(reason)));
    }
    
    /**
     * 限制
     */
    private static string? Limit(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var trimmed = value.Trim();
        return trimmed.Length <= MaxTextLength ? trimmed : trimmed[..MaxTextLength];
    }
    
    /**
     * 处理请求存档Id
     */
    private static string? ResolveProfileId(string sessionId, HttpContext context)
    {
        // SPT 的 MongoId sessionId 通常就是玩家 profile id；如果代理或客户端额外传了 profile 头/查询参数，则优先记录它。
        var profileId =
            ReadHeader(context, "X-Profile-Id")
            ?? ReadHeader(context, "Profile-Id")
            ?? ReadQuery(context, "profileId")
            ?? ReadQuery(context, "profile_id")
            ?? sessionId;

        return Limit(profileId);
    }


}