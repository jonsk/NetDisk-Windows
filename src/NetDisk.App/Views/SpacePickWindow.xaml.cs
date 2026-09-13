// "选择要同步的空间"对话框(多空间:添加绑定的第一步)。
//
// 只做两件事:列出服务端可见的空间、把**已经绑过的**标出来并禁用(避免用户把两个空间指到同一个目录 ——
// 那会让两棵树互相上传对方的文件,是"空间串了"最直接的原因)。

using System.Windows;
using NetDisk.ClientCore;

namespace NetDisk.App.Views;

public partial class SpacePickWindow : Window
{
    public SpacePickWindow(IReadOnlyList<SpaceView> spaces, IReadOnlySet<string> alreadyBound)
    {
        InitializeComponent();
        foreach (var s in spaces)
        {
            var bound = alreadyBound.Contains(s.id);
            var item = new System.Windows.Controls.ListBoxItem
            {
                Content = (string.IsNullOrWhiteSpace(s.name) ? s.id : s.name)
                          + $"  ({s.kind})"
                          + (bound ? "  — 已绑定" : ""),
                Tag = s,
                IsEnabled = !bound,
            };
            List.Items.Add(item);
            if (!bound && List.SelectedItem is null)
            {
                List.SelectedItem = item;
            }
        }
        HintText.Text = alreadyBound.Count == 0
            ? "每个空间需要一个**独立**的本地目录。"
            : $"已有 {alreadyBound.Count} 个空间绑定;已绑定的空间不再重复列出可选。";
    }

    /// <summary>用户选中的空间(取消时为 null)。</summary>
    public SpaceView? Selected { get; private set; }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is not System.Windows.Controls.ListBoxItem item || item.Tag is not SpaceView s)
        {
            HintText.Text = "请先选中一个空间。";
            return;
        }
        Selected = s;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
