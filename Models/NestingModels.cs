using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using BKMsystem.ViewModels;

namespace BKMsystem.Models;

// ---------------------------------------------------------------------------
// Узел дерева раскроев (вкладки "Раскрои" и "База").
// Иерархия: Материал/Толщина -> Карта раскроя -> Файлы комплекта (PDF/УП/XLSX)
// ---------------------------------------------------------------------------
public class NestingTreeNode : ViewModelBase
{
    public int SheetId { get; set; }          // id карты в cutting_sheets (0 для групп и файлов)
    public int FileId { get; set; }           // id файла в cutting_files (0 для групп и карт)
    public string Title { get; set; } = "";
    public string NodeType { get; set; } = ""; // "Order" | "Product" | "MaterialFolder" | "Sheet" | "Pdf" | "Mpg" | "Excel" | "Other"
    public string Status { get; set; } = "";  // Новый / Выполнен
    public string SheetName { get; set; } = ""; // чистое имя карты (04S_SL01) — для канонического имени при выгрузке
    public string FileName { get; set; } = "";  // имя файла в базе (для узлов-файлов)
    public string FileType { get; set; } = "";  // CNC / PDF / EXCEL / DXF
    /// <summary>УП уже выгружалась на флешку — после этого доступна кнопка «Выполнено».</summary>
    public bool UsbExported { get; set; }
    /// <summary>Все SheetId под узлом «Лист N мм» — для выгрузки комплекта УП.</summary>
    public List<int> RelatedSheetIds { get; set; } = new();

    /// <summary>Момент, когда УП реально забрали на резку (выгрузка на флешку). Заполняется
    /// только для узла-файла .MPG, который уже выгружен, но ещё не отмечен «Выполнен» —
    /// нужно, чтобы NestingViewModel мог периодически пересчитывать TimeInWorkText
    /// (см. RecomputeTimeInWork) без повторного похода в базу.</summary>
    public DateTime? UsbExportedAt { get; set; }

    private string _timeInWorkText = "";
    /// <summary>Бейдж "⏱ в работе: 2ч 15м" для карточки УП — отдельное поле (не часть Title),
    /// чтобы можно было стилизовать отдельно от имени файла. Пустая строка — бейдж не нужен
    /// (УП ещё не выгружена, либо уже выполнена).</summary>
    public string TimeInWorkText
    {
        get => _timeInWorkText;
        private set { _timeInWorkText = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasTimeInWork)); }
    }

    /// <summary>Есть ли что показывать в бейдже — удобно для IsVisible в XAML.</summary>
    public bool HasTimeInWork => !string.IsNullOrEmpty(TimeInWorkText);

    /// <summary>Пересчитывает TimeInWorkText из UsbExportedAt на текущий момент. Вызывается
    /// один раз при построении дерева и затем периодически из NestingViewModel (таймер),
    /// чтобы бейдж "тикал" без перестроения всего дерева и лишних запросов к базе.</summary>
    public void RecomputeTimeInWork()
    {
        if (NodeType != "Mpg" || !UsbExported || !UsbExportedAt.HasValue
            || string.Equals(Status, "Выполнен", StringComparison.OrdinalIgnoreCase))
        {
            TimeInWorkText = "";
            return;
        }

        double elapsed = BKMsystem.Services.WorkTimeCalculator.WorkingHoursElapsed(UsbExportedAt.Value, DateTime.Now);
        TimeInWorkText = $"⏱ в работе: {BKMsystem.Services.WorkTimeCalculator.FormatElapsed(elapsed)}";
    }

    private bool _isExpanded = true;
    /// <summary>Развёрнут ли узел в дереве — привязывается в XAML, чтобы состояние
    /// не сбрасывалось при перестроении дерева (Refresh()).</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; OnPropertyChanged(); }
    }

    public string Icon => NodeType switch
    {
        "Order"    => "📦",
        "Product"  => "🚜",
        "Material" => "🔩",
        "MaterialFolder" => "📄",
        "Sheet"    => "🗺",
        "Pdf"      => "📄",
        "Mpg"      => "⚙",
        "Excel"    => "📊",
        _          => "📦"
    };

    public ObservableCollection<NestingTreeNode> Children { get; set; } = new();
}
public class SheetTargetUnit
{
    public int? TaskId { get; set; }
    public string OrderName { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string UnitName { get; set; } = "";
}

// ---------------------------------------------------------------------------
// Модель отдельной детали из раскроя (информация из Excel .xlsx)
// ---------------------------------------------------------------------------
public class NestingPartModel
{
    public int PartNumber { get; set; }
    public string PartName { get; set; } = "";
    public int Quantity { get; set; }
    public decimal WeightKg { get; set; }
    public string OrderName { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string UnitName { get; set; } = "";

    // Добавьте размеры:
    public double Length { get; set; }
    public double Width { get; set; }
}

// ---------------------------------------------------------------------------
// Комплект раскроя: параметры листа + файлы + детали
// ---------------------------------------------------------------------------
public class NestingUploadModel
{
    public string NestingName { get; set; } = "";   // уникальное имя карты (04S_SL01)
    public string Material { get; set; } = "";
    public decimal Thickness { get; set; }
    public string SheetSize { get; set; } = "";
    public string TotalTime { get; set; } = "";

    public int? TaskId { get; set; }                // задание планирования (PlannedTasks)
    public string OrderName { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string UnitName { get; set; } = "";
    public string PlannedDate { get; set; } = "";   // yyyy-MM-dd
    public string PackageId { get; set; } = "";

    public Dictionary<string, string> FilePaths { get; set; } = new(); // имя файла -> путь на диске
    public List<NestingPartModel> Parts { get; set; } = new();
    public List<SheetTargetUnit> TargetUnits { get; set; } = new();
}

// ---------------------------------------------------------------------------
// DTO для локальной очереди оффлайн-синхронизации (байты файлов сразу в Base64)
// ---------------------------------------------------------------------------
public class NestingSheetDto
{
    public string NestingName { get; set; } = "";
    public string Material { get; set; } = "";
    public decimal Thickness { get; set; }
    public string SheetSize { get; set; } = "";
    public string TotalTime { get; set; } = "";
    public int? TaskId { get; set; }
    public string OrderName { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string UnitName { get; set; } = "";
    public string PlannedDate { get; set; } = "";
    public string PackageId { get; set; } = "";
    public List<NestingFileDto> Files { get; set; } = new();
    public List<NestingPartModel> Parts { get; set; } = new();
    /// <summary>Все изделия/узлы, к которым реально относится эта карта — уже готовый список
    /// из ImportKitAsync. Используется НАПРЯМУЮ вместо угадывания по чертёжным номерам деталей,
    /// потому что при одинаковых чертежах у нескольких изделий (например, несколько зав.№ одной
    /// и той же сборки) угадывание по номеру ломается — деталь достаётся только одному изделию.</summary>
    public List<SheetTargetUnit> TargetUnits { get; set; } = new();
}

public class NestingFileDto
{
    public string FileName { get; set; } = "";
    public string FileType { get; set; } = "";  // CNC / PDF / DXF / EXCEL / OTHER
    public string Base64Data { get; set; } = "";
}

// ---------------------------------------------------------------------------
// Дерево вкладки "Учет УП" (технолог): Дата -> Заказ -> Изделие -> Листы (толщина)
// ---------------------------------------------------------------------------
public class PlanTaskGroupNode : ViewModelBase
{
    public string Title { get; set; } = "";
    public string NodeType { get; set; } = "";  // Date | Order | Product | Thickness
    public int TaskId { get; set; }
    public string OrderName { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string UnitName { get; set; } = "";
    public string PlannedDate { get; set; } = ""; // yyyy-MM-dd
    public double Thickness { get; set; }
    public string SteelGrade { get; set; } = "";
    /// <summary>Ключ резервации — чтобы снять лист с левого дерева (✕).</summary>
    public string ReservationKey { get; set; } = "";

    private bool _isExpanded = true;
    /// <summary>Развёрнут ли узел в дереве — привязывается в XAML, чтобы состояние
    /// не сбрасывалось при перестроении дерева.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; OnPropertyChanged(); }
    }

    public string Icon => NodeType switch
    {
        "Date"      => "📅",
        "Order"     => "📦",
        "Product"   => "🚜",
        "Thickness" => "🔩",
        _           => "📁"
    };

    public ObservableCollection<PlanTaskGroupNode> Children { get; set; } = new();
}
public class SheetReservation
{
    public string ReservationKey { get; set; } = "";
    public string OrderName { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string SheetTitle { get; set; } = "";
    public string Thickness { get; set; } = "";
    public string SteelGrade { get; set; } = "";
}