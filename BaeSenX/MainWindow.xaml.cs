using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Input;

namespace BaeSenX;

public partial class MainWindow : Window
{
    private ScriptSummary? _scriptSummary;

    public MainWindow()
    {
        InitializeComponent();
        AppendLog("BaeSenX 中文化工具已啟動。");
        AppendLog("支援 BSXScript 3.0～3.3；本版本只修改字串表，不修改腳本指令。");
    }

    private async void BrowseScriptButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "選擇 Bishop 遊戲的 bsxx.dat",
            Filter = "Bishop BSXX 腳本 (bsxx.dat)|bsxx.dat|DAT 檔案 (*.dat)|*.dat|所有檔案 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
            return;

        ScriptPathTextBox.Text = dialog.FileName;
        _scriptSummary = null;
        ExportButton.IsEnabled = false;
        ImportButton.IsEnabled = false;
        SetBusy(true, "正在檢查腳本…");

        try
        {
            ScriptSummary summary = await Task.Run(() => TranslationService.Inspect(dialog.FileName));
            _scriptSummary = summary;
            ScriptInfoTextBlock.Text =
                $"版本：{summary.Version}    角色名稱：{summary.CharacterCount:N0} 筆    對話／選項：{summary.MessageCount:N0} 筆    檔案：{FormatBytes(summary.FileSize)}";
            ExportButton.IsEnabled = true;
            ImportButton.IsEnabled = true;
            AppendLog($"已讀取：{dialog.FileName}");
            AppendLog($"格式確認：{summary.Version}；角色名稱 {summary.CharacterCount:N0} 筆；訊息表 {summary.MessageCount:N0} 筆。");
            StatusTextBlock.Text = "腳本已就緒";
        }
        catch (Exception exception)
        {
            ScriptInfoTextBlock.Text = "無法讀取此腳本。請確認檔案完整，且格式為 BSXScript 3.0～3.3。";
            AppendLog("讀取失敗：" + exception.Message);
            MessageBox.Show(this, exception.Message, "讀取腳本失敗", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusTextBlock.Text = "讀取失敗";
        }
        finally
        {
            SetBusy(false, StatusTextBlock.Text);
        }
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetScriptPath(out string scriptPath))
            return;

        var dialog = new SaveFileDialog
        {
            Title = "匯出翻譯文字檔",
            Filter = "BaeSenX 翻譯文字檔 (*.txt)|*.txt|所有檔案 (*.*)|*.*",
            DefaultExt = ".txt",
            AddExtension = true,
            FileName = Path.GetFileName(scriptPath) + ".txt",
            InitialDirectory = Path.GetDirectoryName(scriptPath),
            OverwritePrompt = true
        };

        if (dialog.ShowDialog(this) != true)
            return;

        SetBusy(true, "正在匯出文字…");
        try
        {
            ScriptSummary summary = await Task.Run(() =>
                TranslationService.ExportText(scriptPath, dialog.FileName));

            AppendLog("翻譯文字檔已匯出：" + dialog.FileName);
            AppendLog($"匯出項目：角色名稱 {summary.CharacterCount:N0} 筆；對話／選項 {summary.MessageCount:N0} 筆。");
            AppendLog("請只修改每筆資料中以 ◆ 開頭的翻譯行；◇ 開頭的是原文。");
            StatusTextBlock.Text = "匯出完成";
            MessageBox.Show(this,
                "翻譯文字檔已匯出。\n\n◇ 行是原文，◆ 行是翻譯內容。請使用 UTF-8 編輯，並保留索引標記。",
                "匯出完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            AppendLog("匯出失敗：" + exception.Message);
            MessageBox.Show(this, exception.Message, "匯出失敗", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusTextBlock.Text = "匯出失敗";
        }
        finally
        {
            SetBusy(false, StatusTextBlock.Text);
        }
    }

    private async void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetScriptPath(out string scriptPath))
            return;

        var translationDialog = new OpenFileDialog
        {
            Title = "選擇已翻譯的文字檔",
            Filter = "翻譯文字檔 (*.txt)|*.txt|所有檔案 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (translationDialog.ShowDialog(this) != true)
            return;

        var outputDialog = new SaveFileDialog
        {
            Title = "另存翻譯後的 BSXX 腳本",
            Filter = "BSXX 腳本 (*.dat)|*.dat|所有檔案 (*.*)|*.*",
            DefaultExt = ".dat",
            AddExtension = true,
            FileName = Path.GetFileNameWithoutExtension(scriptPath) + "_zh.dat",
            InitialDirectory = Path.GetDirectoryName(scriptPath),
            OverwritePrompt = true
        };

        if (outputDialog.ShowDialog(this) != true)
            return;

        if (string.Equals(Path.GetFullPath(scriptPath), Path.GetFullPath(outputDialog.FileName),
                StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "輸出檔案不能覆蓋原始 bsxx.dat。請選擇其他檔名。",
                "不允許覆蓋來源檔", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SetBusy(true, "正在匯入並重建腳本…");
        try
        {
            ImportSummary summary = await Task.Run(() => TranslationService.ImportText(
                scriptPath, translationDialog.FileName, outputDialog.FileName));

            AppendLog("翻譯匯入完成：" + outputDialog.FileName);
            AppendLog($"實際更新：角色名稱 {summary.UpdatedCharacterCount:N0} 筆；對話／選項 {summary.UpdatedMessageCount:N0} 筆。");
            AppendLog($"重新讀取驗證成功；版本 {summary.Version}；輸出大小 {FormatBytes(summary.OutputSize)}。");
            AppendLog("Opcode 區段已逐位元組比對，確認與原始檔相同。");
            StatusTextBlock.Text = "匯入完成";
            MessageBox.Show(this,
                $"翻譯腳本已建立：\n{outputDialog.FileName}\n\n" +
                $"更新角色名稱：{summary.UpdatedCharacterCount:N0} 筆\n" +
                $"更新對話／選項：{summary.UpdatedMessageCount:N0} 筆\n\n" +
                "原始 bsxx.dat 未被覆蓋；Opcode 區段保持不變。",
                "匯入完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            AppendLog("匯入失敗：" + exception.Message);
            MessageBox.Show(this, exception.Message, "匯入失敗", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusTextBlock.Text = "匯入失敗";
        }
        finally
        {
            SetBusy(false, StatusTextBlock.Text);
        }
    }

    private bool TryGetScriptPath(out string scriptPath)
    {
        scriptPath = ScriptPathTextBox.Text.Trim();
        if (_scriptSummary is null || scriptPath.Length == 0 || !File.Exists(scriptPath))
        {
            MessageBox.Show(this, "請先選擇有效的 bsxx.dat。", "尚未選擇腳本",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        return true;
    }

    private void SetBusy(bool busy, string status)
    {
        BrowseScriptButton.IsEnabled = !busy;
        ExportButton.IsEnabled = !busy && _scriptSummary is not null;
        ImportButton.IsEnabled = !busy && _scriptSummary is not null;
        Mouse.OverrideCursor = busy ? System.Windows.Input.Cursors.Wait : null;
        StatusTextBlock.Text = status;
    }

    private void AppendLog(string message)
    {
        LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        LogTextBox.ScrollToEnd();
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = new[] { "B", "KB", "MB", "GB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.##} {units[unit]}";
    }
}
