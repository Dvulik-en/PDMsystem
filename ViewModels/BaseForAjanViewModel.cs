using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using BKMsystem.Models;
using BKMsystem.Services;
using BKMsystem.Views;

namespace BKMsystem.ViewModels;

/// <summary>
/// Вкладка "База": архив выполненных карт раскроя.
/// Технолог может вернуть карту во вкладку "Раскрои" (повторный рез) —
/// кнопка "Вернуть в работу" меняет статус обратно на "Новый".
/// </summary>
public partial class BaseForAjanViewModel : ViewModelBase
{
    private readonly NestingRepository _repo = new();
    private readonly UserService _userService = new();
    public bool CanDelete => _userService.CanDeleteData;

    public ObservableCollection<NestingTreeNode> Tree { get; } = new();

    private NestingTreeNode? _selectedNode;
    public NestingTreeNode? SelectedNode
    {
        get => _selectedNode;
        set { _selectedNode = value; OnPropertyChanged(); }
    }

    private string _statusMessage = "";
    public string StatusMessage
    {
        get => _statusMessage;
        set { _statusMessage = value; OnPropertyChanged(); }
    }

    public BaseForAjanViewModel()
    {
        _repo.EnsureTables();
        Refresh();

        DatabaseService.OnDatabaseUpdated += tableName =>
        {
            if (tableName.Equals("cutting_sheets", StringComparison.OrdinalIgnoreCase))
            {
                Refresh();
            }
        };
    }

    [RelayCommand]
    private void Refresh()
    {
        var expandedKeys = CaptureExpandedKeys(Tree);

        Tree.Clear();
        foreach (var node in _repo.GetSheetsTree("done"))
            Tree.Add(node);

        RestoreExpandedKeys(Tree, expandedKeys);
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

    /// <summary>Вернуть карту из архива во вкладку "Раскрои" для повторного реза.</summary>
    [RelayCommand]
    private void ReturnToWork()
    {
        int sheetId = SelectedNode?.SheetId ?? 0;
        if (sheetId <= 0)
        {
            StatusMessage = "Сначала выберите карту раскроя в дереве.";
            return;
        }

        bool online = _repo.SetStatusSafe(sheetId, "Новый");
        StatusMessage = online
            ? $"Карта {SelectedNode?.SheetName} возвращена во вкладку «Раскрои»."
            : "Нет связи: статус сохранён локально и отправится при появлении сети.";
        Refresh();
    }

    /// <summary>Выгрузить файлы карты из архива (канонические имена AJAN).</summary>
    [RelayCommand]
    private async Task ExportAsync()
    {
        int sheetId = SelectedNode?.SheetId ?? 0;
        if (sheetId <= 0)
        {
            StatusMessage = "Сначала выберите карту раскроя в дереве.";
            return;
        }

        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop ||
            desktop.MainWindow == null)
            return;

        var folders = await desktop.MainWindow.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Выберите папку / флешку для выгрузки"
        });
        if (folders.Count == 0) return;

        try
        {
            int files = await Task.Run(() => _repo.ExportSheet(sheetId, folders[0].Path.LocalPath));
            StatusMessage = $"Выгружено файлов: {files} (имена канонические, напр. {SelectedNode?.SheetName}.MPG).";
        }
        catch (Exception ex)
        {
            StatusMessage = "Ошибка выгрузки: " + ex.Message;
        }
    }
    [RelayCommand]
    private async Task DeleteSheetAsync()
    {
        int sheetId = SelectedNode?.SheetId ?? 0;
        if (sheetId <= 0)
        {
            StatusMessage = "Сначала выберите карту раскроя в дереве.";
            return;
        }

        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop ||
            desktop.MainWindow == null)
            return;

        bool confirmed = await ConfirmDialog.Show(desktop.MainWindow,
            $"Удалить карту «{SelectedNode?.SheetName}» безвозвратно? Это действие нельзя отменить.");
        if (!confirmed) return;

        bool ok = _repo.DeleteSheetSafe(sheetId);
        StatusMessage = ok
            ? $"Карта {SelectedNode?.SheetName} удалена."
            : "Нет связи с сервером — удаление невозможно офлайн.";
        Refresh();
    }

}
