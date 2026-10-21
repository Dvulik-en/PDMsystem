using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using BKMsystem.Models;
using BKMsystem.Services;
using BKMsystem.Views;

namespace BKMsystem.ViewModels;

/// <summary>
/// Вкладка "Раскрои" (оператор плазмы).
/// Дерево: Заказ → Изделие/Узел → Лист N мм → УП (.MPG) + один PDF.
/// «Выгрузить на флешку» — только на узле листа, только .MPG.
/// «Выполнено» — только на УП после выгрузки на флешку.
/// </summary>
public partial class NestingViewModel : ViewModelBase
{
    private readonly NestingRepository _repo = new();
    private string? _lastPdfTempPath;

    public ObservableCollection<NestingTreeNode> Tree { get; } = new();

    private NestingTreeNode? _selectedNode;
    public NestingTreeNode? SelectedNode
    {
        get => _selectedNode;
        set
        {
            _selectedNode = value;
            OnPropertyChanged();
            UpdatePreview();
            UpdateActionButtons();
        }
    }

    private string _previewTitle = "Выберите карту раскроя из дерева слева";
    public string PreviewTitle
    {
        get => _previewTitle;
        set { _previewTitle = value; OnPropertyChanged(); }
    }

    private string _previewText = "";
    public string PreviewText
    {
        get => _previewText;
        set { _previewText = value; OnPropertyChanged(); }
    }

    private string? _mpgFilePath;
    public string? MpgFilePath
    {
        get => _mpgFilePath;
        set { _mpgFilePath = value; OnPropertyChanged(); }
    }

    private string? _pdfFilePath;
    public string? PdfFilePath
    {
        get => _pdfFilePath;
        set { _pdfFilePath = value; OnPropertyChanged(); }
    }

    private bool _isMpgVisible;
    public bool IsMpgVisible
    {
        get => _isMpgVisible;
        set { _isMpgVisible = value; OnPropertyChanged(); }
    }

    private bool _isPdfVisible;
    public bool IsPdfVisible
    {
        get => _isPdfVisible;
        set { _isPdfVisible = value; OnPropertyChanged(); }
    }

    private bool _isTextVisible;
    public bool IsTextVisible
    {
        get => _isTextVisible;
        set { _isTextVisible = value; OnPropertyChanged(); }
    }

    private int _pdfPageIndex;
    public int PdfPageIndex
    {
        get => _pdfPageIndex;
        set { _pdfPageIndex = value; OnPropertyChanged(); OnPropertyChanged(nameof(PdfPageInfo)); }
    }

    private int _pdfPageCount = 1;
    public int PdfPageCount
    {
        get => _pdfPageCount;
        set { _pdfPageCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(PdfPageInfo)); }
    }

    private double _pdfZoom = 1.0;
    public double PdfZoom
    {
        get => _pdfZoom;
        set { _pdfZoom = value; OnPropertyChanged(); OnPropertyChanged(nameof(PdfZoomText)); }
    }

    public string PdfPageInfo => $"стр. {PdfPageIndex + 1} / {PdfPageCount}";
    public string PdfZoomText => $"{PdfZoom * 100:0}%";

    private string _statusMessage = "";
    public string StatusMessage
    {
        get => _statusMessage;
        set { _statusMessage = value; OnPropertyChanged(); }
    }

    private bool _isExportUsbVisible;
    public bool IsExportUsbVisible
    {
        get => _isExportUsbVisible;
        set { _isExportUsbVisible = value; OnPropertyChanged(); }
    }

    private bool _isMarkDoneVisible;
    public bool IsMarkDoneVisible
    {
        get => _isMarkDoneVisible;
        set { _isMarkDoneVisible = value; OnPropertyChanged(); }
    }

    private bool _isMarkDoneEnabled;
    public bool IsMarkDoneEnabled
    {
        get => _isMarkDoneEnabled;
        set { _isMarkDoneEnabled = value; OnPropertyChanged(); }
    }

    public NestingViewModel()
    {
        _repo.EnsureTables();
        Refresh();

        DatabaseService.OnDatabaseUpdated += tableName =>
        {
            if (tableName.Equals("cutting_sheets", StringComparison.OrdinalIgnoreCase))
            {
                Dispatcher.UIThread.Post(Refresh);
            }
        };

        // Бейдж "⏱ в работе" должен "тикать" сам по себе, а не только при полном обновлении
        // дерева (Refresh идёт в базу и сбрасывает разворот узлов). Раз в минуту просто
        // пересчитываем текст бейджа на уже построенных узлах — без похода в базу.
        _timeInWorkTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _timeInWorkTimer.Tick += (_, _) => RecomputeTimeInWorkBadges(Tree);
        _timeInWorkTimer.Start();
    }

    private readonly DispatcherTimer _timeInWorkTimer;

    private static void RecomputeTimeInWorkBadges(IEnumerable<NestingTreeNode> nodes)
    {
        foreach (var n in nodes)
        {
            if (n.NodeType == "Mpg") n.RecomputeTimeInWork();
            if (n.Children.Count > 0) RecomputeTimeInWorkBadges(n.Children);
        }
    }

    [RelayCommand]
    private void Refresh()
    {
        var expandedKeys = CaptureExpandedKeys(Tree);

        Tree.Clear();
        foreach (var node in _repo.GetSheetsTree("active"))
            Tree.Add(node);

        RestoreExpandedKeys(Tree, expandedKeys);
        UpdateActionButtons();
    }

    /// <summary>Собирает ключи развёрнутых узлов дерева — чтобы восстановить их после
    /// перестроения (Tree.Clear()/Add() создаёт новые объекты, и без этого дерево
    /// каждый раз сворачивалось бы обратно).</summary>
    private static HashSet<string> CaptureExpandedKeys(IEnumerable<NestingTreeNode> nodes)
    {
        var keys = new HashSet<string>();
        void Walk(IEnumerable<NestingTreeNode> level, string parentPath)
        {
            foreach (var n in level)
            {
                string path = parentPath + "/" + n.NodeType + ":" + n.Title;
                if (n.IsExpanded) keys.Add(path);
                if (n.Children.Count > 0) Walk(n.Children, path);
            }
        }
        Walk(nodes, "");
        return keys;
    }

    private static void RestoreExpandedKeys(IEnumerable<NestingTreeNode> nodes, HashSet<string> expandedKeys)
    {
        void Walk(IEnumerable<NestingTreeNode> level, string parentPath)
        {
            foreach (var n in level)
            {
                string path = parentPath + "/" + n.NodeType + ":" + n.Title;
                if (expandedKeys.Contains(path)) n.IsExpanded = true;
                if (n.Children.Count > 0) Walk(n.Children, path);
            }
        }
        Walk(nodes, "");
    }

    private void UpdateActionButtons()
    {
        var node = SelectedNode;
        IsExportUsbVisible = node?.NodeType == "MaterialFolder"
                             && node.RelatedSheetIds.Count > 0;

        bool isMpg = node?.NodeType == "Mpg" && (node.SheetId > 0);
        IsMarkDoneVisible = isMpg;
        // Активна только после выгрузки на флешку и пока статус не «Выполнен»
        IsMarkDoneEnabled = isMpg
                            && node!.UsbExported
                            && !string.Equals(node.Status, "Выполнен", StringComparison.OrdinalIgnoreCase);
    }

    private void UpdatePreview()
    {
        IsMpgVisible = false;
        IsPdfVisible = false;
        IsTextVisible = false;
        PreviewText = "";

        var node = SelectedNode;
        if (node == null)
        {
            PreviewTitle = "Выберите карту раскроя из дерева слева";
            return;
        }

        switch (node.NodeType)
        {
            case "Order":
            case "Product":
                PreviewTitle = node.Title;
                PreviewText = "Разверните узел и выберите лист (толщину) или файл УП / PDF.";
                IsTextVisible = true;
                return;

            case "MaterialFolder":
                PreviewTitle = node.Title;
                PreviewText =
                    $"Карт УП: {node.RelatedSheetIds.Count}\n" +
                    $"Флешка: {(node.UsbExported ? "уже выгружали" : "ещё не выгружали")}\n\n" +
                    "Нажмите «Выгрузить на флешку» — на носитель уйдут только файлы .MPG.\n" +
                    "После выгрузки откройте нужную УП и отметьте «Выполнено».";
                IsTextVisible = true;
                return;
        }

        if (node.FileId <= 0) return;

        var file = _repo.GetFile(node.FileId);
        if (file == null)
        {
            PreviewTitle = node.FileName;
            PreviewText = "Не удалось прочитать файл из базы (нет связи с сервером).";
            IsTextVisible = true;
            return;
        }

        string tmpDir = Path.Combine(Path.GetTempPath(), "BKMsystem", "preview");
        Directory.CreateDirectory(tmpDir);
        string tmpPath = Path.Combine(tmpDir, file.Value.FileName);

        // Не перезаписываем тот же PDF лишний раз — иначе просмотрщик снова открывает файл
        bool samePdf = node.NodeType == "Pdf"
                       && string.Equals(_lastPdfTempPath, tmpPath, StringComparison.OrdinalIgnoreCase)
                       && File.Exists(tmpPath)
                       && new FileInfo(tmpPath).Length == file.Value.Data.Length;
        if (!samePdf)
            File.WriteAllBytes(tmpPath, file.Value.Data);

        if (node.NodeType == "Mpg")
        {
            PreviewTitle = $"УП {node.SheetName} — анимация реза (зелёное — прорезано, пунктир — холостые)";
            MpgFilePath = tmpPath;
            IsMpgVisible = true;
        }
        else if (node.NodeType == "Pdf")
        {
            PreviewTitle = $"Отчёт раскроя: {node.SheetName}";
            if (!string.Equals(PdfFilePath, tmpPath, StringComparison.OrdinalIgnoreCase))
            {
                PdfFilePath = tmpPath;
                PdfPageIndex = 0;
                PdfZoom = 1.0;
                PdfPageCount = Math.Max(1, PdfViewerControl.GetPageCount(tmpPath));
            }
            _lastPdfTempPath = tmpPath;
            IsPdfVisible = true;
        }
        else
        {
            PreviewTitle = node.FileName;
            PreviewText = "Этот тип файла не отображается у оператора.";
            IsTextVisible = true;
        }
    }

    [RelayCommand]
    private void NextPdfPage()
    {
        if (PdfPageIndex < PdfPageCount - 1) PdfPageIndex++;
    }

    [RelayCommand]
    private void PrevPdfPage()
    {
        if (PdfPageIndex > 0) PdfPageIndex--;
    }

    [RelayCommand] private void ZoomInPdf()   => PdfZoom = Math.Min(PdfZoom * 1.25, 6.0);
    [RelayCommand] private void ZoomOutPdf()  => PdfZoom = Math.Max(PdfZoom / 1.25, 0.3);
    [RelayCommand] private void ResetPdfZoom(){ PdfZoom = 1.0; }

    [RelayCommand]
    private void MarkDone()
    {
        if (!IsMarkDoneEnabled)
        {
            StatusMessage = SelectedNode?.UsbExported == true
                ? "Эта УП уже отмечена как выполненная."
                : "Сначала выгрузите УП на флешку (выберите лист и нажмите «Выгрузить на флешку»).";
            return;
        }

        int sheetId = SelectedNode?.SheetId ?? 0;
        if (sheetId <= 0)
        {
            StatusMessage = "Сначала выберите файл УП (.MPG) в дереве.";
            return;
        }

        bool online = _repo.SetStatusSafe(sheetId, "Выполнен");
        StatusMessage = online
            ? $"УП {SelectedNode?.SheetName} выполнена. Когда все УП листа отмечены — лист/узел/заказ исчезнут из очереди."
            : "Нет связи: статус сохранён локально и отправится при появлении сети.";

        // Сразу гасим кнопку на текущем узле
        if (SelectedNode != null)
        {
            SelectedNode.Status = "Выполнен";
            IsMarkDoneEnabled = false;
        }
        Refresh();
    }

    [RelayCommand]
    private async Task ExportUsbAsync()
    {
        if (SelectedNode?.NodeType != "MaterialFolder" || SelectedNode.RelatedSheetIds.Count == 0)
        {
            StatusMessage = "Выберите лист (например «Лист 4 мм») в дереве — кнопка выгрузки только для листа.";
            return;
        }

        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop ||
            desktop.MainWindow == null)
            return;

        var folders = await desktop.MainWindow.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Выберите флешку / папку для выгрузки УП (.MPG)"
        });
        if (folders.Count == 0) return;

        string targetDir = folders[0].Path.LocalPath;
        var sheetIds = SelectedNode.RelatedSheetIds.ToList();

        try
        {
            int files = await Task.Run(() => _repo.ExportSheetMpgOnly(sheetIds, targetDir));
            _repo.MarkUsbExported(sheetIds);

            // Обновляем флаги в текущем дереве до Refresh
            SelectedNode.UsbExported = true;
            foreach (var child in SelectedNode.Children.Where(c => c.NodeType == "Mpg"))
                child.UsbExported = true;

            StatusMessage = $"На флешку выгружено УП: {files} шт. → {targetDir}. Теперь можно отмечать «Выполнено» по каждой УП.";
            UpdateActionButtons();
            Refresh();
        }
        catch (Exception ex)
        {
            StatusMessage = "Ошибка выгрузки: " + ex.Message;
        }
    }
}