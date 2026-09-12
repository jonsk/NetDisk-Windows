// DE-D-07 行为检查器:本地状态库的 schema 版本化。
//
// 用法:`dotnet run --project desktop/tests/StateStoreCheck -c Release`
//
// 三句验收("升级不丢态、不重建;可回滚")在这里被拆成可执行的断言:
//   - **不丢态**:v1 库里写的行,升到 v2 之后必须还在(且内容一致);
//   - **不重建**:连续打开两次不会重新建表/清空;
//   - **可回滚**:用旧版客户端的迁移表(v1)打开 v2 库,仍能读到数据 ——
//     这依赖两条纪律(迁移只增不删 + 查询显式列名),两条都在这里被钉住。
// 另外两条"会静默毁数据"的路径也要断言:破坏性迁移必须在**构造时**被拒;
// 迁移失败**不能写版本号**(否则下次启动会跳过迁移,在一个残缺 schema 上跑)。

using NetDisk.SyncEngine;

var checks = new List<(string Name, Func<Task> Run)>
{
    ("① 全新库:迁移到最新版本且三张表就位", CheckFreshInstallAsync),
    ("② 升级不丢态:v1 的数据在 v2 库中完好", CheckUpgradeKeepsDataAsync),
    ("③ 可回滚:旧版(v1)迁移表能打开并读 v2 库", CheckRollbackReadAsync),
    ("④ 比代码新的库被识别(不是当成当前版本)", CheckNewerSchemaDetectedAsync),
    ("⑤ 破坏性迁移在构造时被拒(DROP/DELETE/RENAME)", CheckDestructiveMigrationRejectedAsync),
    ("⑥ 迁移失败不写版本号(不留下半截 schema)", CheckFailedMigrationKeepsVersionAsync),
    ("⑦ WAL + busy_timeout + foreign_keys 已生效", CheckPragmasAsync),
    ("⑧ 重复打开是幂等的(不重建、不清空)", CheckIdempotentOpenAsync),
    ("⑨ 升级后新增列可用且旧行取到默认值", CheckNewColumnsAsync),
    ("⑩ 两个连接共用同一文件(WAL 下读写不互锁)", CheckConcurrentConnectionsAsync),
};

var failed = 0;
foreach (var (name, run) in checks)
{
    try
    {
        await run();
        Console.WriteLine($"✓ {name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.Error.WriteLine($"✗ {name}: {ex.Message}");
    }
}

Console.WriteLine();
if (failed == 0)
{
    Console.WriteLine($"全部通过:{checks.Count} 项 DE-D-07 行为断言(状态库 schema 版本化)");
    return 0;
}
Console.Error.WriteLine($"{failed}/{checks.Count} 项失败");
return 1;

// ---------------------------------------------------------------- 检查

static Task CheckFreshInstallAsync()
{
    WithTempDb(path =>
    {
        using var store = StateStore.Open(path);
        Assert(store.SchemaVersion == StateStore.LatestVersionOf(StateStore.DefaultMigrations),
            $"全新库应到最新版本,实际 v{store.SchemaVersion}");
        foreach (var t in new[] { "sync_state", "expected_changes", "cursors" })
        {
            Assert(TableExists(store, t), $"表 {t} 应当存在");
        }
    });
    return Task.CompletedTask;
}

static Task CheckUpgradeKeepsDataAsync()
{
    WithTempDb(path =>
    {
        var v1Only = StateStore.DefaultMigrations.Where(m => m.Version == 1).ToList();
        using (var old = StateStore.Open(path, v1Only))
        {
            Assert(old.SchemaVersion == 1, $"旧版应停在 v1,实际 v{old.SchemaVersion}");
            old.Execute(
                "INSERT INTO sync_state (file_id, space_id, local_path, remote_version, size, state) " +
                "VALUES ($f, $s, $p, $v, $z, $st)",
                ("$f", "file-1"), ("$s", "space-1"), ("$p", @"C:\sync\a.txt"),
                ("$v", 7L), ("$z", 123L), ("$st", "Synced"));
            old.Execute("INSERT INTO cursors (space_id, last_seq) VALUES ($s, $q)",
                ("$s", "space-1"), ("$q", 42L));
        }

        using var upgraded = StateStore.Open(path); // 完整迁移表 → 升到 v2
        Assert(upgraded.SchemaVersion == StateStore.LatestVersionOf(StateStore.DefaultMigrations),
            $"升级后应为最新版本,实际 v{upgraded.SchemaVersion}");
        Assert(Convert.ToInt64(upgraded.Scalar(
            "SELECT remote_version FROM sync_state WHERE file_id = $f", ("$f", "file-1"))) == 7L,
            "升级后原有的 remote_version 必须保持不变");
        Assert(Convert.ToString(upgraded.Scalar(
            "SELECT local_path FROM sync_state WHERE file_id = $f", ("$f", "file-1"))) == @"C:\sync\a.txt",
            "升级后原有的 local_path 必须保持不变");
        Assert(Convert.ToInt64(upgraded.Scalar(
            "SELECT last_seq FROM cursors WHERE space_id = $s", ("$s", "space-1"))) == 42L,
            "游标也必须在升级后保持");
    });
    return Task.CompletedTask;
}

static Task CheckRollbackReadAsync()
{
    WithTempDb(path =>
    {
        // 新版写数据(v2)
        using (var latest = StateStore.Open(path))
        {
            latest.Execute(
                "INSERT INTO sync_state (file_id, space_id, local_path, remote_version, pending_remote_gone) " +
                "VALUES ($f, $s, $p, $v, 1)",
                ("$f", "f2"), ("$s", "s2"), ("$p", @"C:\sync\b.txt"), ("$v", 3L));
        }

        // 用户回退到旧版客户端:只有 v1 迁移、且按**显式列名**读(不许 SELECT *)
        var v1Only = StateStore.DefaultMigrations.Where(m => m.Version == 1).ToList();
        using var old = StateStore.Open(path, v1Only);
        Assert(old.SchemaNewerThanCode, "旧版代码应当知道自己打开的是更新的库");
        Assert(old.SchemaVersion == StateStore.LatestVersionOf(StateStore.DefaultMigrations),
            $"库的版本仍是当前最新(v1 代码只读、不改它),实际 v{old.SchemaVersion}");
        var version = Convert.ToInt64(old.Scalar(
            "SELECT remote_version FROM sync_state WHERE file_id = $f", ("$f", "f2")));
        Assert(version == 3L, $"回退后仍应读到数据,实际 remote_version={version}");
    });
    return Task.CompletedTask;
}

static Task CheckNewerSchemaDetectedAsync()
{
    WithTempDb(path =>
    {
        var withV3 = StateStore.DefaultMigrations
            .Append(new Migration(99, "future-client", "ALTER TABLE sync_state ADD COLUMN future_flag INTEGER NOT NULL DEFAULT 0;"))
            .ToList();
        using (var future = StateStore.Open(path, withV3))
        {
            Assert(future.SchemaVersion == 99, $"应到 v99,实际 v{future.SchemaVersion}");
        }

        using var current = StateStore.Open(path); // 当前代码只到 v2
        Assert(current.SchemaVersion == 99, "库的版本号是库的事实(由更新版本客户端写的 v99)");
        Assert(current.SchemaNewerThanCode, "当前代码必须识别出「库比代码新」");
        // 仍然可用:按显式列名读自己认识的列
        current.Execute("INSERT INTO sync_state (file_id, space_id, local_path) VALUES ($f,$s,$p)",
            ("$f", "f3"), ("$s", "s3"), ("$p", "c"));
        Assert(TableExists(current, "sync_state"), "更新的库仍应可读写自己认识的列");
    });
    return Task.CompletedTask;
}

static Task CheckDestructiveMigrationRejectedAsync()
{
    var cases = new (string Sql, string Label)[]
    {
        ("DROP TABLE sync_state;", "DROP TABLE"),
        ("ALTER TABLE sync_state DROP COLUMN state;", "DROP COLUMN"),
        ("DELETE FROM sync_state;", "DELETE FROM"),
        ("ALTER TABLE sync_state RENAME COLUMN state TO st;", "RENAME COLUMN"),
        ("TRUNCATE TABLE sync_state;", "TRUNCATE"),
    };
    foreach (var (sql, label) in cases)
    {
        var rejected = false;
        try
        {
            _ = new Migration(9, "destructive", sql);
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }
        Assert(rejected, $"{label} 必须在**构造迁移时**就被拒绝(否则回退路径会永久损坏)");
    }

    // 只增的语句必须放行(否则门禁会把人逼去关掉它)
    _ = new Migration(9, "additive", "ALTER TABLE sync_state ADD COLUMN ok INTEGER NOT NULL DEFAULT 0;");
    _ = new Migration(9, "index", "DROP INDEX IF EXISTS ix_sync_state_path;");
    return Task.CompletedTask;
}

static Task CheckFailedMigrationKeepsVersionAsync()
{
    WithTempDb(path =>
    {
        using (var v2 = StateStore.Open(path))
        {
            v2.Execute("INSERT INTO sync_state (file_id, space_id, local_path) VALUES ($f,$s,$p)",
                ("$f", "keep"), ("$s", "s"), ("$p", "p"));
        }

        // 一条"先建表、再执行非法 SQL"的迁移:失败后**必须整体回滚**(表与版本号都不留)
        var broken = StateStore.DefaultMigrations
            .Append(new Migration(99, "broken",
                "CREATE TABLE half_built (id INTEGER PRIMARY KEY);\nSELECT * FROM no_such_table;"))
            .ToList();
        var threw = false;
        try
        {
            using var _ = StateStore.Open(path, broken);
        }
        catch (Exception)
        {
            threw = true;
        }
        Assert(threw, "迁移失败应当抛出,而不是留下半截 schema");

        using var after = StateStore.Open(path); // 回到 v2
        Assert(after.SchemaVersion == StateStore.LatestVersionOf(StateStore.DefaultMigrations),
            $"失败之后版本必须仍是最新(v99 那条被回滚),实际 v{after.SchemaVersion}");
        Assert(!TableExists(after, "half_built"), "失败迁移里建的表必须被回滚掉");
        Assert(Convert.ToString(after.Scalar("SELECT file_id FROM sync_state LIMIT 1")) == "keep",
            "失败迁移不能影响原有数据");
    });
    return Task.CompletedTask;
}

static Task CheckPragmasAsync()
{
    WithTempDb(path =>
    {
        using var store = StateStore.Open(path);
        Assert(Convert.ToString(store.Scalar("PRAGMA journal_mode;"))?.ToLowerInvariant() == "wal",
            $"journal_mode 应为 wal(WAL 让读写并发不互锁),实际 {store.Scalar("PRAGMA journal_mode;")}");
        Assert(Convert.ToInt64(store.Scalar("PRAGMA busy_timeout;")) == 5000, "busy_timeout 应为 5000ms");
        Assert(Convert.ToInt64(store.Scalar("PRAGMA foreign_keys;")) == 1, "foreign_keys 应打开");
    });
    return Task.CompletedTask;
}

static Task CheckIdempotentOpenAsync()
{
    WithTempDb(path =>
    {
        long first;
        using (var a = StateStore.Open(path))
        {
            a.Execute("INSERT INTO sync_state (file_id, space_id, local_path) VALUES ($f,$s,$p)",
                ("$f", "x"), ("$s", "s"), ("$p", "p"));
            first = Convert.ToInt64(a.Scalar("SELECT count(*) FROM sync_state"));
        }
        using (var b = StateStore.Open(path))
        {
            Assert(b.SchemaVersion == StateStore.LatestVersionOf(StateStore.DefaultMigrations), "第二次打开不该改变版本");
            Assert(Convert.ToInt64(b.Scalar("SELECT count(*) FROM sync_state")) == first,
                "第二次打开不该重建/清空数据");
        }
    });
    return Task.CompletedTask;
}

static Task CheckNewColumnsAsync()
{
    WithTempDb(path =>
    {
        // 先在 v1 写一行,再升级:新列应当取到默认值(而不是 NULL —— NOT NULL DEFAULT 就是为了这个)
        var v1Only = StateStore.DefaultMigrations.Where(m => m.Version == 1).ToList();
        using (var old = StateStore.Open(path, v1Only))
        {
            old.Execute("INSERT INTO sync_state (file_id, space_id, local_path) VALUES ($f,$s,$p)",
                ("$f", "legacy"), ("$s", "s"), ("$p", "p"));
        }
        using var now = StateStore.Open(path);
        Assert(Convert.ToInt64(now.Scalar(
            "SELECT pending_remote_gone FROM sync_state WHERE file_id=$f", ("$f", "legacy"))) == 0,
            "新增的 pending_remote_gone 对旧行应为默认值 0");
        Assert(Convert.ToString(now.Scalar(
            "SELECT retry_after_utc FROM sync_state WHERE file_id=$f", ("$f", "legacy"))) == "",
            "新增的 retry_after_utc 对旧行应为空串");
    });
    return Task.CompletedTask;
}

static Task CheckConcurrentConnectionsAsync()
{
    WithTempDb(path =>
    {
        using var writer = StateStore.Open(path);
        using var reader = StateStore.Open(path);
        writer.Execute("INSERT INTO sync_state (file_id, space_id, local_path) VALUES ($f,$s,$p)",
            ("$f", "w1"), ("$s", "s"), ("$p", "p"));
        Assert(Convert.ToInt64(reader.Scalar("SELECT count(*) FROM sync_state")) == 1,
            "一个连接写入后另一个连接应当能读到(WAL)");
        reader.Execute("INSERT INTO sync_state (file_id, space_id, local_path) VALUES ($f,$s,$p)",
            ("$f", "w2"), ("$s", "s"), ("$p", "p"));
        Assert(Convert.ToInt64(writer.Scalar("SELECT count(*) FROM sync_state")) == 2,
            "写入不应因为另一个连接持有读事务而失败(database is locked)");
    });
    return Task.CompletedTask;
}

// ---------------------------------------------------------------- 工具

static bool TableExists(StateStore store, string name)
    => Convert.ToInt64(store.Scalar(
        "SELECT count(*) FROM sqlite_master WHERE type='table' AND name=$n", ("$n", name))) > 0;

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new Exception(message);
    }
}

static void WithTempDb(Action<string> body)
{
    var dir = Path.Combine(Path.GetTempPath(), "netdisk-state-check");
    Directory.CreateDirectory(dir);
    var path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".db");
    try
    {
        body(path);
    }
    finally
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try
            {
                if (File.Exists(path + suffix))
                {
                    File.Delete(path + suffix);
                }
            }
            catch (IOException)
            {
                // 清理失败不影响结论(下一条用新文件名)
            }
        }
    }
}
