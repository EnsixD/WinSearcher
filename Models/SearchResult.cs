using System.Windows.Media;

namespace WinSearcher.Models;

public enum ResultType
{
    App,
    File,
    Folder,
    Shortcut,
    Recent
}

public class SearchResult
{
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public ImageSource? Icon { get; set; }
    public ResultType Type { get; set; }

    public string TypeLabel => Type switch
    {
        ResultType.App => "APP",
        ResultType.File => "FILE",
        ResultType.Folder => "FOLDER",
        ResultType.Shortcut => "APP",
        ResultType.Recent => "RECENT",
        _ => ""
    };
}
