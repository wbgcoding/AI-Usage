namespace AiUsage.Services;

/// <summary>The real save-file dialog both CSV exports default to; tests inject their own path source.</summary>
internal static class CsvSaveDialog
{
    internal static string? Show(string suggestedFileName)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = suggestedFileName,
            Filter = LocalizationService.Instance["Dialog.Filter.Csv"],
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
