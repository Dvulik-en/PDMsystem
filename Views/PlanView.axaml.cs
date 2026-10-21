using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using BKMsystem.Models;
using BKMsystem.Services;
using BKMsystem.ViewModels;

namespace BKMsystem.Views;

public partial class PlanView : UserControl
{
    public PlanView()
    {
        InitializeComponent();
        Loaded += (s, e) =>
        {
            if (DataContext is PlanViewModel vm)
            {
                vm.RefreshAll();
            }
        };

        // Автообновление дерева планирования — включая изменения разбивки по дням
        // (PlanTaskSegments) и норм трудоёмкости (UnitLaborNorms, влияет на шкалу).
        //
        // ВАЖНО: это событие прилетает от фонового потока, который слушает Postgres
        // LISTEN/NOTIFY (DatabaseService), а НЕ от потока интерфейса. Обновлять
        // ObservableCollection напрямую из чужого потока нельзя — Avalonia либо тихо
        // потеряет часть изменений, либо начнёт "терять" элементы из вида (именно это,
        // судя по всему, и происходило: собственное же сохранение вызывает уведомление,
        // и второй RefreshAll(), прилетевший из фонового потока почти одновременно с первым
        // UI-потоковым, гонялся с ним за одни и те же коллекции). Заворачиваем в
        // Dispatcher.UIThread.Post, чтобы обновление интерфейса ВСЕГДА шло из потока UI.
        DatabaseService.OnDatabaseUpdated += (tableName) =>
        {
            if (tableName.Equals("plannedtasks", StringComparison.OrdinalIgnoreCase) ||
                tableName.Equals("details", StringComparison.OrdinalIgnoreCase) ||
                tableName.Equals("plantasksegments", StringComparison.OrdinalIgnoreCase) ||
                tableName.Equals("unitlabornorms", StringComparison.OrdinalIgnoreCase) ||
                tableName.Equals("plandaysettings", StringComparison.OrdinalIgnoreCase))
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (DataContext is PlanViewModel vm)
                    {
                        // RefreshAll(), а не только дерево — иначе календарь и правая панель
                        // плиток не увидят изменения, прилетевшие от другого окна/пользователя.
                        vm.RefreshAll();
                    }
                });
            }
        };
    }

    private void OnAddClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PlanViewModel vm)
        {
            vm.AddToPlan();
        }
    }

    /// <summary>ПКМ на узле дерева → "Резать также на следующий день". DataContext пункта
    /// меню наследуется от TextBlock строки — это тот же PlanTreeNode, что стоит в дереве.</summary>
    private void OnAddDayClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: PlanTreeNode node } && DataContext is PlanViewModel vm)
        {
            vm.AddNextDayToSplit(node.TaskId);
        }
    }

    private void OnRemoveDayClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: PlanTreeNode node } && DataContext is PlanViewModel vm)
        {
            vm.RemoveLastDayFromSplit(node.TaskId);
        }
    }

    // ------------------------------------------------------------------
    // Правая панель "плиток" (Заказ/Изделие/Узел) — начало перетаскивания.
    // Реальная постановка в план происходит в DayColumnView.OnPoolDrop, когда
    // плитку бросают на конкретный день (данные передаются через DataObject).
    // ------------------------------------------------------------------
    private async void OnPoolTilePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control control && control.Tag != null)
        {
            var data = new DataObject();
            data.Set(DayColumnView.PoolDragFormat, control.Tag);
            await DragDrop.DoDragDrop(e, data, DragDropEffects.Copy);
        }
    }

    // ------------------------------------------------------------------
    // Листание видимого окна дат календаря — не привязано к "сегодня", чтобы
    // можно было вернуться к прошедшим дням (напр. незакрытая вовремя задача ПДО).
    // ------------------------------------------------------------------
    private void OnTimelineBackClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PlanViewModel vm) vm.TimelineShiftBack();
    }

    private void OnTimelineForwardClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PlanViewModel vm) vm.TimelineShiftForward();
    }

    private void OnTimelineTodayClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PlanViewModel vm) vm.TimelineJumpToToday();
    }

    // ------------------------------------------------------------------
    // Срочная дорезка: выбор найденной детали из списка результатов поиска.
    // ------------------------------------------------------------------
    private void OnUrgentPartPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { Tag: DetailSearchResult part } && DataContext is PlanViewModel vm)
        {
            vm.SelectedUrgentPart = part;
        }
    }

    private void OnUrgentPartClearClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PlanViewModel vm) vm.SelectedUrgentPart = null;
    }

    private void OnAddUrgentRecutClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PlanViewModel vm) vm.AddUrgentRecut();
    }
}
