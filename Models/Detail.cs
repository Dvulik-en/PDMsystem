using System.Globalization;
using System.Text.RegularExpressions;

namespace BKMsystem.Models;

public class Detail
{
    // Уникальный номер в базе
    public int Id { get; set; }
    
    // КД продукции (Номер чертежа детали)
    public string DrawingNumber { get; set; } = "";
    
    // Наименование детали
    public string Name { get; set; } = "";

    // Материал / Марка стали
    public string SteelGrade { get; set; } = "";
    
    // Размеры заготовки
    public string Dimensions { get; set; } = "";
    
    // Количество в узле
    public int Quantity { get; set; }

    // Тип детали: "Лист", "Плазма", "Труба", "Швеллер" и т.д.
    public string DetailType { get; set; } = "Лист";

    // Профиль / Сечение (например, "100х50х4" или "Труба 40х40")
    public string Profile { get; set; } = "";

    // Длина заготовки (для сортового проката и труб)
    public double Length { get; set; }

    // Вычисляемая толщина
    public double Thickness
    {
        get
        {
            var textToSearch = $"{SteelGrade} {Dimensions}";
            var match = Regex.Match(textToSearch, @"(\d+([.,]\d+)?)");
            if (match.Success && double.TryParse(match.Value.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out double thickness))
            {
                return thickness;
            }
            return 0;
        }
    }
}