// 期望变更账本的 SQLite 落库(DE-D-14 顺带补上 DE-D-10 记录的那处缺口)。
//
// 为什么必须落库:内存账本能防住同进程内的回环,但防不住**跨进程重启**的那一种 ——
// 进程在"登记意图"(即将落盘)与"事件到达"之间崩溃时,重启后 watcher 仍可能补发那次事件,
// 而新进程的账本是空的,于是它会被当成"用户在本地改了文件"→ 凭空多传一次
// (幂等、不丢数据,但用户会看到"我什么都没改,它却在传")。
//
// 三条纪律:
//   ①**查询显式列名**(DE-D-07 的纪律②):这个库可能被更新版本写过,
//     `SELECT *` 会让旧代码在"列多了/少了"时静默取错值;
//   ②**同一路径只有一条记录**:账本按路径匹配,重复行会让"销账"只删掉一条、
//     另一条继续吞后续事件(表现为"用户改了文件却一直没同步");
//   ③**登记是覆盖而非追加**:同一个文件连续两次落盘,只有最后一次的 size/mtime 有意义。

using NetDisk.ClientCore;
using NetDisk.SyncEngine.Sync;

namespace NetDisk.SyncEngine;

/// <summary>把账本写进状态库的实现(表结构见 StateStore 的 v3 迁移)。</summary>
public sealed class SqliteExpectedChangeSink : IExpectedChangeSink
{
    private readonly StateStore _store;

    public SqliteExpectedChangeSink(StateStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public void Save(ExpectedChange change)
    {
        // 先删同路径的旧行(纪律②):账本按路径匹配,留两行会让销账只消掉一条
        Remove(change.Path);
        _store.Execute(@"
INSERT INTO expected_changes (local_path, op, file_id, created_at_utc, expected_size, expected_mtime_ticks)
VALUES ($path, $op, '', $at, $size, $mtime)",
            ("$path", change.Path),
            ("$op", "expect"),
            ("$at", change.RecordedAt.UtcDateTime.ToString("O")),
            ("$size", change.ExpectedSize),
            ("$mtime", change.ExpectedMtimeTicks));
    }

    public void Remove(string path)
    {
        _store.Execute("DELETE FROM expected_changes WHERE local_path = $path", ("$path", path));
    }

    public IReadOnlyList<ExpectedChange> Load()
    {
        // 走 StateStore 的封装(显式列名 + 与别的线程共用同一把锁,见 StateStore._gate)
        return _store.Query(
            "SELECT local_path, expected_size, expected_mtime_ticks, created_at_utc FROM expected_changes",
            reader =>
            {
                var path = reader.GetString(0);
                var size = reader.GetInt64(1);
                var mtime = reader.GetInt64(2);
                var at = DateTimeOffset.TryParse(reader.GetString(3), out var parsed)
                    ? parsed
                    : DateTimeOffset.UnixEpoch;
                return new ExpectedChange(path, size, mtime, at);
            });
    }
}
