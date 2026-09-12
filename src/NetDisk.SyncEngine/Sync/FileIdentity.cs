// 用 FileIdInfo 识别改名(DE-D-11)。
//
// 为什么必须识别改名:一个 10GB 的文件被改名,若同步引擎把它看成"删了一个 + 新建了一个",
// 用户要为此**重传 10GB**;而服务端本来有一次 `MOVE` 就够了(6.3)。这在真实使用里
// 极其常见:用户整理目录、批量重命名、工具改后缀。
//
// 判据是 Windows 的**文件身份**(`FILE_ID_INFO` = 卷序列号 + 128 位文件 ID):
//   - 同一卷内改名/移动 → 文件 ID 不变 → 判定为 MOVE(不读内容、不重传);
//   - **跨卷**移动 → 卷序列号不同(本质上是一次复制+删除)→ 不能当成 MOVE;
//   - **文件 ID 失效**:杀毒软件重写、备份还原、云盘客户端"占位文件"替换等会让同一个
//     路径指向一个**新的**文件 ID。此时不能就此认定"这是两个不同的文件" ——
//     否则用户会看到"我改个名它就重传了几个 G"。
//
// 所以判定是两级的:①身份相同 → MOVE(且**不去算哈希**,10GB 文件不该为了改个名被读一遍);
// ②身份缺失/失效 → **降级为哈希比对**(内容一样就仍然按 MOVE 处理,内容不同才算两个文件)。
// 这条降级路径必须存在,也必须被测到 —— 它是"文件 ID 不可靠时仍然不重传"的唯一保障。

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NetDisk.SyncEngine.Sync;

/// <summary>文件身份(卷序列号 + 128 位文件 ID)。</summary>
public readonly record struct FileIdentity(ulong VolumeSerial, ulong FileIdHigh, ulong FileIdLow)
{
    public bool SameVolume(in FileIdentity other) => VolumeSerial == other.VolumeSerial;

    public bool Equals(in FileIdentity other)
        => VolumeSerial == other.VolumeSerial && FileIdHigh == other.FileIdHigh && FileIdLow == other.FileIdLow;
}

/// <summary>文件身份读取(真实实现走 Win32;测试可注入假的)。</summary>
public interface IFileIdentityProvider
{
    /// <summary>读取文件/目录的身份;读不到(权限、被占用、路径不存在)返回 null。</summary>
    FileIdentity? TryGet(string path);
}

/// <summary>Win32 实现:CreateFile + GetFileInformationByHandleEx(FileIdInfo)。</summary>
public sealed class Win32FileIdentityProvider : IFileIdentityProvider
{
    private const uint FileReadAttributes = 0x0080;
    private const uint ShareAll = 0x00000001 | 0x00000002 | 0x00000004;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000; // 允许对**目录**取句柄
    private const int FileIdInfoClass = 18;

    public FileIdentity? TryGet(string path)
    {
        // 只申请"读属性":不需要读内容权限,因此对只读文件也能取到身份
        using var handle = CreateFile(path, FileReadAttributes, ShareAll, IntPtr.Zero,
            OpenExisting, BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return null;
        }
        var info = new FileIdInfoNative { VolumeSerialNumber = 0, FileId = new byte[16] };
        if (!GetFileInformationByHandleEx(handle, FileIdInfoClass, ref info, (uint)Marshal.SizeOf<FileIdInfoNative>()))
        {
            return null;
        }
        return new FileIdentity(
            info.VolumeSerialNumber,
            BitConverter.ToUInt64(info.FileId, 8),
            BitConverter.ToUInt64(info.FileId, 0));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfoNative
    {
        public ulong VolumeSerialNumber;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] FileId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle hFile, int fileInformationClass, ref FileIdInfoNative lpFileInformation, uint dwBufferSize);
}

/// <summary>改名判定的结论。</summary>
public enum RenameVerdict
{
    /// <summary>是同一个文件的改名 → 走 MOVE(不重传)。</summary>
    Move,

    /// <summary>不是同一个文件(或跨卷)→ 按"删除 + 新建"处理(需要上传)。</summary>
    NotARename,
}

/// <summary>改名判定结果(带判据,便于日志与排障)。</summary>
public sealed record RenameDecision(RenameVerdict Verdict, string Basis, bool HashComputed);

/// <summary>一侧的文件信息(改名判定只关心这几个量)。</summary>
public sealed record FileFingerprint(string Path, long Size, FileIdentity? Identity);

/// <summary>
/// 改名判定器:先比身份,身份不可靠时**降级为哈希比对**。
/// </summary>
public sealed class RenameDetector
{
    private readonly IFileIdentityProvider _identity;
    private readonly Func<string, string?>? _hash;

    /// <param name="identity">身份读取(通常 <see cref="Win32FileIdentityProvider"/>)。</param>
    /// <param name="hash">内容哈希(降级路径才调用;读 10GB 内容只为比一次是很贵的,
    /// 所以它必须是一个**只在需要时**被调用的委托,检查器会断言"身份相同时它没被调用")。</param>
    public RenameDetector(IFileIdentityProvider identity, Func<string, string?>? hash = null)
    {
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _hash = hash;
    }

    /// <summary>降级路径被使用的次数(诊断用:数量大说明这台机器上文件 ID 不稳)。</summary>
    public int HashFallbacks { get; private set; }

    /// <summary>
    /// 判断"旧路径消失、新路径出现"是不是同一次改名。
    /// </summary>
    public RenameDecision Detect(FileFingerprint before, FileFingerprint after)
    {
        // ① 身份相同 → 直接 MOVE。**不读内容**:10GB 的文件不该为了改个名被读一遍。
        if (before.Identity is { } b && after.Identity is { } a && b.Equals(a))
        {
            return new RenameDecision(RenameVerdict.Move, "文件身份相同(同卷内改名/移动)", HashComputed: false);
        }

        // ② 身份都有但不同:可能是跨卷(卷序列号不同)或文件被替换。
        if (before.Identity is { } b2 && after.Identity is { } a2 && !b2.SameVolume(a2))
        {
            // 跨卷移动在底层是"复制 + 删除",服务端看到的是两个不同的对象 → 不能当 MOVE
            return new RenameDecision(RenameVerdict.NotARename, "跨卷(卷序列号不同)→ 不是改名", HashComputed: false);
        }

        // ③ 大小都相同才有资格继续比内容(大小不同直接判否,省掉一次读)
        if (before.Size != after.Size)
        {
            return new RenameDecision(RenameVerdict.NotARename, "大小不同 → 不是同一个文件", HashComputed: false);
        }

        // ④ 降级:文件 ID 缺失或失效(杀毒重写/备份还原/占位文件替换)→ 比内容。
        //    这条路径是"文件 ID 不可靠时仍然不重传几个 G"的唯一保障。
        if (_hash is null)
        {
            return new RenameDecision(RenameVerdict.NotARename,
                "文件身份不可用且未提供内容哈希 → 保守判否(宁可重传,不可错判)", HashComputed: false);
        }

        HashFallbacks++;
        var h1 = _hash(before.Path);
        var h2 = _hash(after.Path);
        if (h1 is not null && h2 is not null && h1 == h2)
        {
            return new RenameDecision(RenameVerdict.Move,
                "文件身份不可靠,但内容哈希一致 → 仍按 MOVE 处理(避免重传)", HashComputed: true);
        }
        return new RenameDecision(RenameVerdict.NotARename, "内容哈希不同 → 不是同一个文件", HashComputed: true);
    }
}
