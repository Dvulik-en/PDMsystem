using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BKMsystem.Services;
using BKMsystem.Models;

namespace BKMsystem.Views;

public partial class SettingsView : UserControl
{
    private readonly ExcelParserService _parserService = new();
    private readonly DatabaseService _dbService = new();
    private readonly UserRepository _users = new();

    public SettingsView()
    {
        InitializeComponent();

        // Сообщения фоновой синхронизации показываем в статус-строке
        OfflineSyncService.OnSyncMessage += msg =>
        {
            StatusTextBlock.Text = msg;
        };

        // Блок заявок на регистрацию виден только администратору
        if (UserService.CurrentUser?.Role == "Admin")
        {
            AdminPanel.IsVisible = true;
            RefreshPendingList();
        }
    }

    // ======================= Заявки на регистрацию (Admin) =======================

    private void OnRefreshPendingClicked(object? sender, RoutedEventArgs e) => RefreshPendingList();

    private void RefreshPendingList()
    {
        PendingUsersList.Children.Clear();

        var pending = _users.GetPending();
        NoPendingText.IsVisible = pending.Count == 0;

        foreach (var user in pending)
        {
            var row = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#242424")),
                CornerRadius = new Avalonia.CornerRadius(6),
                Padding = new Avalonia.Thickness(10, 8)
            };

            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*, Auto")
            };

            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock
            {
                Text = $"{user.FullName}  ({user.Login})",
                Foreground = Brushes.White,
                FontWeight = FontWeight.SemiBold
            });
            info.Children.Add(new TextBlock
            {
                Text = $"заявка от {user.CreatedAt}",
                Foreground = new SolidColorBrush(Color.Parse("#888888")),
                FontSize = 11
            });
            Grid.SetColumn(info, 0);
            grid.Children.Add(info);

            // Кнопки назначения роли
            var roles = new (string Text, string Role, string Color)[]
            {
                ("ПДО", "Pdo", "#2D5A88"),
                ("Технолог", "Technologist", "#2E7D32"),
                ("Учетник", "Accountant", "#B26A00"),
                ("Оператор", "Operator", "#6A3D9A"),
                ("Админ", "Admin", "#8B0000"),
            };

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center
            };

            foreach (var r in roles)
            {
                var btn = new Button
                {
                    Content = r.Text,
                    Background = new SolidColorBrush(Color.Parse(r.Color)),
                    Foreground = Brushes.White,
                    Padding = new Avalonia.Thickness(10, 5),
                    FontSize = 12,
                    Tag = (user.Id, r.Role, user.FullName)
                };
                btn.Click += OnAssignRoleClicked;
                buttons.Children.Add(btn);
            }

            Grid.SetColumn(buttons, 1);
            grid.Children.Add(buttons);
            row.Child = grid;
            PendingUsersList.Children.Add(row);
        }
    }

    private void OnAssignRoleClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not (int userId, string role, string fullName))
            return;

        bool ok = _users.Approve(userId, role);
        StatusTextBlock.Text = ok
            ? $"✔ {fullName} назначена роль «{UserService.RoleDisplay(role)}». Пользователь может входить."
            : "Не удалось назначить роль (нет связи с сервером?).";

        RefreshPendingList();
    }

    // ======================= Импорт ведомостей =======================

    private async void OnParseFolderClicked(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Выберите папку с ведомостями"
        });

        if (folders.Count > 0)
        {
            string folderPath = folders[0].Path.LocalPath;

            ParseProgressBar.IsVisible = true;
            ParseProgressBar.Value = 0;
            StatusTextBlock.Text = "Сканирование файлов...";

            var progress = new Progress<ProgressReport>(report =>
            {
                if (report.Total > 0)
                {
                    ParseProgressBar.Value = (double)report.Current / report.Total * 100;
                }
                StatusTextBlock.Text = $"Обработка: {report.CurrentFileName} ({report.Current}/{report.Total})";
            });

            _dbService.InitializeDatabase();

            var treeData = await Task.Run(() =>
                _parserService.BuildStatementTree(folderPath, progress)
            );

            StatusTextBlock.Text = "Сохранение в базу данных...";

            // Собираем все детали дерева в плоский список и сохраняем ОДНОЙ транзакцией,
            // вместе с нормами трудоёмкости на плазменную резку по узлам (лист "Трудоёмкость"
            // ведомости — читается в ExcelParserService.GetPlasmaCuttingTime и лежит в
            // PlasmaCuttingTime у узловых StatementItem).
            var ordersToClear = new List<string>();
            var rows = new List<DetailRowDto>();
            var laborNorms = new List<UnitLaborNormDto>();
            CollectRows(treeData, "", "", "", ordersToClear, rows, laborNorms);

            bool savedOnline = await Task.Run(() => _dbService.ReplaceStatement(ordersToClear, rows, laborNorms));

            ParseProgressBar.IsVisible = false;
            StatusTextBlock.Text = savedOnline
                ? "Успешно! Ведомости пропарсены и обновлены в базе."
                : "Нет связи с сервером. Данные сохранены локально и будут автоматически отправлены при появлении сети.";
        }
    }

    /// <summary>
    /// Рекурсивно обходит дерево ведомостей: собирает список заказов для очистки,
    /// плоский список деталей с их иерархией для пакетной вставки, и норму трудоёмкости
    /// на плазменную резку для каждого узла (если она была прочитана из ведомости).
    /// </summary>
    private void CollectRows(
        IEnumerable<StatementItem> items,
        string currentOrder, string currentProduct, string currentUnit,
        List<string> ordersToClear, List<DetailRowDto> rows, List<UnitLaborNormDto> laborNorms)
    {
        foreach (var item in items)
        {
            string order = currentOrder;
            string product = currentProduct;
            string unit = currentUnit;

            if (item.Icon == "📁" || item.Title.StartsWith("Заказ: "))
            {
                order = item.Title.Replace("Заказ: ", "");
                if (!ordersToClear.Contains(order))
                    ordersToClear.Add(order);
            }
            else if (item.Icon == "📦" || item.Title.StartsWith("Продукция: "))
            {
                product = item.Title.Replace("Продукция: ", "");
            }
            else if (item.Icon == "🧩" || item.Title.StartsWith("Узел: "))
            {
                unit = item.Title.Replace("Узел: ", "");

                if (item.PlasmaCuttingTime.HasValue)
                {
                    laborNorms.Add(new UnitLaborNormDto
                    {
                        OrderName = order,
                        ProductName = product,
                        UnitName = unit,
                        PlasmaCuttingSeconds = item.PlasmaCuttingTime.Value.TotalSeconds
                    });
                }
            }

            if (item.DetailData != null)
            {
                rows.Add(new DetailRowDto
                {
                    Detail = item.DetailData,
                    OrderName = order,
                    ProductName = product,
                    UnitName = unit
                });
            }

            if (item.Children.Count > 0)
            {
                CollectRows(item.Children, order, product, unit, ordersToClear, rows, laborNorms);
            }
        }
    }
}
