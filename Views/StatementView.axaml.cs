using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using BKMsystem.Models;
using BKMsystem.Services;

namespace BKMsystem.Views;

public partial class StatementView : UserControl
{
    private readonly DatabaseService _dbService = new();
    private readonly DxfStorageService _dxfStorageService = new();

    public List<StatementItem> RootNodes { get; private set; } = new();

    public StatementView()
    {
        InitializeComponent();
        DataContext = this;
        LoadDataFromDatabase();
        // Автоматическое обновление ведомости при изменении таблицы Details, а также
        // норм трудоёмкости (UnitLaborNorms) — они пишутся в той же транзакции при
        // импорте, но это отдельная таблица со своим NOTIFY.
        DatabaseService.OnDatabaseUpdated += (tableName) =>
        {
            if (tableName.Equals("details", StringComparison.OrdinalIgnoreCase) ||
                tableName.Equals("unitlabornorms", StringComparison.OrdinalIgnoreCase))
            {
                LoadDataFromDatabase();
            }
        };
    }

    private async void LoadDataFromDatabase()
    {
        var treeData = await Task.Run(() =>
        {
            _dbService.InitializeDatabase();
            return _dbService.GetStatementTreeFromDb();
        });

        RootNodes = treeData;
        Tree.ItemsSource = RootNodes;
    }

    private async void OnTreeVievDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is TreeView treeView && treeView.SelectedItem is StatementItem selectedItem)
        {
            if (selectedItem.IsGroup || selectedItem.DetailData == null)
                return;

            Cursor = new Cursor(StandardCursorType.Wait);

            try
            {
                // Поиск DXF в фоновом потоке
                string? dxfPath = await Task.Run(() =>
                    _dxfStorageService.FindDxfPath(selectedItem.DetailData, selectedItem.ProductGroupName ?? "")
                );

                if (!string.IsNullOrEmpty(dxfPath) && File.Exists(dxfPath))
                {
                    var parentWindow = TopLevel.GetTopLevel(this) as Window;
                    var viewerWindow = new DxfViewerWindow(dxfPath);

                    if (parentWindow != null)
                    {
                        viewerWindow.Show(parentWindow);
                    }
                    else
                    {
                        viewerWindow.Show();
                    }
                }
            }
            finally
            {
                Cursor = Cursor.Default;
            }
        }
    }
}
