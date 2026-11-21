using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Agate.Models;
using Agate.Services;

namespace Agate;

public partial class FileSplitPage : ContentPage
{
    private const long Megabyte = 1024L * 1024;

    private readonly List<FileItem> _items;
    private readonly ObservableCollection<FileItem> _parts = new();
    private FileItem? _splitTarget;

    /// <summary>
    /// items 来自多选：恰好 1 个非目录文件 → 默认分割；
    /// 全部是 `*.partNNN` 且 ≥2 个 → 默认合并；其余情况按数量猜一个合理默认。
    /// </summary>
    public FileSplitPage(IReadOnlyList<FileItem> items)
    {
        InitializeComponent();
        _items = items.Where(i => !i.IsDirectory).ToList();
        BindableLayout.SetItemsSource(PartsList, _parts);

        var looksLikeParts = _items.Count >= 2 && _items.All(i => FileSplitter.PartIndex(i.FullPath) != int.MaxValue);

        // 先填分片列表，再切模式：SelectedIndex 变化会触发 ApplyMode，那时 _parts 必须已就绪。
        if (looksLikeParts)
        {
            var sorted = FileSplitter.SortParts(_items.Select(i => i.FullPath));
            foreach (var path in sorted)
                _parts.Add(_items.FirstOrDefault(i => i.FullPath == path) ?? new FileItem { Name = Path.GetFileName(path), FullPath = path });
        }

        ModePicker.SelectedIndex = looksLikeParts ? 1 : 0;
        ApplyMode();
    }

    private bool IsMergeMode => ModePicker.SelectedIndex == 1;

    private void OnModeChanged(object? sender, EventArgs e) => ApplyMode();

    private void ApplyMode()
    {
        var merging = IsMergeMode;

        SplitSection.IsVisible = !merging;
        MergeSection.IsVisible = merging;
        ModeHintLabel.Text = merging ? "把选中的分片按序号顺序拼接成一个文件。" : "把一个文件按大小或行数切成多个分片。";
        RunButton.Text = merging ? "合并" : "分割";

        if (merging)
        {
            PrepareMerge();
        }
        else
        {
            _splitTarget = _items.FirstOrDefault();
            if (_splitTarget is null)
            {
                StatusLabel.Text = "请选择一个要分割的文件。";
                StatusLabel.IsVisible = true;
                RunButton.IsEnabled = false;
                SplitFileLabel.Text = "未选择文件";
                SplitSizeLabel.Text = "";
                EstimateLabel.Text = "";
                return;
            }

            StatusLabel.IsVisible = false;
            RunButton.IsEnabled = true;
            SplitFileLabel.Text = _splitTarget.Name;
            var size = new FileInfo(_splitTarget.FullPath).Length;
            SplitSizeLabel.Text = FileOperations.FormatSize(size);
            ThresholdEntry.Text = size < Megabyte ? "1" : "100";
            UpdateEstimate();

            ThresholdEntry.TextChanged -= OnThresholdChanged;
            ThresholdEntry.TextChanged += OnThresholdChanged;
            SplitBasisPicker.SelectedIndexChanged -= OnThresholdChanged;
            SplitBasisPicker.SelectedIndexChanged += OnThresholdChanged;
        }
    }

    private void OnThresholdChanged(object? sender, EventArgs e) => UpdateEstimate();

    private void UpdateEstimate()
    {
        if (_splitTarget is null) return;

        var size = new FileInfo(_splitTarget.FullPath).Length;
        if (SplitBasisPicker.SelectedIndex == 1)
        {
            ThresholdLabel.Text = "每片行数";
            EstimateLabel.Text = "按行数分割会保留原始换行字节，适合文本/日志。";
            return;
        }

        ThresholdLabel.Text = "每片大小（MB）";
        var mb = double.TryParse((ThresholdEntry.Text ?? "").Trim(), out var value) && value > 0 ? value : 100;
        var bytes = (long)(mb * Megabyte);
        var count = bytes <= 0 ? 0 : (size + bytes - 1) / bytes;
        EstimateLabel.Text = count <= 0 ? "" : $"约 {count} 个分片 · 每片 {FileOperations.FormatSize(bytes)}";
    }

    private void PrepareMerge()
    {
        if (_parts.Count < 2)
        {
            StatusLabel.Text = "请选择至少两个分片文件。";
            StatusLabel.IsVisible = true;
            RunButton.IsEnabled = false;
            MergeCountLabel.Text = "";
            OutputEntry.Text = "";
            return;
        }

        StatusLabel.IsVisible = false;
        RunButton.IsEnabled = true;
        MergeCountLabel.Text = $"{_parts.Count} 个分片 · 合计 {FileOperations.FormatSize(_parts.Sum(p => new FileInfo(p.FullPath).Length))}";
        OutputEntry.Text = DefaultMergeName(_parts[0].FullPath);
        OutputHintLabel.Text = "输出到第一个分片所在目录；同名会被覆盖。";
    }

    /// <summary>从 `xxx.part001` 反推原文件名；没有分片后缀就追加 .merged。</summary>
    private static string DefaultMergeName(string firstPart)
    {
        var name = Path.GetFileName(firstPart);
        var match = Regex.Match(name, @"^(?<base>.+)\.part\d+$", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["base"].Value : name + ".merged";
    }

    private async void OnRunClicked(object? sender, EventArgs e)
    {
        if (IsMergeMode) await RunMergeAsync();
        else await RunSplitAsync();
    }

    private async Task RunSplitAsync()
    {
        if (_splitTarget is null) return;

        var path = _splitTarget.FullPath;
        var byLines = SplitBasisPicker.SelectedIndex == 1;

        if (byLines)
        {
            if (!int.TryParse((ThresholdEntry.Text ?? "").Trim(), out var lines) || lines <= 0)
            {
                StatusLabel.Text = "请输入有效的行数。";
                StatusLabel.IsVisible = true;
                return;
            }

            RunButton.IsEnabled = false;
            try
            {
                var parts = await Task.Run(() => FileSplitter.SplitByLines(path, lines));
                await this.SafeAlertAsync("已分割", $"生成 {parts.Count} 个分片。", "好");
                await Navigation.PopAsync();
            }
            catch (Exception ex)
            {
                StatusLabel.Text = "分割失败：" + ex.Message;
                StatusLabel.IsVisible = true;
            }
            finally { RunButton.IsEnabled = true; }
            return;
        }

        if (!double.TryParse((ThresholdEntry.Text ?? "").Trim(), out var mb) || mb <= 0)
        {
            StatusLabel.Text = "请输入有效的大小（MB）。";
            StatusLabel.IsVisible = true;
            return;
        }

        var chunk = (long)(mb * Megabyte);
        RunButton.IsEnabled = false;
        try
        {
            var parts = await Task.Run(() => FileSplitter.SplitBySize(path, chunk));
            await this.SafeAlertAsync("已分割", $"生成 {parts.Count} 个分片。", "好");
            await Navigation.PopAsync();
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "分割失败：" + ex.Message;
            StatusLabel.IsVisible = true;
        }
        finally { RunButton.IsEnabled = true; }
    }

    private async Task RunMergeAsync()
    {
        var output = (OutputEntry.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(output) || output.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            StatusLabel.Text = "请输入有效的输出文件名。";
            StatusLabel.IsVisible = true;
            return;
        }

        var directory = Path.GetDirectoryName(_parts[0].FullPath) ?? "";
        var target = Path.Combine(directory, output);

        var overwrite = File.Exists(target);
        if (overwrite)
        {
            var confirmed = await this.SafeConfirmAsync("覆盖确认", $"{output} 已存在，确定覆盖？", "覆盖", "取消");
            if (!confirmed) return;
        }

        var sources = _parts.Select(p => p.FullPath).ToList();
        RunButton.IsEnabled = false;
        try
        {
            var written = await Task.Run(() => FileSplitter.Merge(sources, target));
            await this.SafeAlertAsync("已合并", $"{output} · {FileOperations.FormatSize(written)}", "好");
            await Navigation.PopAsync();
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "合并失败：" + ex.Message;
            StatusLabel.IsVisible = true;
        }
        finally { RunButton.IsEnabled = true; }
    }
}
