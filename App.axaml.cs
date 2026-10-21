using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using BKMsystem.Models;
using BKMsystem.Views;

namespace BKMsystem;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // Ловим разворачивание/сворачивание узлов дерева ГЛОБАЛЬНО, один раз на всё
        // приложение — и сразу пишем в модель. Это замена не сработавшей Mode=TwoWay
        // привязке через Style Setter (она заносит значение из модели В TreeViewItem,
        // но обратно, при клике пользователя по стрелочке, в модель не возвращает).
        TreeViewItem.IsExpandedProperty.Changed.AddClassHandler<TreeViewItem>((item, e) =>
        {
            bool expanded = item.IsExpanded;
            switch (item.DataContext)
            {
                case NestingTreeNode n: n.IsExpanded = expanded; break;
                case PlanTaskGroupNode n: n.IsExpanded = expanded; break;
                case StatementItem n: n.IsExpanded = expanded; break;
            }
        });
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Приложение завершается, когда закрыты ВСЕ окна
            // (окно входа закрывается при успешном входе, MainWindow остаётся).
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnLastWindowClose;

            // Сначала — окно входа / регистрации.
            desktop.MainWindow = new LoginWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
