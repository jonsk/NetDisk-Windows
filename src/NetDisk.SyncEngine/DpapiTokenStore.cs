// DPAPI 令牌存储(DE-D-04 ①)。
//
// 为什么用 DPAPI 而不是"自己加密 + 自己藏密钥":桌面端要防的是"同机其它用户/被顺走的
// 磁盘文件读到令牌"。DPAPI(CurrentUser 作用域)把密钥绑到**当前 Windows 用户**,
// 由系统托管与轮换 —— 自己写一份密钥管理只会得到一个更弱的东西:密钥要么硬编码在
// 二进制里(任何拿到 exe 的人都能解),要么需要一个用户口令(那就又回到"每次启动问
// 口令",与静默刷新冲突)。
//
// **为什么直接 P/Invoke crypt32 而不是用 System.Security.Cryptography.ProtectedData 包**:
// 那个类型在 .NET Core 之后是一个**独立 NuGet 包**,会给一个零 NuGet 依赖的客户端内核
// 引入唯一的外部依赖(CI 断网/私有源都装不出来,而它做的事就是调这两个 API)。
// 这里用 30 行 P/Invoke 换掉一个包依赖,并且让"密文格式"完全可见。
//
// 作用域选择:CRYPTPROTECT_LOCAL_MACHINE **不用** —— 那会让同机任何用户都能解密;
// 我们接受"用户重装系统/换机器后要重新登录"这个代价。

using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NetDisk.Transport;

using NetDisk.SyncEngine.Host;

namespace NetDisk.SyncEngine;

/// <summary>DPAPI 加密的令牌文件(CurrentUser 作用域 + 固定附加熵)。</summary>
public sealed class DpapiTokenStore : ITokenStore
{
    /// <summary>
    /// 附加熵(secondary entropy):让密文**只对"本程序 + 本用户"有意义。
    ///
    /// 它不是密钥(熵是公开的),作用是防止"同机另一个程序也解你自己的 DPAPI 密文":
    /// 有熵时,别的进程即使以同一用户身份运行,不知道这串熵也解不开。
    /// </summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NetDisk.Desktop.TokenStore.v1");

    private const int CryptProtectUiForbidden = 0x1;

    private readonly string _path;

    public DpapiTokenStore(string? path = null)
    {
        _path = path ?? DefaultPath();
    }

    /// <summary>令牌文件路径(默认与配置同目录:<c>&lt;程序目录&gt;\tokens.bin</c>)。</summary>
    public string Path => _path;

    /// <summary>
    /// 默认路径:与配置/状态库同目录(见 <see cref="ClientPaths"/>),即程序所在目录。
    ///
    /// 早前放在 LOCALAPPDATA 的理由是"令牌是机器本地的,别跟着漫游走"。单文件发布后
    /// 统一到程序目录:程序目录本身通常也在本机(MSI 是 perUser 安装),而**配置与令牌分家**
    /// 才是真麻烦(用户看到"配置在、登录态没了",无从解释)。漫游目录的问题依然不存在:
    /// 我们不再用 Roaming,只可能落到 %APPDATA%(那是回退),不会跨机器同步 DPAPI 密文
    /// —— 密文解不开时 <see cref="LoadAsync"/> 会当作未登录并要求重新登录。
    /// </summary>
    public static string DefaultPath() => ClientPaths.TokenPath;

    public ValueTask<TokenSet?> LoadAsync(CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(_path))
            {
                return ValueTask.FromResult<TokenSet?>(null);
            }
            var blob = File.ReadAllBytes(_path);
            if (blob.Length == 0)
            {
                return ValueTask.FromResult<TokenSet?>(null);
            }
            var json = Unprotect(blob);
            var dto = JsonSerializer.Deserialize<StoredTokens>(json);
            if (dto is null || string.IsNullOrEmpty(dto.AccessToken) || string.IsNullOrEmpty(dto.RefreshToken))
            {
                return ValueTask.FromResult<TokenSet?>(null);
            }
            return ValueTask.FromResult<TokenSet?>(new TokenSet
            {
                AccessToken = dto.AccessToken,
                RefreshToken = dto.RefreshToken,
                AccessExpiresAt = dto.AccessExpiresAt,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                      or CryptographyException)
        {
            // 契约①:任何失败都当作"未登录"。**顺带把坏密文删掉** ——
            // 留着它只会让每次启动都走一遍同样的失败路径(而且下次仍解不开)。
            TryDelete();
            return ValueTask.FromResult<TokenSet?>(null);
        }
    }

    public ValueTask SaveAsync(TokenSet tokens, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        var dto = new StoredTokens
        {
            AccessToken = tokens.AccessToken,
            RefreshToken = tokens.RefreshToken,
            AccessExpiresAt = tokens.AccessExpiresAt,
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(dto);
        var blob = Protect(json);

        var dir = System.IO.Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);

        // 契约②:原子写 —— 同目录临时文件 + File.Move(overwrite)。
        // 直接 File.WriteAllBytes 在掉电时可能留下半截密文,而半截密文既解不开
        // 也无法区分"损坏"与"就是没有";临时文件与目标同目录是为了让 Move 变成
        // 同卷 rename(跨卷 Move 不是原子的)。
        var tmp = _path + ".tmp";
        File.WriteAllBytes(tmp, blob);
        File.Move(tmp, _path, overwrite: true);
        return ValueTask.CompletedTask;
    }

    public ValueTask ClearAsync(CancellationToken ct = default)
    {
        TryDelete();
        return ValueTask.CompletedTask;
    }

    private void TryDelete()
    {
        try
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 删不掉不是致命错误:调用方随后会覆盖它(而 Save 是原子的)
        }
    }

    private static byte[] Protect(byte[] plain)
    {
        var inBlob = new DataBlob(plain);
        var entropyBlob = new DataBlob(Entropy);
        try
        {
            if (!CryptProtectData(ref inBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out var outBlob))
            {
                throw new CryptographyException("CryptProtectData 失败", Marshal.GetLastWin32Error());
            }
            try
            {
                return outBlob.ToArray();
            }
            finally
            {
                LocalFree(outBlob.pbData);
            }
        }
        finally
        {
            inBlob.Free();
            entropyBlob.Free();
        }
    }

    private static byte[] Unprotect(byte[] cipher)
    {
        var inBlob = new DataBlob(cipher);
        var entropyBlob = new DataBlob(Entropy);
        try
        {
            if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out var outBlob))
            {
                throw new CryptographyException("CryptUnprotectData 失败(密文损坏或换了 Windows 用户)", Marshal.GetLastWin32Error());
            }
            try
            {
                return outBlob.ToArray();
            }
            finally
            {
                LocalFree(outBlob.pbData);
            }
        }
        finally
        {
            inBlob.Free();
            entropyBlob.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;

        public DataBlob(byte[] data)
        {
            cbData = data.Length;
            pbData = Marshal.AllocHGlobal(data.Length);
            Marshal.Copy(data, 0, pbData, data.Length);
        }

        public readonly byte[] ToArray()
        {
            var buf = new byte[cbData];
            Marshal.Copy(pbData, buf, 0, cbData);
            return buf;
        }

        public readonly void Free()
        {
            if (pbData != IntPtr.Zero)
            {
                // 明文缓冲区要清零再释放:令牌是凭据,释放后内存里不该留着
                for (var i = 0; i < cbData; i++)
                {
                    Marshal.WriteByte(pbData, i, 0);
                }
                Marshal.FreeHGlobal(pbData);
            }
        }
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn, string? szDataDescr, ref DataBlob pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn, IntPtr ppszDataDescr, ref DataBlob pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DataBlob pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private sealed class CryptographyException : Exception
    {
        public CryptographyException(string message, int win32Error)
            : base($"{message}(win32={win32Error})")
        {
        }
    }

    private sealed record StoredTokens
    {
        [JsonPropertyName("access_token")] public string AccessToken { get; init; } = "";
        [JsonPropertyName("refresh_token")] public string RefreshToken { get; init; } = "";
        [JsonPropertyName("access_expires_at")] public DateTimeOffset AccessExpiresAt { get; init; }
    }
}
