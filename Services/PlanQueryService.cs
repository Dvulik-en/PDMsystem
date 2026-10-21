using System;
using System.Collections.Generic;
using Npgsql;

namespace BKMsystem.Services;

/// <summary>
/// Запросы к плану (PlannedTasks), не входящие в основной DatabaseService.
/// Используется вкладкой ПДО, чтобы скрывать уже запланированные изделия/узлы.
/// </summary>
public static class PlanQueryService
{
    /// <summary>
    /// Ключи запланированных позиций в нижнем регистре:
    ///   "заказ|*|"                 — заказ запланирован целиком («Все изделия»)
    ///   "заказ|изделие|*"          — изделие запланировано целиком («Весь узел»)
    ///   "заказ|изделие|узел"       — запланирован конкретный узел
    /// При отсутствии связи возвращает пустой набор (показываем всё, ничего не прячем).
    /// </summary>
    public static HashSet<string> GetPlannedKeys()
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var conn = new NpgsqlConnection(DatabaseService.ConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT OrderName, ProductName, UnitName FROM PlannedTasks;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                string o = (r.IsDBNull(0) ? "" : r.GetString(0)).Trim();
                string p = (r.IsDBNull(1) ? "" : r.GetString(1)).Trim();
                string u = (r.IsDBNull(2) ? "" : r.GetString(2)).Trim();
                if (string.IsNullOrEmpty(o)) continue;

                if (string.IsNullOrEmpty(p) || p == "Все изделия")
                    keys.Add($"{o}|*|");
                else if (string.IsNullOrEmpty(u) || u == "Весь узел")
                    keys.Add($"{o}|{p}|*");
                else
                    keys.Add($"{o}|{p}|{u}");
            }
        }
        catch
        {
            // оффлайн или таблицы ещё нет — не фильтруем
        }
        return keys;
    }

    public static bool IsPlanned(HashSet<string> keys, string order, string? product = null, string? unit = null)
    {
        order = order.Trim();
        if (keys.Contains($"{order}|*|")) return true;
        if (product == null) return false;
        product = product.Trim();
        if (keys.Contains($"{order}|{product}|*")) return true;
        if (unit == null) return false;
        return keys.Contains($"{order}|{product}|{unit.Trim()}");
    }
}
