using System;
using BKMsystem.ViewModels;

namespace BKMsystem.Models;

/// <summary>Один квадратик месячной сетки в "Метриках" (директор) — зелёный/красный день,
/// при наведении показывает статус и причину недореза от мастера (если она есть).
/// Аналог GitHub-контрибьюшн-графика, как просил пользователь.</summary>
public class MetricsDaySquareModel : ViewModelBase
{
    public DateTime Date { get; set; }
    public string DayNumber => Date.Day.ToString();
    public bool IsCurrentMonth { get; set; } = true;

    private DayCutStatus _cutStatus = DayCutStatus.NotApplicable;
    public DayCutStatus CutStatus
    {
        get => _cutStatus;
        set
        {
            _cutStatus = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SquareColor));
            OnPropertyChanged(nameof(TooltipText));
        }
    }

    private string? _delayReasonsJoined;
    /// <summary>Причины недореза от мастера за этот день (может быть несколько заказов —
    /// склеены через "; "), показываются во всплывающей подсказке квадратика.</summary>
    public string? DelayReasonsJoined
    {
        get => _delayReasonsJoined;
        set { _delayReasonsJoined = value; OnPropertyChanged(); OnPropertyChanged(nameof(TooltipText)); }
    }

    private string? _unfinishedPartsJoined;
    /// <summary>Конкретные невырезанные узлы/детали за этот день (по заказам) — директору важно
    /// видеть не просто "не успели", а ЧТО именно, чтобы учитывать на будущее при планировании.
    /// Формат: "Заказ №12: Стенка, Крышка; Заказ №7: Основание".</summary>
    public string? UnfinishedPartsJoined
    {
        get => _unfinishedPartsJoined;
        set { _unfinishedPartsJoined = value; OnPropertyChanged(); OnPropertyChanged(nameof(TooltipText)); }
    }

    /// <summary>Не текущий месяц (клетки-заполнители в начале/конце сетки) — рисуем блёкло,
    /// без статуса, чтобы не путать с реальным "пусто" за этот месяц.</summary>
    public string SquareColor
    {
        get
        {
            if (!IsCurrentMonth) return "#1A1A1A";
            return CutStatus switch
            {
                DayCutStatus.Done => "#2E7D32",
                DayCutStatus.NotDoneNoReason => "#C62828",
                DayCutStatus.NotDoneWithReason => "#C49000",
                DayCutStatus.NoTasks => "#2A2A2A",
                _ => "#1E1E1E" // будущий день
            };
        }
    }

    public string TooltipText
    {
        get
        {
            if (!IsCurrentMonth) return "";
            string dateStr = Date.ToString("dd.MM.yyyy");
            string statusText = CutStatus switch
            {
                DayCutStatus.Done => "Выполнено — всё вырезано",
                DayCutStatus.NotDoneNoReason => "Не выполнено — причина не указана",
                DayCutStatus.NotDoneWithReason => "Не выполнено",
                DayCutStatus.NoTasks => "На этот день ничего не запланировано",
                _ => "День ещё не наступил"
            };
            string result = $"{dateStr}\n{statusText}";
            if (!string.IsNullOrWhiteSpace(UnfinishedPartsJoined))
                result += $"\nНе вырезано: {UnfinishedPartsJoined}";
            if (!string.IsNullOrWhiteSpace(DelayReasonsJoined))
                result += $"\nПричина: {DelayReasonsJoined}";
            return result;
        }
    }
}

/// <summary>Сводная плашка-цифра в верхней части "Метрик" (аналог карточек статистики на
/// референс-изображении пользователя) — заголовок + большое число + подпись.</summary>
public class MetricsStatCardModel
{
    public string Title { get; set; } = "";
    public string Value { get; set; } = "";
    public string Subtitle { get; set; } = "";
}

/// <summary>Кольцевой индикатор процента (как "Показатели" на присланном пользователем
/// референсе — заряд/состояние/давление и т.д. в виде дуг). Дуга рисуется вручную через
/// Path-геометрию (Avalonia не имеет готового кольцевого прогресса), считается один раз при
/// создании модели — процент за месяц не меняется на лету, как в живом приборе.</summary>
public class RingGaugeModel
{
    public string Label { get; set; } = "";
    public double Percent { get; set; } // 0..100

    /// <summary>Есть ли вообще что считать (были ли за период дни с заданиями / карты с
    /// нужными метками времени). Раньше "нечего сравнивать" тихо превращалось в 100% — то есть
    /// пустой месяц выглядел как идеальный, что неверно: показываем прочерк вместо цифры.</summary>
    public bool HasData { get; set; } = true;

    public string PercentText => HasData ? $"{Math.Round(Percent)}%" : "—";

    /// <summary>Цвет дуги — по статусной шкале (зелёный/жёлтый/красный), а не бренд-акцент:
    /// это индикатор состояния "успеваем/не успеваем", а не декоративная цифра. Без данных —
    /// нейтральный серый, чтобы не читалось как "плохо" или "хорошо".</summary>
    public string RingColor
    {
        get
        {
            if (!HasData) return "#555555";
            return Percent switch
            {
                >= 90 => "#2E7D32",
                >= 70 => "#C49000",
                _ => "#C62828"
            };
        }
    }

    /// <summary>Path-геометрия дуги от 12 часов по часовой стрелке на Percent% окружности,
    /// вписанной в квадрат 100x100 с центром (50,50) и радиусом 42 (совпадает с фоновым
    /// Ellipse StrokeThickness=10 в разметке — см. RingGaugeControl в MetricsView.axaml).
    /// Без данных дугу вообще не рисуем — остаётся только фоновый серый круг + "—".</summary>
    public string ArcPathData
    {
        get
        {
            if (!HasData) return "";

            const double cx = 50, cy = 50, r = 42;
            double pct = Math.Clamp(Percent, 0, 100);
            double sweepDeg = pct >= 99.95 ? 359.9 : pct / 100.0 * 360.0; // 360° вырождает Arc-сегмент
            const double startDeg = -90; // 12 часов
            double endDeg = startDeg + sweepDeg;

            var start = PointOnCircle(cx, cy, r, startDeg);
            var end = PointOnCircle(cx, cy, r, endDeg);
            int isLargeArc = sweepDeg > 180 ? 1 : 0;

            return $"M {start.x:0.##},{start.y:0.##} A {r},{r} 0 {isLargeArc} 1 {end.x:0.##},{end.y:0.##}";
        }
    }

    private static (double x, double y) PointOnCircle(double cx, double cy, double r, double angleDeg)
    {
        double rad = angleDeg * Math.PI / 180.0;
        return (cx + r * Math.Cos(rad), cy + r * Math.Sin(rad));
    }
}

/// <summary>Строка "норма (ведомость) vs факт (реальное время резки)" по одному узлу заказа —
/// то, что технолог просил в "Метрики" вместо оперативного бейджика времени у оператора:
/// директору важно видеть, ГДЕ реально расходится норма и факт, чтобы решать, что менять.
/// Полоски рисуются как две наложенные Border на общей шкале шириной BarMaxWidth: серая —
/// норма (100%), цветная — факт; если факт длиннее нормы, видно, насколько именно.</summary>
public class MetricsUnitTimeRow
{
    public const double BarMaxWidth = 220;

    public string OrderName { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string UnitName { get; set; } = "";
    public string UnitLabel => string.IsNullOrWhiteSpace(ProductName)
        ? $"{OrderName} / {UnitName}"
        : $"{OrderName} / {ProductName} / {UnitName}";

    public double? NormHours { get; set; }
    public double FactHours { get; set; }
    public int SheetsCounted { get; set; }

    public bool HasNorm => NormHours.HasValue;
    public string NormText => HasNorm ? $"{NormHours:0.#} ч" : "нормы нет";
    public string FactText => $"{FactHours:0.#} ч ({SheetsCounted} УП)";

    /// <summary>Процент от нормы — используется и для текста, и для цвета/ширины полосы.</summary>
    public double? PercentOfNorm => HasNorm && NormHours!.Value > 0 ? 100.0 * FactHours / NormHours.Value : null;

    public string DeltaText
    {
        get
        {
            if (!HasNorm) return "нет ведомости для сравнения";
            double delta = FactHours - NormHours!.Value;
            double pct = PercentOfNorm ?? 0;
            string sign = delta > 0 ? "+" : "";
            return $"{sign}{delta:0.#} ч ({pct:0}% от нормы)";
        }
    }

    /// <summary>Тот же принцип цвета, что и в RingGaugeModel: зелёный — в пределах нормы,
    /// жёлтый — небольшой перерасход, красный — существенный, серый — сравнивать не с чем.</summary>
    public string BarColor
    {
        get
        {
            if (!HasNorm) return "#555555";
            double pct = PercentOfNorm ?? 0;
            return pct switch
            {
                <= 105 => "#2E7D32",
                <= 130 => "#C49000",
                _ => "#C62828"
            };
        }
    }

    /// <summary>Ширина полосы нормы — всегда полная шкала (100% = BarMaxWidth), опорная линия.</summary>
    public double NormBarWidth => HasNorm ? BarMaxWidth : 0;

    /// <summary>Ширина полосы факта относительно той же шкалы — может быть длиннее нормы
    /// (перерасход), тогда видно явное превышение; капается, чтобы не вылезать из карточки.</summary>
    public double FactBarWidth
    {
        get
        {
            if (!HasNorm) return 0;
            double pct = Math.Clamp(PercentOfNorm ?? 0, 0, 200);
            return pct / 100.0 * BarMaxWidth;
        }
    }
}
