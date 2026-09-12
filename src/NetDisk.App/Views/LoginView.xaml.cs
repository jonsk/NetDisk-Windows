// 登录页的代码后置:把用户输入变成"一条跑起来的同步链"。
//
// 这一层刻意很薄 —— 真正的编排(对账/冲突/传输)都在 SyncEngine 的 SyncHost 里。
// 这里只做三件 UI 才有资格做的事:
//   ① 收集输入(服务器地址/账号/同步目录);
//   ② 首次运行时用 OnboardingPlan 的语义**确认同步根**(容量预估 + 路径预算校验);
//   ③ 把结果(配置 + 运行时)交给主窗口。
//
// ⚠ 所有等待都在 async/await 上,绝不 .Result/.Wait():登录与首次列目录都是网络操作,
// 在 UI 线程上同步等待会把窗口冻住(用户看到"未响应"),这也是最容易犯的错。

using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using NetDisk.SyncEngine.Files;
using NetDisk.SyncEngine.Host;
using NetDisk.SyncEngine.Onboarding;

namespace NetDisk.App.Views;

// 基类**必须写全限定名**:本工程同时开了 UseWPF 与 UseWindowsForms(托盘要用),
// 于是 `UserControl` 会在 System.Windows.Controls 与 System.Windows.Forms 之间二义
// (CS0104)。既有视图也是这个写法。
public partial class LoginView : System.Windows.Controls.UserControl
{
    /// <summary>登录并启动同步成功。参数是已经跑起来的运行时。</summary>
    public event Action<SyncRuntime>? SignedIn;

    public LoginView()
    {
        InitializeComponent();

        var cfg = ClientConfig.Load();
        ServerBox.Text = string.IsNullOrWhiteSpace(cfg.BaseUrl) ? "http://" : cfg.BaseUrl;
        UserBox.Text = cfg.LastLogin;
        // 同步目录给一个**默认值**而不是留空:大多数用户不会自己挑目录,
        // 留空的结果是点登录后报"请填写同步目录",而他并不知道该填什么。
        RootBox.Text = string.IsNullOrWhiteSpace(cfg.SyncRoot)
            ? System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "NetDisk")
            : cfg.SyncRoot;
        PassBox.Focus();
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        // 用系统选择框(WPF 没有原生的"选文件夹");选不到就保持原值,不报错
        var dlg = new OpenFolderDialog
        {
            Title = "选择同步目录",
            InitialDirectory = Directory.Exists(RootBox.Text) ? RootBox.Text : null,
        };
        if (dlg.ShowDialog() == true)
        {
            RootBox.Text = dlg.FolderName;
        }
    }

    private async void OnLogin(object sender, RoutedEventArgs e)
    {
        var baseUrl = ServerBox.Text.Trim().TrimEnd('/');
        var user = UserBox.Text.Trim();
        var pass = PassBox.Password;
        var root = RootBox.Text.Trim();

        if (baseUrl.Length == 0 || user.Length == 0 || pass.Length == 0 || root.Length == 0)
        {
            Fail("服务器地址、用户名、口令、同步目录都要填。");
            return;
        }
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out _))
        {
            Fail("服务器地址要写成完整地址,例如 http://10.14.37.187");
            return;
        }

        var cfg = ClientConfig.Load();
        cfg.BaseUrl = baseUrl;
        cfg.SyncRoot = root;

        LoginButton.IsEnabled = false;
        try
        {
            Info("正在登录…");
            SyncRuntime runtime;
            try
            {
                runtime = await SyncRuntime.SignInAsync(cfg, user, pass);
            }
            catch (Exception ex)
            {
                Fail("登录失败: " + ex.Message);
                return;
            }

            // 首次运行:先让用户看见"要同步什么",再决定开始。
            // 这里复用 OnboardingPlan 的**语义**(容量预估 + 路径预算 + 三选默认节流),
            // 而不是自己另写一套判断 —— 另写一套必然与引擎侧的规则漂移。
            if (!cfg.Onboarded)
            {
                Info("正在读取服务器上的文件清单…");
                var plan = await BuildPlanAsync(runtime, FirstSyncChoice.FullWithThrottle, root);
                var problems = plan.Validate();
                if (problems.Count > 0)
                {
                    Fail("同步目录不可用: " + string.Join(";", problems));
                    await runtime.DisposeAsync();
                    return;
                }

                var estimate = plan.Estimate;
                var summary =
                    $"服务器上有 {estimate?.FileCount ?? 0} 个文件、{estimate?.DirectoryCount ?? 0} 个目录," +
                    $"合计约 {FormatSize(estimate?.TotalBytes ?? 0)}。\n\n" +
                    $"同步目录:{root}\n" +
                    "(首次同步会把服务器上的文件下载到该目录,并把该目录里的文件上传到服务器。)";
                OnboardingText.Text = summary;
                Info(summary);

                var ok = System.Windows.MessageBox.Show(
                    summary + "\n\n现在开始同步吗?", "确认首次同步",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;
                if (!ok)
                {
                    Info("已取消:没有开始同步,也没有改动任何文件。");
                    await runtime.DisposeAsync();
                    return;
                }
                cfg.Onboarded = true;
            }

            cfg.Save();

            Info("正在启动同步…");
            var started = await runtime.StartAsync();
            if (!started)
            {
                Fail("同步未能启动(配置不完整或令牌不可用),请重新登录。");
                await runtime.DisposeAsync();
                return;
            }

            PassBox.Clear();
            SignedIn?.Invoke(runtime);
        }
        catch (Exception ex)
        {
            Fail("发生未预期的错误: " + ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            LoginButton.IsEnabled = true;
        }
    }

    /// <summary>用引擎的向导算计划(容量预估 + 路径预算),树来自真实远端列表。</summary>
    private static async Task<OnboardingPlan> BuildPlanAsync(
        SyncRuntime runtime, FirstSyncChoice choice, string root)
    {
        var files = new FileApi(runtime.Api);
        var spaceId = runtime.Config.SpaceId;
        var wizard = new OnboardingWizard(async (_, ct) =>
            await files.CollectTreeAsync(spaceId, null, ct).ConfigureAwait(false));
        return await wizard.BuildAsync(choice, root);
    }

    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1)
        {
            v /= 1024;
            i++;
        }
        return v.ToString(i == 0 ? "F0" : "F1") + " " + units[i];
    }

    private void Info(string message)
    {
        StatusText.Foreground = System.Windows.Media.Brushes.DimGray;
        StatusText.Text = message;
    }

    private void Fail(string message)
    {
        StatusText.Foreground = System.Windows.Media.Brushes.Firebrick;
        StatusText.Text = message;
    }
}
