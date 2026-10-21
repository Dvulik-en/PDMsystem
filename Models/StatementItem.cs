using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BKMsystem.Models;

public class StatementItem : INotifyPropertyChanged
{
    public string Title { get; set; } = "";
    
    private string _icon = "";
    public string Icon 
    { 
        get
        {
            if (!IsGroup) 
                return "📄"; // Обычная деталь

            return GetMaterialIcon();
        }
        set => _icon = value;
    }

    public bool IsGroup { get; set; }

    public string DrawingNumber { get; set; } = "";
    public string Thickness { get; set; } = "";
    public string SteelGrade { get; set; } = "";
    public int Quantity { get; set; }
    
    public string DetailType { get; set; } = "";
    public string Profile { get; set; } = "";
    public double Length { get; set; }

    public Detail? DetailData { get; set; }
    public string? ProductGroupName { get; set; }
    
    public string? ExcelFilePath { get; set; }
    public TimeSpan? PlasmaCuttingTime { get; set; }

    /// <summary>Читаемое "3ч 30м" для бейджа в дереве. Пусто, если время не прочитано
    /// из ведомости (нет листа "Трудоёмкость" либо узел ещё не импортирован).</summary>
    public string PlasmaCuttingTimeText
    {
        get
        {
            if (!PlasmaCuttingTime.HasValue) return "";
            var t = PlasmaCuttingTime.Value;
            int totalMinutes = (int)Math.Round(t.TotalMinutes);
            return $"{totalMinutes / 60}ч {totalMinutes % 60}м";
        }
    }

    public ObservableCollection<StatementItem> Children { get; set; } = new();

    // --- ПОЛЯ ДЛЯ ТЕХНОЛОГА ---

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
    }

    private bool _isEnabled = true;
    public bool IsEnabled
    {
        get => _isEnabled;
        set { if (_isEnabled != value) { _isEnabled = value; OnPropertyChanged(); } }
    }

    private bool _isExpanded = false;
    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded != value) { _isExpanded = value; OnPropertyChanged(); } }
    }

    // --- ОПРЕДЕЛЕНИЕ ИКОНОК ПО МАТЕРИАЛУ И ОЧИСТКА НАЗВАНИЯ ---

    private string GetMaterialIcon()
    {
        // 1. Иконки высшего уровня (Планы 📅, Заказы 📦, Изделия ⚙️, Узлы 🧩)
        if (!string.IsNullOrEmpty(_icon)) 
            return _icon;

        // 2. Ищем строго по наименованию самого материала (Title)
        string title = Title.ToLower().Trim();

        if (title.Contains("лист") || title.Contains("плита")) 
            return "📄";

        if (title.Contains("труба")) 
            return "⭕";

        if (title.Contains("швеллер")) 
            return "匚";

        if (title.Contains("уголок")) 
            return "∟";

        if (title.Contains("балка") || title.Contains("двутавр")) 
            return "工";

        // 3. Запасной вариант для листовых групп, если в Title заполнена толщина
        if (!string.IsNullOrEmpty(Thickness)) 
            return "📄";

        return "📁";
    }

    /// <summary>
    /// Безопасная очистка текста от вшитых иконок и дублирующих префиксов
    /// </summary>
    public string DisplayTitle
    {
        get
        {
            if (!IsGroup) return Title;

            string clean = Title.Trim();

            // Удаляем возможные текстовые значки из строки
            clean = clean.Replace("🗓️", "")
                         .Replace("🗓", "")
                         .Replace("📅", "")
                         .Replace("📦", "")
                         .Replace("📄", "")
                         .Replace("📄", "")
                         .Replace("匚", "")
                         .Replace("⭕", "")
                         .Replace("∟", "")
                         .Trim();

            // Удаляем префикс "Лист:"
            if (clean.StartsWith("Лист:", StringComparison.OrdinalIgnoreCase))
            {
                clean = clean.Substring(5).Trim();
            }

            // Убираем дублирование толщины в начале ("1,5мм, Лист..." -> "Лист...")
            if (!string.IsNullOrEmpty(Thickness))
            {
                string thickPrefix = Thickness + "мм,";
                if (clean.StartsWith(thickPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    clean = clean.Substring(thickPrefix.Length).Trim();
                }
            }

            return clean;
        }
    }

    /// <summary>
    /// Приоритет сортировки: 1 - Листы, 2 - Трубы, 3 - Швеллеры, 4 - Уголки, 5 - Балки, 99 - Прочее
    /// </summary>
    public int SortOrder
    {
        get
        {
            if (!IsGroup) return 99;

            string text = $"{Title} {Profile}".ToLower().Trim();

            if (text.Contains("труба")) return 2;
            if (text.Contains("швеллер")) return 3;
            if (text.Contains("уголок")) return 4;
            if (text.Contains("балка") || text.Contains("двутавр")) return 5;
            if (text.Contains("лист") || text.Contains("плита") || !string.IsNullOrEmpty(Thickness)) return 1;

            return 99;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}