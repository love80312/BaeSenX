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
    private LegacyExportSummary? _legacySummary;
    private bool _isBusy;

    public MainWindow()
    {
        InitializeComponent();
        AppendLog("BaeSenX 中文化工具已啟動。");
        AppendLog("舊版 BSXScript_Tool DAT → TXT 與新版 BaeSenX TXT → DAT 為兩個獨立功能。");
        AppendLog("舊版轉換只讀取舊版 DAT，不需要原始腳本，也不會自動比對或合併。");
        AppendLog("新版功能仍由 TranslationService 處理，不受舊版格式解析影響。");
    }

    private async void BrowseLegacyDatButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "選擇舊版 BSXScript_Tool 打包的 DAT",
            Filter = "DAT 檔案 (*.dat)|*.dat|所有檔案 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
            return;

        LegacyDatPathTextBox.Text = dialog.FileName;
        _legacySummary = null;
        RefreshControlState();
        SetBusy(true, "正在檢查舊版 DAT…");

        try
        {
            LegacyExportSummary summary = await Task.Run(() => LegacyExportService.Inspect(dialog.FileName));
            _legacySummary = summary;
            LegacyDatInfoTextBlock.Text =
                $"格式：{summary.Version}    角色名稱：{summary.CharacterCount:N0} 筆    對話／選項：{summary.MessageCount:N0} 筆    檔案：{FormatBytes(summary.FileSize)}\n" +
                $"角色字串區：標頭 {summary.DeclaredCharacterBlockSize:N0} bytes／實際邊界 {summary.AvailableCharacterBlockSize:N0} bytes；" +
                $"訊息字串區：標頭 {summary.DeclaredMessageBlockSize:N0} bytes／實際至 EOF {summary.AvailableMessageBlockSize:N0} bytes。";

            AppendLog("已讀取舊版 DAT：" + dialog.FileName);
            AppendLog($"舊版資料：角色名稱 {summary.CharacterCount:N0} 筆；訊息／選項 {summary.MessageCount:N0} 筆。");
            AppendLog($"角色區標頭／實際大小：{summary.DeclaredCharacterBlockSize:N0}／{summary.AvailableCharacterBlockSize:N0} bytes；尾端資料 {summary.CharacterTrailingByteCount:N0} bytes。");
            AppendLog($"訊息區標頭／實際大小：{summary.DeclaredMessageBlockSize:N0}／{summary.AvailableMessageBlockSize:N0} bytes；尾端資料 {summary.MessageTrailingByteCount:N0} bytes。");
            if (!summary.CharacterBlockSizeMatchesAvailable || !summary.MessageBlockSizeMatchesAvailable)
                AppendLog("注意：舊版 DAT 的字串區大小欄位與實際區域不一致；已依相鄰區塊／EOF 驗證所有索引及 UTF-16LE 結尾，不會把標頭大小當作實際邊界。");
            StatusTextBlock.Text = "舊版 DAT 已就緒";
        }
        catch (Exception exception)
        {
            _legacySummary = null;
            LegacyDatInfoTextBlock.Text = "無法安全解析此舊版 DAT。請確認檔案完整，且格式為 BSXScript 3.0～3.3。";
            AppendLog("舊版 DAT 讀取失敗：" + exception.Message);
            MessageBox.Show(this, exception.Message, "舊版 DAT 讀取失敗", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusTextBlock.Text = "舊版 DAT 讀取失敗";
        }
        finally
        {
            SetBusy(false, StatusTextBlock.Text);
        }
    }

    private async void ExportLegacyButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetLegacyDatPath(out string legacyDatPath))
            return;

        var dialog = new SaveFileDialog
        {
            Title = "匯出舊版 DAT 參考文字檔",
            Filter = "舊版 DAT 參考文字檔 (*.txt)|*.txt|所有檔案 (*.*)|*.*",
            DefaultExt = ".txt",
            AddExtension = true,
            FileName = Path.GetFileNameWithoutExtension(legacyDatPath) + "_legacy.txt",
            InitialDirectory = Path.GetDirectoryName(legacyDatPath),
            OverwritePrompt = true
        };

        if (dialog.ShowDialog(this) != true)
            return;

        SetBusy(true, "正在匯出舊版 DAT 文字…");
        try
        {
            LegacyExportSummary summary = await Task.Run(() =>
                LegacyExportService.ExportText(legacyDatPath, dialog.FileName));

            AppendLog("舊版 DAT 參考 TXT 已匯出：" + dialog.FileName);
            AppendLog($"匯出項目：角色名稱 {summary.CharacterCount:N0} 筆；對話／選項 {summary.MessageCount:N0} 筆。");
            AppendLog($"尾端資料已寫入 TXT 註解：角色區 {summary.CharacterTrailingByteCount:N0} bytes；訊息區 {summary.MessageTrailingByteCount:N0} bytes。");
            AppendLog("此 TXT 僅供人工比對；請自行將需要的翻譯整理到新版 BaeSenX TXT，再使用新版功能打包。");
            StatusTextBlock.Text = "舊版 TXT 匯出完成";

            MessageBox.Show(this,
                "舊版 DAT 已直接匯出為參考 TXT。\n\n" +
                $"角色名稱：{summary.CharacterCount:N0} 筆\n" +
                $"對話／選項：{summary.MessageCount:N0} 筆\n\n" +
                "此功能不會修改 DAT，也不會與新版 TXT 自動比對或合併。\n" +
                "請自行比對後，以新版 BaeSenX TXT 執行打包。",
                "舊版 DAT 匯出完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            AppendLog("舊版 DAT 匯出失敗：" + exception.Message);
            MessageBox.Show(this, exception.Message, "舊版 DAT 匯出失敗", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusTextBlock.Text = "舊版 DAT 匯出失敗";
        }
        finally
        {
            SetBusy(false, StatusTextBlock.Text);
        }
    }

    private async void BrowseScriptButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "選擇新版 BaeSenX 使用的原始 bsxx.dat",
            Filter = "Bishop BSXX 腳本 (bsxx.dat)|bsxx.dat|DAT 檔案 (*.dat)|*.dat|所有檔案 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
            return;

        ScriptPathTextBox.Text = dialog.FileName;
        _scriptSummary = null;
        RefreshControlState();
        SetBusy(true, "正在檢查新版腳本…");

        try
        {
            ScriptSummary summary = await Task.Run(() => TranslationService.Inspect(dialog.FileName));
            _scriptSummary = summary;
            ScriptInfoTextBlock.Text =
                $"版本：{summary.Version}    角色名稱：{summary.CharacterCount:N0} 筆    對話／選項：{summary.MessageCount:N0} 筆    檔案：{FormatBytes(summary.FileSize)}";
            AppendLog("已讀取新版原始腳本：" + dialog.FileName);
            AppendLog($"新版格式確認：{summary.Version}；角色名稱 {summary.CharacterCount:N0} 筆；訊息表 {summary.MessageCount:N0} 筆。");
            StatusTextBlock.Text = "新版腳本已就緒";
        }
        catch (Exception exception)
        {
            _scriptSummary = null;
            ScriptInfoTextBlock.Text = "無法讀取此新版腳本。請確認檔案完整，且格式為 BSXScript 3.0～3.3。";
            AppendLog("新版腳本讀取失敗：" + exception.Message);
            MessageBox.Show(this, exception.Message, "新版腳本讀取失敗", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusTextBlock.Text = "新版腳本讀取失敗";
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
            Title = "匯出新版 BaeSenX 翻譯文字檔",
            Filter = "BaeSenX 翻譯文字檔 (*.txt)|*.txt|所有檔案 (*.*)|*.*",
            DefaultExt = ".txt",
            AddExtension = true,
            FileName = Path.GetFileName(scriptPath) + ".txt",
            InitialDirectory = Path.GetDirectoryName(scriptPath),
            OverwritePrompt = true
        };

        if (dialog.ShowDialog(this) != true)
            return;

        SetBusy(true, "正在匯出新版翻譯文字…");
        try
        {
            ScriptSummary summary = await Task.Run(() =>
                TranslationService.ExportText(scriptPath, dialog.FileName));

            AppendLog("新版 BaeSenX 翻譯文字檔已匯出：" + dialog.FileName);
            AppendLog($"新版匯出項目：角色名稱 {summary.CharacterCount:N0} 筆；對話／選項 {summary.MessageCount:N0} 筆。");
            AppendLog("◇ 行是原文，◆ 行是翻譯內容。請自行將舊版 TXT 中需要的翻譯整理到這份新版 TXT。");
            StatusTextBlock.Text = "新版 TXT 匯出完成";
            MessageBox.Show(this,
                "新版 BaeSenX 翻譯文字檔已匯出。\n\n" +
                "◇ 行是原文，◆ 行是翻譯內容。\n" +
                "請自行比對舊版參考 TXT，將翻譯整理到這份新版 TXT。",
                "新版 TXT 匯出完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            AppendLog("新版 TXT 匯出失敗：" + exception.Message);
            MessageBox.Show(this, exception.Message, "新版 TXT 匯出失敗", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusTextBlock.Text = "新版 TXT 匯出失敗";
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
            Title = "選擇已整理完成的新版 BaeSenX TXT",
            Filter = "BaeSenX 翻譯文字檔 (*.txt)|*.txt|所有檔案 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (translationDialog.ShowDialog(this) != true)
            return;

        var outputDialog = new SaveFileDialog
        {
            Title = "另存新版翻譯後的 BSXX 腳本",
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

        SetBusy(true, "正在匯入新版 TXT 並重建腳本…");
        try
        {
            ImportSummary summary = await Task.Run(() => TranslationService.ImportText(
                scriptPath, translationDialog.FileName, outputDialog.FileName));

            AppendLog("新版翻譯匯入並打包完成：" + outputDialog.FileName);
            AppendLog($"實際更新：角色名稱 {summary.UpdatedCharacterCount:N0} 筆；對話／選項 {summary.UpdatedMessageCount:N0} 筆。");
            AppendLog($"重新讀取驗證成功；版本 {summary.Version}；輸出大小 {FormatBytes(summary.OutputSize)}。");
            AppendLog("Opcode 區段已逐位元組比對，確認與原始新版腳本相同。");
            StatusTextBlock.Text = "新版 DAT 打包完成";
            MessageBox.Show(this,
                $"新版翻譯腳本已建立：\n{outputDialog.FileName}\n\n" +
                $"更新角色名稱：{summary.UpdatedCharacterCount:N0} 筆\n" +
                $"更新對話／選項：{summary.UpdatedMessageCount:N0} 筆\n\n" +
                "原始 bsxx.dat 未被覆蓋；Opcode 區段保持不變。",
                "新版 DAT 打包完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            AppendLog("新版 DAT 打包失敗：" + exception.Message);
            MessageBox.Show(this, exception.Message, "新版 DAT 打包失敗", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusTextBlock.Text = "新版 DAT 打包失敗";
        }
        finally
        {
            SetBusy(false, StatusTextBlock.Text);
        }
    }

    private bool TryGetLegacyDatPath(out string legacyDatPath)
    {
        legacyDatPath = LegacyDatPathTextBox.Text.Trim();
        if (_legacySummary == null || legacyDatPath.Length == 0 || !File.Exists(legacyDatPath))
        {
            MessageBox.Show(this, "請先選擇並成功檢查舊版 BSXScript_Tool DAT。",
                "尚未選擇舊版 DAT", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        return true;
    }

    private bool TryGetScriptPath(out string scriptPath)
    {
        scriptPath = ScriptPathTextBox.Text.Trim();
        if (_scriptSummary == null || scriptPath.Length == 0 || !File.Exists(scriptPath))
        {
            MessageBox.Show(this, "請先選擇並成功檢查新版原始 bsxx.dat。",
                "尚未選擇新版腳本", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        return true;
    }

    private void SetBusy(bool busy, string status)
    {
        _isBusy = busy;
        RefreshControlState();
        Mouse.OverrideCursor = busy ? Cursors.Wait : null;
        StatusTextBlock.Text = status;
    }

    private void RefreshControlState()
    {
        BrowseLegacyDatButton.IsEnabled = !_isBusy;
        ExportLegacyButton.IsEnabled = !_isBusy && _legacySummary != null;
        BrowseScriptButton.IsEnabled = !_isBusy;
        ExportButton.IsEnabled = !_isBusy && _scriptSummary != null;
        ImportButton.IsEnabled = !_isBusy && _scriptSummary != null;
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
