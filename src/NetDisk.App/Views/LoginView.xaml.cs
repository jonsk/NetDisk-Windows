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
using NetDisk.App.Localization;

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

        // 语言切换:登录前也能切换语言(与设置页共用同一套 i18n;登录文案全是绑定,切换即时刷新)。
        LanguageCombo.ItemsSource = Locale.Languages.Select(l => l.SelfName).ToList();
        LanguageCombo.SelectedIndex = GetLanguageIndex(Loc.Current);

        PassBox.Focus();
    }

    private static int GetLanguageIndex(string code) =>
        string.Equals(code, "en", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

    /// <summary>登录页语言切换:立即生效(免重启)+ 写回 client.json 持久化。</summary>
    private void OnLanguageChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (LanguageCombo.SelectedIndex < 0)
        {
            return;
        }
        var code = LanguageCombo.SelectedIndex switch
        {
            1 => "en",
            _ => "zh",
        };
        if (string.Equals(code, Loc.Current, StringComparison.Ordinal))
        {
            return;
        }
        Loc.Instance.Code = code;      // 即时全局刷新(空串通知 = 所有绑定重算)
        var cfg = ClientConfig.Load(); // 登录页还没有 SyncRuntime,直接读写配置持久化
        cfg.Language = code;
        cfg.Save();
        AppLog.Write("app", $"界面语言已切换:{code}");
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        // 用系统选择框(WPF 没有原生的"选文件夹");选不到就保持原值,不报错
        var dlg = new OpenFolderDialog
        {
            Title = Loc.T("Login.PickSyncDir"),
            InitialDirectory = Directory.Exists(RootBox.Text) ? RootBox.Text : null,
        };
        if (dlg.ShowDialog() == true)
        {
            RootBox.Text = dlg.FolderName;
        }
    }

    /// <summary>在 PasswordBox 与明文 TextBox 之间切换,方便核对输入的口令。</summary>
    private void OnTogglePassword(object sender, RoutedEventArgs e)
    {
        if (PassBox.Visibility == Visibility.Visible)
        {
            PassTextBox.Text = PassBox.Password;
            PassBox.Visibility = Visibility.Collapsed;
            PassTextBox.Visibility = Visibility.Visible;
            PassTextBox.Focus();
        }
        else
        {
            PassBox.Password = PassTextBox.Text;
            PassTextBox.Visibility = Visibility.Collapsed;
            PassBox.Visibility = Visibility.Visible;
            PassBox.Focus();
        }
    }

    private async void OnLogin(object sender, RoutedEventArgs e)
    {
        var baseUrl = ServerBox.Text.Trim().TrimEnd('/');
        var user = UserBox.Text.Trim();
        var pass = PassBox.Visibility == Visibility.Visible ? PassBox.Password : PassTextBox.Text;
        var root = RootBox.Text.Trim();

        if (baseUrl.Length == 0 || user.Length == 0 || pass.Length == 0 || root.Length == 0)
        {
            Fail(Loc.T("Login.FillAll"));
            return;
        }
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out _))
        {
            Fail(Loc.T("Error.BadBaseUrl"));
            return;
        }

        var cfg = ClientConfig.Load();
        cfg.BaseUrl = baseUrl;
        cfg.SyncRoot = root;

        LoginButton.IsEnabled = false;
        try
        {
            Info(Loc.T("Login.LoggingIn"));
            SyncRuntime runtime;
            try
            {
                runtime = await SyncRuntime.SignInAsync(cfg, user, pass);
            }
            catch (Exception ex)
            {
                Fail(Loc.F("Login.LoginFail", ex.Message));
                return;
            }

            // 首次运行:先让用户看见"要同步什么",再决定开始。
            // 这里复用 OnboardingPlan 的**语义**(容量预估 + 路径预算 + 三选默认节流),
            // 而不是自己另写一套判断 —— 另写一套必然与引擎侧的规则漂移。
            if (!cfg.Onboarded)
            {
                Info(Loc.T("Login.Listing"));
                var plan = await BuildPlanAsync(runtime, FirstSyncChoice.FullWithThrottle, root);
                var problems = plan.Validate();
                if (problems.Count > 0)
                {
                    Fail(Loc.F("Login.RootUnusable", string.Join(";", problems)));
                    await runtime.DisposeAsync();
                    return;
                }

                var estimate = plan.Estimate;
                // 空空间要给一句人话:否则"0 个文件、0 个目录"看起来像出错了
                // (服务端没有任何文件是完全正常的 —— 全新账号、或刚被清空的空间)。
                var remoteLine = plan.RemoteTreeWasEmpty
                    ? Loc.T("Login.EmptySpace")
                    : Loc.F("Login.CountSummary",
                        estimate?.FileCount ?? 0, estimate?.DirectoryCount ?? 0,
                        FormatSize(estimate?.TotalBytes ?? 0));
                var summary =
                    remoteLine + "\n\n" +
                    Loc.F("Login.SyncDirSummary", root);
                OnboardingText.Text = summary;
                Info(summary);

                var ok = System.Windows.MessageBox.Show(
                    summary + "\n\n" + Loc.T("Login.ConfirmStartQuestion"), Loc.T("Login.ConfirmFirstSync"),
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;
                if (!ok)
                {
                    Info(Loc.T("Login.Cancelled"));
                    await runtime.DisposeAsync();
                    return;
                }
                cfg.Onboarded = true;
            }

            cfg.Save();

            Info(Loc.T("Login.Starting"));
            var started = await runtime.StartAsync();
            if (!started)
            {
                Fail(Loc.T("Login.StartFail"));
                await runtime.DisposeAsync();
                return;
            }

            PassBox.Clear();
            PassTextBox.Clear();
            SignedIn?.Invoke(runtime);
        }
        catch (Exception ex)
        {
            Fail(Loc.F("Login.Unexpected", ex.GetType().Name + ": " + ex.Message));
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
