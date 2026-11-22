using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AggPanSearch;
using AggPanSearch.Config;
using AggPanSearch.Models;
using AggPanSearch.Sources.Plugins;

namespace AggPanSearch.WPF;

/// <summary>
/// AggPanSearch 搜索功能测试主窗口。
/// </summary>
public partial class MainWindow : Window
{
    private readonly AggPanSearchService _service;

    public MainWindow()
    {
        InitializeComponent();

        // 使用默认 TG 频道配置（AggPanSearchConfig.DefaultChannels，清理后约 90 个频道）+ 内置 Mock 插件，
        // 便于测试 TG 来源与插件来源。Channels 不赋值即采用默认频道列表。
        var config = new AggPanSearchConfig
        {
            CacheTtlMinutes = 30,
            Log = msg => Dispatcher.BeginInvoke(() => AppendStatus(msg)),
        };
        _service = new AggPanSearchService(config);
        _service.Plugins.Register(new MockSearchPlugin());
    }

    private async void SearchButton_Click(object sender, RoutedEventArgs e)
        => await DoSearchAsync();

    private void KeywordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            _ = DoSearchAsync();
    }

    private async Task DoSearchAsync()
    {
        var keyword = KeywordBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(keyword))
        {
            AppendStatus("请输入关键词。");
            return;
        }

        SearchButton.IsEnabled = false;
        StatusText.Foreground = Brushes.Blue;
        StatusText.Text = "搜索中，请稍候...";
        TotalText.Text = string.Empty;
        MergedTree.Items.Clear();
        ResultGrid.ItemsSource = null;

        try
        {
            var source = (ComboBoxItem)SourceBox.SelectedItem;
            var sourceType = (string)source.Tag;
            var resp = await _service.SearchAsync(keyword, b => b
                .ResultType(ResultTypes.All)
                .Source(sourceType)
                .Refresh()); // 强制刷新，便于测试真实网络

            RenderResults(resp);
        }
        catch (Exception ex)
        {
            AppendStatus($"搜索失败：{ex.Message}");
        }
        finally
        {
            SearchButton.IsEnabled = true;
            StatusText.Foreground = Brushes.Gray;
            StatusText.Text = "就绪";
        }
    }

    private void RenderResults(SearchResponse resp)
    {
        TotalText.Text = $"共 {resp.Total} 条结果";
        AppendStatus($"搜索完成，共 {resp.Total} 条。");

        // 归并结果 -> 树
        MergedTree.Items.Clear();
        if (resp.MergedByType is not null && resp.MergedByType.Count > 0)
        {
            foreach (var kv in resp.MergedByType)
            {
                var groupNode = new TreeViewItem { Header = $"[{kv.Key}] ({kv.Value.Count})", IsExpanded = true };
                foreach (var link in kv.Value)
                {
                    var linkNode = new TreeViewItem
                    {
                        Header = $"{link.Note}  (来源:{link.Source})",
                        Tag = link.Url,
                    };
                    var sub = new TreeViewItem { Header = "链接：", IsExpanded = false };
                    var urlItem = new TreeViewItem { Header = link.Url, Tag = link.Url, FontFamily = new FontFamily("Consolas") };
                    var pwdItem = new TreeViewItem { Header = $"提取码：{link.Password ?? "无"}" };
                    sub.Items.Add(urlItem);
                    sub.Items.Add(pwdItem);
                    linkNode.Items.Add(sub);
                    groupNode.Items.Add(linkNode);
                }
                MergedTree.Items.Add(groupNode);
            }
        }
        else
        {
            MergedTree.Items.Add(new TreeViewItem { Header = "无归并结果" });
        }

        // 明细结果 -> 表格（取去重后的链接做可双击打开）
        ResultGrid.ItemsSource = resp.Results ?? new List<SearchResult>();
        AppendStatus($"按网盘类型归并 {resp.MergedByType?.Count ?? 0} 类，明细 {resp.Results?.Count ?? 0} 条。");
    }

    private void ResultGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        => OpenSelectedLinksPosText(ResultGrid.SelectedItem as SearchResult);

    private void OpenSelectedLinksPosText(SearchResult? item)
    {
        if (item is null || item.Links.Count == 0)
        {
            AppendStatus("该结果无可用链接。");
            return;
        }

        foreach (var link in item.Links)
            OpenUrl(link.Url);
    }

    private void MergedTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (MergedTree.SelectedItem is TreeViewItem item && item.Tag is string url)
            OpenUrl(url);
    }

    private static void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"无法打开链接：{ex.Message}", "AggPanSearch", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void AppendStatus(string message)
        => StatusText.Text = message;

    protected override void OnClosed(EventArgs e)
    {
        _service.Dispose();
        base.OnClosed(e);
    }
}