using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Agate.Models;

/// <summary>WebDAV 远端目录列表中的一项。路径均相对远端根，'/' 分隔，无首尾斜杠。</summary>
public sealed class RemoteItem : INotifyPropertyChanged
{
    public string Name { get; set; } = string.Empty;

    /// <summary>相对远端根的路径（'/' 分隔，无首尾斜杠）。目录与文件共用。</summary>
    public string RelativePath { get; set; } = string.Empty;

    public bool IsDirectory { get; set; }

    public bool IsFile => !IsDirectory;

    public long Size { get; set; }

    public DateTime LastModified { get; set; }

    /// <summary>右侧尺寸文字：目录不显示，文件按 B/KB/MB/GB 自适应。</summary>
    public string SizeText
    {
        get
        {
            if (IsDirectory) return string.Empty;

            const long kb = 1024, mb = 1024 * 1024, gb = 1024L * 1024 * 1024;
            return Size switch
            {
                >= gb => $"{Size / (double)gb:F1} GB",
                >= mb => $"{Size / (double)mb:F1} MB",
                >= kb => $"{Size / (double)kb:F1} KB",
                _ => $"{Size} B"
            };
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
