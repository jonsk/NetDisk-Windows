// 令牌落盘端口(DE-D-04)。
//
// **为什么端口定义在 Transport 而实现在 SyncEngine**:实现必须用 Windows 的 DPAPI
// (P/Invoke crypt32),而 ClientCore/Transport 是**平台无关**工程(ADR-4,由
// depsguard 静态校验)。把端口放在这里,SyncEngine 就能实现它,而 Transport 的
// 其余部分(以及将来可能的其它宿主)只依赖这个抽象。
//
// 契约的三条纪律:
//   ①**Load 失败必须当作"未登录"**,绝不能抛出去炸启动 —— 密文损坏、换了机器、
//     换了 Windows 用户都会让 DPAPI 解不开,而那时候用户要的是"重新登录",不是崩溃;
//   ②**Save 必须是原子的**:直接覆盖写在掉电/崩溃时会留下半截密文,下次启动
//     既解不开也覆盖不了(表现为"每次启动都要重新登录"且看不出原因);
//   ③**Clear 必须真的把密文从盘上删掉**(登出/被吊销后,磁盘上不能留下可解密的令牌)。

namespace NetDisk.Transport;

/// <summary>令牌持久化端口。实现须保证原子写与失败即"未登录"。</summary>
public interface ITokenStore
{
    /// <summary>读取令牌;<b>任何</b>失败(不存在/损坏/无法解密)都返回 <c>null</c>。</summary>
    ValueTask<TokenSet?> LoadAsync(CancellationToken ct = default);

    /// <summary>原子写入(先写临时文件再替换)。</summary>
    ValueTask SaveAsync(TokenSet tokens, CancellationToken ct = default);

    /// <summary>删除令牌(登出 / 被吊销)。</summary>
    ValueTask ClearAsync(CancellationToken ct = default);
}

/// <summary>令牌内存实现(测试与"不落盘"模式用;也用于检查器)。</summary>
public sealed class InMemoryTokenStore : ITokenStore
{
    private TokenSet? _tokens;

    public ValueTask<TokenSet?> LoadAsync(CancellationToken ct = default) => ValueTask.FromResult(_tokens);

    public ValueTask SaveAsync(TokenSet tokens, CancellationToken ct = default)
    {
        _tokens = tokens;
        return ValueTask.CompletedTask;
    }

    public ValueTask ClearAsync(CancellationToken ct = default)
    {
        _tokens = null;
        return ValueTask.CompletedTask;
    }
}
