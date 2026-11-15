using System.Collections.ObjectModel;
using Pebble.Models;
using Pebble.Services;

namespace Pebble;

public partial class BatchRenamePage : ContentPage
{
    private static readonly Color OkColorLight = Color.FromArgb("#14161F");
    private static readonly Color OkColorDark = Color.FromArgb("#E9ECF5");
    private static readonly Color BadColorLight = Color.FromArgb("#C0392B");
    private static readonly Color BadColorDark = Color.FromArgb("#FF7A6B");

    private readonly List<FileItem> _items;
    private readonly ObservableCollection<PreviewRow> _preview = new();
    private List<RenamePlanItem> _plan = new();

    private sealed class PreviewRow
    {
        public string Original { get; init; } = "";
        public string Target { get; init; } = "";
        public Color TargetColor { get; init; } = Colors.Gray;
    }

    public BatchRenamePage(IReadOnlyList<FileItem> items)
    {
        InitializeComponent();
        _items = items.ToList();
        PreviewList.ItemsSource = _preview;

        TemplateEntry.Text = "{name}_{n:000}{ext}";
        StartEntry.Text = "1";
        StepEntry.Text = "1";

        RefreshPreview();
    }

    private RenameMode CurrentMode => ModePicker.SelectedIndex == 1 ? RenameMode.Regex : RenameMode.Sequence;

    private void OnModeChanged(object? sender, EventArgs e)
    {
        var regex = CurrentMode == RenameMode.Regex;
        SequenceSection.IsVisible = !regex;
        RegexSection.IsVisible = regex;
        RefreshPreview();
    }

    private void OnInputChanged(object? sender, EventArgs e) => RefreshPreview();

    private void RefreshPreview()
    {
        var paths = _items.Select(i => i.FullPath).ToList();

        _plan = CurrentMode == RenameMode.Regex
            ? BatchRenamer.PlanRegex(paths, PatternEntry.Text ?? "", ReplacementEntry.Text ?? "", IncludeExtSwitch.IsToggled)
            : BatchRenamer.PlanSequence(paths, TemplateEntry.Text ?? "", ParseInt(StartEntry.Text, 1), ParseInt(StepEntry.Text, 1));

        var dark = Application.Current?.RequestedTheme == AppTheme.Dark ||
                   Application.Current?.UserAppTheme == AppTheme.Dark;
        var ok = dark ? OkColorDark : OkColorLight;
        var bad = dark ? BadColorDark : BadColorLight;

        _preview.Clear();
        foreach (var item in _plan)
        {
            _preview.Add(new PreviewRow
            {
                Original = item.OriginalName,
                Target = item.Error ?? item.NewName,
                TargetColor = item.Error is null ? ok : bad
            });
        }

        var ready = _plan.Count(i => i.CanApply);
        var errors = _plan.Count(i => i.Error is not null);
        SummaryLabel.Text = errors > 0
            ? $"{_items.Count} 项 · {ready} 项可改 · {errors} 项有冲突"
            : $"{_items.Count} 项 · 全部可改";
        ApplyButton.IsEnabled = ready > 0 && errors == 0;
        StatusLabel.IsVisible = false;
    }

    private static int ParseInt(string? text, int fallback)
        => int.TryParse((text ?? "").Trim(), out var value) ? value : fallback;

    private async void OnApplyClicked(object? sender, EventArgs e)
    {
        if (_plan.Count(i => i.CanApply) == 0)
        {
            StatusLabel.Text = "没有可重命名的项。";
            StatusLabel.IsVisible = true;
            return;
        }

        var confirmed = await this.SafeConfirmAsync("批量重命名",
            $"将重命名 {_plan.Count(i => i.CanApply)} 项，确定继续？", "重命名", "取消");
        if (!confirmed) return;

        ApplyButton.IsEnabled = false;
        try
        {
            var moved = await Task.Run(() => BatchRenamer.Apply(_plan));
            await this.SafeAlertAsync("完成", $"已重命名 {moved} 项。", "好");
            await Navigation.PopAsync();
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "重命名失败：" + ex.Message;
            StatusLabel.IsVisible = true;
            ApplyButton.IsEnabled = true;
        }
    }
}
