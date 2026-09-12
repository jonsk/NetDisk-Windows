// 本地状态库(SQLite)+ schema 版本化(DE-D-07)。
//
// 这一层的所有设计都围绕一件事:**状态库是"本地唯一的真相",它坏了 = 用户要重新同步**。
// 所以规矩比"能建表"多得多:
//
// ①**迁移只增不删**(由 <see cref="Migration"/> 的构造校验强制):不允许 DROP /
//   TRUNCATE / DELETE FROM / RENAME COLUMN。理由不是洁癖 —— 桌面端一定会有"用户装了
//   新版、又回退到旧版"的情况(MSI 回滚、企业灰度、用户手动降级)。只增的 schema 让
//   旧版本代码仍能打开新版本写的库并读到数据(可回滚);而一旦某次迁移删了列,
//   回退后的旧客户端就会在**启动时**炸,用户看到的是"网盘打不开了"。
// ②**查询必须显式列名,永远不写 `SELECT *`**:这是①的另一半。旧代码面对新库时,
//   只有"按名字取自己认识的那几列"才能正常工作(SQLite 允许未知列存在)。
// ③**每条迁移一个事务,`user_version` 在 SQL 成功之后才写**:顺序反了的话,迁移失败
//   会留下"版本说迁移过了、实际只建了一半表"的库 —— 下次启动直接跳过迁移,
//   之后所有的查询都在一个残缺的 schema 上跑。
// ④**遇到"比代码新的库"要能识别**:正常打开(只增 schema 可以读),但把
//   <see cref="SchemaNewerThanCode"/> 置位,便于上层提示"该库由更新版本的客户端写过"。
// ⑤**WAL + busy_timeout**:状态库同时被 watcher 线程、同步线程与 UI 读,默认的
//   rollback journal 会让"读写并发"直接报 database is locked。

using Microsoft.Data.Sqlite;

namespace NetDisk.SyncEngine;

/// <summary>一条 schema 迁移(只增不删;构造时会做破坏性语句校验)。</summary>
public sealed record Migration
{
    public Migration(int version, string name, string sql)
    {
        if (version <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(version), "迁移版本必须从 1 开始");
        }
        Version = version;
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Sql = sql ?? throw new ArgumentNullException(nameof(sql));
        EnsureAdditiveOnly(Sql, version);
    }

    public int Version { get; }

    public string Name { get; }

    public string Sql { get; }

    /// <summary>
    /// 破坏性语句校验(纪律①)。**在构造时**就拒绝,而不是靠代码审查 ——
    /// 迁移是"写了就再也不会改"的代码,一旦带着 DROP 合进去,回退路径就永久坏了。
    /// </summary>
    private static void EnsureAdditiveOnly(string sql, int version)
    {
        // 只查"会丢数据/丢结构"的语句。注意 `DROP INDEX` 不在其中:重建索引不丢状态。
        string[] forbidden = { "DROP TABLE", "DROP COLUMN", "TRUNCATE", "DELETE FROM", "RENAME COLUMN", "RENAME TO" };
        foreach (var f in forbidden)
        {
            if (sql.Contains(f, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"迁移 v{version} 含破坏性语句 {f}:迁移只能「加」,不能「减」 —— " +
                    "否则用户回退到旧版客户端后会打不开状态库(见 StateStore 顶部说明)");
            }
        }
    }
}

/// <summary>状态库版本异常。</summary>
public sealed class StateSchemaException : Exception
{
    public StateSchemaException(string message) : base(message)
    {
    }
}

/// <summary>本地状态库(SQLite)。</summary>
public sealed class StateStore : IDisposable
{
    /// <summary>当前代码支持的最高版本(由迁移表推导,不手写常量以免两处漂移)。</summary>
    public static int LatestVersionOf(IReadOnlyList<Migration> migrations)
        => migrations.Count == 0 ? 0 : migrations.Max(m => m.Version);

    /// <summary>
    /// 内置迁移(只增)。
    ///
    /// v1:同步状态 / 期望变更(自触发销账)/ 空间游标 —— 这三张表是 DE-D-08~13 的共同底座。
    /// v2:给 sync_state 加"远端已消失待确认"与"退避到什么时候"两列(纯 ADD COLUMN,
    ///     带默认值 —— SQLite 的 ADD COLUMN 要求 NOT NULL 列必须有默认值)。
    /// </summary>
    public static readonly IReadOnlyList<Migration> DefaultMigrations = new[]
    {
        new Migration(1, "sync-state-and-cursors", @"
CREATE TABLE IF NOT EXISTS sync_state (
    file_id           TEXT PRIMARY KEY,
    space_id          TEXT NOT NULL,
    local_path        TEXT NOT NULL,
    remote_version    INTEGER NOT NULL DEFAULT 0,
    local_mtime_ticks INTEGER NOT NULL DEFAULT 0,
    size              INTEGER NOT NULL DEFAULT 0,
    hash_sha256       TEXT NOT NULL DEFAULT '',
    state             TEXT NOT NULL DEFAULT 'Synced',
    updated_at_utc    TEXT NOT NULL DEFAULT ''
);
CREATE INDEX IF NOT EXISTS ix_sync_state_space ON sync_state(space_id);
CREATE INDEX IF NOT EXISTS ix_sync_state_path  ON sync_state(local_path);

CREATE TABLE IF NOT EXISTS expected_changes (
    id             INTEGER PRIMARY KEY AUTOINCREMENT,
    local_path     TEXT NOT NULL,
    op             TEXT NOT NULL,
    file_id        TEXT NOT NULL DEFAULT '',
    created_at_utc TEXT NOT NULL DEFAULT ''
);
CREATE INDEX IF NOT EXISTS ix_expected_changes_path ON expected_changes(local_path);

CREATE TABLE IF NOT EXISTS cursors (
    space_id       TEXT PRIMARY KEY,
    last_seq       INTEGER NOT NULL DEFAULT 0,
    updated_at_utc TEXT NOT NULL DEFAULT ''
);"),
        new Migration(2, "sync-state-pending-and-backoff", @"
ALTER TABLE sync_state ADD COLUMN pending_remote_gone INTEGER NOT NULL DEFAULT 0;
ALTER TABLE sync_state ADD COLUMN retry_after_utc TEXT NOT NULL DEFAULT '';"),
    };

    private StateStore(SqliteConnection conn, int schemaVersion, bool newerThanCode)
    {
        Connection = conn;
        SchemaVersion = schemaVersion;
        SchemaNewerThanCode = newerThanCode;
    }

    public SqliteConnection Connection { get; }

    /// <summary>打开时从库里读到的 schema 版本。</summary>
    public int SchemaVersion { get; }

    /// <summary>库由**更新版本**的客户端写过(只增 schema 仍可读,但值得告知用户)。</summary>
    public bool SchemaNewerThanCode { get; }

    /// <summary>打开(必要时迁移)状态库。</summary>
    /// <param name="path">库文件路径(<c>:memory:</c> 也可,便于测试)。</param>
    /// <param name="migrations">迁移表;默认 <see cref="DefaultMigrations"/>。</param>
    public static StateStore Open(string path, IReadOnlyList<Migration>? migrations = null)
    {
        migrations ??= DefaultMigrations;
        var ordered = migrations.OrderBy(m => m.Version).ToList();
        var codeVersion = LatestVersionOf(ordered);

        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // 连接池会持有文件句柄,导致"关掉 App 后库文件仍被占用"(测试里表现为删不掉)
            Pooling = false,
        }.ToString());
        conn.Open();
        ApplyPragmas(conn);

        var dbVersion = ReadUserVersion(conn);
        var newer = dbVersion > codeVersion;
        if (!newer)
        {
            foreach (var m in ordered.Where(m => m.Version > dbVersion))
            {
                ApplyOne(conn, m);
            }
            dbVersion = ReadUserVersion(conn);
        }
        return new StateStore(conn, dbVersion, newer);
    }

    private static void ApplyPragmas(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
PRAGMA journal_mode = WAL;
PRAGMA synchronous = NORMAL;
PRAGMA foreign_keys = ON;
PRAGMA busy_timeout = 5000;";
        cmd.ExecuteNonQuery();
    }

    private static int ReadUserVersion(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static void ApplyOne(SqliteConnection conn, Migration m)
    {
        using var tx = conn.BeginTransaction();
        try
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = m.Sql;
                cmd.ExecuteNonQuery();
            }
            using (var ver = conn.CreateCommand())
            {
                ver.Transaction = tx;
                // PRAGMA 不支持参数化,但这里是 int 且来自我们自己的迁移表(不是外部输入)
                ver.CommandText = $"PRAGMA user_version = {m.Version};";
                ver.ExecuteNonQuery();
            }
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            // 关键:失败的迁移**不写版本号**(纪律③)。版本号与 SQL 在同一个事务里,
            // 所以回滚之后库仍停在旧版本,下次启动会重新尝试这条迁移。
            throw;
        }
    }

    // ---- 薄封装(后续条目用;刻意都要求显式列名,见纪律②)----

    public void Execute(string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args)
        {
            cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        }
        cmd.ExecuteNonQuery();
    }

    public object? Scalar(string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args)
        {
            cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        }
        var result = cmd.ExecuteScalar();
        return result is DBNull ? null : result;
    }

    public void Dispose() => Connection.Dispose();
}
