using System.Collections.ObjectModel;

namespace BKMsystem.Models;

// Это описатель одного узла в нашем дереве Плана (Дата, Заказ, Изделие или Узел)
public class PlanTreeNode
{
    // Заголовок строки (например, "📅 25.08.2026" или "📦 Заказ: №102")
    public string Title { get; set; } = "";
    
    // Тип ветки (Date, Order, Product, Unit) — пригодится для иконок или фильтрации
    public string NodeType { get; set; } = "";

    // Id задания в PlannedTasks — заполнен только у узлов NodeType == "Unit".
    // Нужен, чтобы по правому клику на узле дерева можно было растянуть/сжать его
    // разбивку по дням (PlanViewModel.SplitTaskAcrossDays/RemoveDayFromSplit).
    public int TaskId { get; set; }

    // Дочерние элементы (у Даты дочерние — Заказы, у Заказа — Изделия и т.д.)
    public ObservableCollection<PlanTreeNode> Children { get; set; } = new();
}