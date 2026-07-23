using Microsoft.Data.Sqlite;

namespace Pako.SNIProxy.Persistence;

public sealed class TrafficStore : IDisposable
{
    private readonly string _connectionString;
    private readonly object _writeLock = new();
    private readonly object _readLock = new();
    private SqliteConnection? _writeConnection;
    private SqliteConnection? _readConnection;

    public TrafficStore(string dbPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public void Initialize()
    {
        _writeConnection = new SqliteConnection(_connectionString);
        _writeConnection.Open();

        using var cmd = _writeConnection.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA wal_autocheckpoint=1000;
            CREATE TABLE IF NOT EXISTS traffic (
                client_ip   TEXT NOT NULL,
                period_type TEXT NOT NULL,
                period_key  TEXT NOT NULL,
                bytes       INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (client_ip, period_type, period_key)
            );
            CREATE INDEX IF NOT EXISTS idx_traffic_period ON traffic(period_type, period_key);
            """;
        cmd.ExecuteNonQuery();

        // Separate read connection so quota lookups never block writes (WAL allows concurrent read).
        _readConnection = new SqliteConnection(_connectionString);
        _readConnection.Open();
    }

    public void AddBytes(string clientIp, string dayKey, string monthKey, long bytes)
    {
        if (bytes <= 0 || _writeConnection is null)
            return;

        lock (_writeLock)
        {
            using var tx = _writeConnection.BeginTransaction();
            Upsert(clientIp, "day", dayKey, bytes, tx);
            Upsert(clientIp, "month", monthKey, bytes, tx);
            tx.Commit();
        }
    }

    private void Upsert(string clientIp, string periodType, string periodKey, long bytes, SqliteTransaction tx)
    {
        using var cmd = _writeConnection!.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO traffic (client_ip, period_type, period_key, bytes)
            VALUES ($ip, $type, $key, $bytes)
            ON CONFLICT(client_ip, period_type, period_key)
            DO UPDATE SET bytes = bytes + $bytes;
            """;
        cmd.Parameters.AddWithValue("$ip", clientIp);
        cmd.Parameters.AddWithValue("$type", periodType);
        cmd.Parameters.AddWithValue("$key", periodKey);
        cmd.Parameters.AddWithValue("$bytes", bytes);
        cmd.ExecuteNonQuery();
    }

    public long GetBytes(string clientIp, string periodType, string periodKey)
    {
        if (_readConnection is null)
            return 0;

        lock (_readLock)
        {
            using var cmd = _readConnection.CreateCommand();
            cmd.CommandText = "SELECT bytes FROM traffic WHERE client_ip=$ip AND period_type=$type AND period_key=$key;";
            cmd.Parameters.AddWithValue("$ip", clientIp);
            cmd.Parameters.AddWithValue("$type", periodType);
            cmd.Parameters.AddWithValue("$key", periodKey);
            var result = cmd.ExecuteScalar();
            return result is null or DBNull ? 0 : Convert.ToInt64(result);
        }
    }

    public void Checkpoint()
    {
        if (_writeConnection is null)
            return;

        lock (_writeLock)
        {
            using var cmd = _writeConnection.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            cmd.ExecuteNonQuery();
        }
    }

    public void CleanupOldRecords(int keepDays)
    {
        if (_writeConnection is null)
            return;

        lock (_writeLock)
        {
            var cutoffDay = DateTime.UtcNow.AddDays(-keepDays).ToString("yyyy-MM-dd");
            var cutoffMonth = DateTime.UtcNow.AddMonths(-3).ToString("yyyy-MM");

            using var cmd = _writeConnection.CreateCommand();
            cmd.CommandText = """
                DELETE FROM traffic WHERE (period_type='day' AND period_key < $day)
                                     OR (period_type='month' AND period_key < $month);
                """;
            cmd.Parameters.AddWithValue("$day", cutoffDay);
            cmd.Parameters.AddWithValue("$month", cutoffMonth);
            cmd.ExecuteNonQuery();
        }
    }

    public void Dispose()
    {
        _readConnection?.Dispose();
        _writeConnection?.Dispose();
        _readConnection = null;
        _writeConnection = null;
    }
}
