using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Exceptions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Debug = System.Diagnostics.Debug;

namespace BKMsystem.Services
{
    public class AjanUIAutomationService
    {
        public async Task AutomateFullAjanFlowAsync(Process ajanProcess, List<NestPartItem> parts, double thickness = 0, string steelGrade = "")
        {
            await Task.Run(() =>
            {
                try
                {
                    Debug.WriteLine("[FlaUI] Старт автоматизации AJANCAM...");
                    using var automation = new UIA3Automation();

                    // 1. Поиск главного окна AJANCAM
                    var app = Application.Attach(ajanProcess);
                    Window? mainWindow = WaitForElement(() =>
                    {
                        var windows = app.GetAllTopLevelWindows(automation);
                        return windows.FirstOrDefault(w =>
                            !string.IsNullOrEmpty(w.Title) &&
                            !w.Title.Contains("Подождите", StringComparison.OrdinalIgnoreCase));
                    }, 30);

                    if (mainWindow == null)
                    {
                        Debug.WriteLine("[FlaUI] Ошибка: Главное окно AJANCAM не найдено!");
                        return;
                    }

                    Debug.WriteLine($"[FlaUI] Рабочее окно найдено: {mainWindow.Title}");
                    mainWindow.Focus();
                    Task.Delay(1000).Wait();

                    // 2. Нажатие на кнопку CAM
                    AutomationElement? camElement = WaitForElement(() =>
                        mainWindow.FindFirstDescendant(cf => cf.ByName("CAM")), 15);

                    if (camElement != null)
                    {
                        Debug.WriteLine("[FlaUI] Нажатие на кнопку CAM...");
                        ClickElement(camElement);
                    }
                    Task.Delay(1500).Wait();

                    // 3. ШАГ 1: Нажимаем "Новый"
                    var startBtn = WaitForElement(() =>
                        mainWindow.FindFirstDescendant(cf => cf.ByName("Новый"))?.AsButton(), 10);

                    if (startBtn != null)
                    {
                        Debug.WriteLine($"[FlaUI] Нажатие '{startBtn.Name}'...");
                        ClickElement(startBtn);
                        Task.Delay(1500).Wait();
                    }

                    // 4. ШАГ 2: Окно "Настройки" открывается СРАЗУ после "Новый" — материал/ток/толщина листа
                    //    выбираются здесь, ДО того как появится окно добавления деталей.
                    var settingsWindow = WaitForElement(() =>
                        mainWindow.FindFirstDescendant(cf => cf.ByName("Настройки"))?.AsWindow(), 15);

                    if (settingsWindow != null)
                    {
                        string targetSteel = steelGrade.Contains("Ст3", StringComparison.OrdinalIgnoreCase) ? "Ст3" : "09Г2С";
                        string targetThickness = Math.Round(thickness).ToString();
                        string targetAmperage = GetAmperageForMaterial(thickness, targetSteel);

                        Debug.WriteLine($"[FlaUI] Установка параметров: Материал={targetSteel}, Сила тока={targetAmperage}, Толщина={targetThickness}");
                        SetMaterialParametersViaComboBox(settingsWindow, targetSteel, targetAmperage, targetThickness);
                    }
                    else
                    {
                        Debug.WriteLine("[FlaUI] Окно 'Настройки' не найдено — параметры не установлены! ДАЛЬШЕ ИДТИ НЕЛЬЗЯ, прерываю.");
                        return;
                    }

                    Task.Delay(1000).Wait();

                    // 5. ШАГ 3: Нажимаем "Подтвердить" в окне "Настройки" — это открывает
                    //    "Добавить детали в раскрой" (подтверждено вашим рабочим логом).
                    var settingsConfirmBtn = WaitForElement(() =>
                        settingsWindow.FindFirstDescendant(cf => cf.ByName("Подтвердить"))?.AsButton(), 10);

                    if (settingsConfirmBtn != null)
                    {
                        Debug.WriteLine("[FlaUI] Нажатие 'Подтвердить' в окне Настройки...");
                        ClickElement(settingsConfirmBtn);
                        Task.Delay(1500).Wait();
                    }
                    else
                    {
                        Debug.WriteLine("[FlaUI] Кнопка 'Подтвердить' в окне Настройки не найдена — прерываю, чтобы не сломать состояние программы.");
                        return;
                    }

                    // 6. ШАГ 4: Окно "Добавить детали в раскрой" — путь к папке + добавление деталей + количество.
                    //    Ищем строго ВНУТРИ mainWindow по заголовку — БЕЗ фолбэка на mainWindow целиком,
                    //    чтобы при ошибке поиска код падал явно, а не тыкал путь в случайное поле экрана.
                    var addDetailsWindow = WaitForElement(() =>
                        mainWindow.FindFirstDescendant(cf =>
                            cf.ByText("Добавить детали в раскрой")
                            .Or(cf.ByName("Добавить детали в раскрой")))?.AsWindow(), 15);

                    if (addDetailsWindow == null)
                    {
                        Debug.WriteLine("[FlaUI] Окно 'Добавить детали в раскрой' не найдено — прерываю автоматизацию.");
                        return;
                    }

                    Debug.WriteLine("[FlaUI] Переход к окну выбора деталей.");
                    addDetailsWindow.Focus();

                    string? folderPath = Path.GetDirectoryName(parts.FirstOrDefault()?.DxfPath);
                    if (!string.IsNullOrEmpty(folderPath) && Directory.Exists(folderPath))
                    {
                        var pathEditField = addDetailsWindow.FindFirstDescendant(cf =>
                            cf.ByControlType(ControlType.Edit))?.AsTextBox();

                        if (pathEditField != null)
                        {
                            Debug.WriteLine($"[FlaUI] Ввод пути к папке: {folderPath}");
                            pathEditField.Focus();

                            Rectangle editRect = pathEditField.BoundingRectangle;
                            Point editCenter = new Point(editRect.X + editRect.Width / 2, editRect.Y + editRect.Height / 2);
                            Mouse.Click(editCenter);
                            Task.Delay(300).Wait();

                            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
                            Task.Delay(100).Wait();
                            Keyboard.Type(VirtualKeyShort.BACK);
                            Task.Delay(150).Wait();

                            SetClipboardTextSTA(folderPath);
                            Task.Delay(150).Wait();
                            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_V);
                            Task.Delay(300).Wait();

                            Keyboard.Type(VirtualKeyShort.RETURN);
                            Task.Delay(2500).Wait();
                        }
                    }

                    // Перебор всех деталей: поиск через ItemContainerPattern -> добавление -> количество
                    foreach (var part in parts)
                    {
                        string fileName = Path.GetFileName(part.DxfPath);
                        Debug.WriteLine($"[FlaUI] Поиск и добавление детали: {fileName} (Кол-во: {part.Quantity})");

                        SelectAndAddPart(addDetailsWindow, automation, fileName, part.PartId, part.Quantity);
                    }

                    Task.Delay(500).Wait();

                    // 7. ШАГ 5: ЗДЕСЬ НУЖНО ПОДТВЕРЖДЕНИЕ ОТ ВАС.
                    //    Я не знаю точно, как называется кнопка, которая завершает добавление деталей
                    //    и запускает раскладку/переходит дальше — "Далее >>", "Подтвердить" ещё раз,
                    //    или что-то третье. Ищу оба варианта, но ОБЯЗАТЕЛЬНО проверьте по логу,
                    //    что сработал правильный, прежде чем доверять этому шагу боевые задания.
                    var finalBtn = WaitForElement(() =>
                        addDetailsWindow.FindFirstDescendant(cf =>
                            cf.ByName("Далее >>")
                            .Or(cf.ByName("Подтвердить")))?.AsButton(), 10);

                    if (finalBtn != null)
                    {
                        Debug.WriteLine($"[FlaUI] Нажатие '{finalBtn.Name}' для завершения добавления деталей...");
                        ClickElement(finalBtn);
                        Task.Delay(1000).Wait();
                    }
                    else
                    {
                        Debug.WriteLine("[FlaUI] Финальная кнопка не найдена — ОСТАНОВИЛСЯ. Проверьте вручную, как называется кнопка завершения на этом экране.");
                        return;
                    }

                    // 8. ШАГ 6: Обработка всплывающего окна (нажимаем "No" / "Нет"), если оно появилось
                    var noModalBtn = WaitForElement(() =>
                        mainWindow.FindFirstDescendant(cf =>
                            cf.ByName("No")
                            .Or(cf.ByName("Нет")))?.AsButton(), 5);

                    if (noModalBtn != null && !noModalBtn.IsOffscreen)
                    {
                        Debug.WriteLine("[FlaUI] Нажатие 'No' во всплывающем окне...");
                        ClickElement(noModalBtn);
                        Task.Delay(1500).Wait();
                    }

                    Debug.WriteLine("[FlaUI] Автоматизация успешно завершена.");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[FlaUI] Ошибка выполнения автоматизации: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Установка Материала / Силы тока / Толщины через ExpandCollapse+Selection паттерны ComboBox.
        /// Эти списки НЕ поддерживают ввод текста (нет ValuePattern) — только выбор пункта из списка.
        /// Порядок в окне "Настройки -> Размер листа": [0]=Тип резки, [1]=Материал, [2]=Сила тока, [3]=Толщина листа.
        /// </summary>
        private void SetMaterialParametersViaComboBox(Window settingsWindow, string material, string amperage, string thickness)
        {
            try
            {
                var combos = settingsWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.ComboBox))
                                            .Select(e => e.AsComboBox())
                                            .ToList();

                if (combos.Count < 4)
                {
                    Debug.WriteLine($"[FlaUI] Ожидалось 4 комбобокса (Тип резки/Материал/Ток/Толщина), найдено {combos.Count}. Прерываю установку параметров.");
                    return;
                }

                SelectComboAndVerify(combos[1], material, "Материал");
                SelectComboAndVerify(combos[2], amperage, "Сила тока");
                SelectComboAndVerify(combos[3], thickness, "Толщина листа");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FlaUI] Ошибка установки параметров: {ex.Message}");
            }
        }

        private void SelectComboAndVerify(FlaUI.Core.AutomationElements.ComboBox combo, string targetValue, string label)
        {
            try
            {
                combo.Expand();
                Task.Delay(200).Wait();

                var item = combo.Items.FirstOrDefault(i =>
                    NormalizeForCompare(i.Text) == NormalizeForCompare(targetValue));

                if (item == null)
                {
                    Debug.WriteLine($"[FlaUI] В списке '{label}' не найден пункт '{targetValue}'. Доступные варианты: " +
                                     string.Join(", ", combo.Items.Select(i => i.Text)));
                    combo.Collapse();
                    return;
                }

                item.Select();
                Task.Delay(200).Wait();

                string actual = combo.SelectedItem?.Text ?? combo.Name ?? string.Empty;
                Debug.WriteLine(NormalizeForCompare(actual) == NormalizeForCompare(targetValue)
                    ? $"[FlaUI] '{label}' подтверждено: {actual}"
                    : $"[FlaUI] ВНИМАНИЕ: '{label}' — ожидалось '{targetValue}', реально выбрано '{actual}'!");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FlaUI] Ошибка выбора '{label}': {ex.Message}");
            }
        }

        private string NormalizeForCompare(string value) =>
            (value ?? string.Empty).Replace(" ", "").Trim().ToUpperInvariant();

        /// <summary>
        /// Находит деталь в виртуализированном списке через ItemContainerPattern (принудительно
        /// материализует элемент независимо от прокрутки), кликает по ней, жмёт "Добавить деталь"
        /// и выставляет количество. Все клики выполняются через ClickElementSafe с повторным поиском
        /// на случай, если ссылка на элемент протухла из-за переиспользования контейнера WPF.
        /// </summary>
        private void SelectAndAddPart(Window dialogWindow, UIA3Automation automation, string fileName, string partId, int quantity)
        {
            string cleanFileName = Path.GetFileNameWithoutExtension(fileName);

            var listElement = dialogWindow.FindFirstDescendant(cf => cf.ByControlType(ControlType.List));
            if (listElement == null)
            {
                Debug.WriteLine("[FlaUI] Список деталей не найден.");
                return;
            }

            // Защита: если с предыдущей детали осталось незакрытое всплывающее окно — закрываем его сейчас,
            // прежде чем пытаться работать с (заблокированным им) списком.
            if (DismissBlockingPopupIfAny(automation, dialogWindow, $"перед обработкой '{cleanFileName}'", waitMs: 100))
            {
                Debug.WriteLine($"[FlaUI] Перед '{cleanFileName}' было закрыто зависшее окно предупреждения с прошлой детали.");
            }

            AutomationElement? FindItem() => FindListItemRobust(dialogWindow, listElement, automation, cleanFileName, fileName, partId);

            var fileItem = FindItem();
            if (fileItem == null)
            {
                Debug.WriteLine($"[FlaUI] Предупреждение: Файл {cleanFileName} не найден в списке (ни через ItemContainerPattern, ни перебором).");
                return;
            }

            if (!ClickElementSafe(fileItem, FindItem))
            {
                Debug.WriteLine($"[FlaUI] Не удалось выбрать деталь {cleanFileName} в списке.");
                return;
            }
            Task.Delay(200).Wait();

            AutomationElement? FindAddBtn() => dialogWindow.FindFirstDescendant(cf =>
                cf.ByName("Добавить деталь").Or(cf.ByName("Добавить")));

            var addBtn = FindAddBtn();
            if (addBtn != null)
            {
                if (ClickElementSafe(addBtn, FindAddBtn))
                {
                    Debug.WriteLine($"[FlaUI] Добавлена деталь: {cleanFileName}");
                }
                Task.Delay(400).Wait();

                // Ключевая проверка: если после добавления всплыло окно (например "деталь уже добавлена"),
                // закрываем его сразу — иначе оно заблокирует ВСЁ окно на все последующие детали.
                if (DismissBlockingPopupIfAny(automation, dialogWindow, $"добавление '{cleanFileName}'"))
                {
                    Debug.WriteLine($"[FlaUI] ВНИМАНИЕ: похоже, деталь '{cleanFileName}' попыталась добавиться повторно — всплывшее окно закрыто автоматически.");
                }
            }
            else
            {
                Keyboard.Type(VirtualKeyShort.RETURN);
                Task.Delay(400).Wait();
            }

            SetQuantityForPart(dialogWindow, automation, cleanFileName, quantity);
        }

        /// <summary>
        /// Ищет всплывающее модальное окно (кроме известных нам основных: "Добавить детали в раскрой",
        /// "Настройки", главное окно AJANCAM) с одной кнопкой OK/ОК и закрывает его. Такие окна блокируют
        /// (IsEnabled = false) весь родительский экран, пока их не закрыть — именно это выглядит в логе
        /// как массовое "Элемент отключён" на всех деталях подряд после того места, где всплыло окно.
        /// </summary>
        private bool DismissBlockingPopupIfAny(UIA3Automation automation, Window knownWindow, string context, int waitMs = 700)
        {
            Task.Delay(waitMs).Wait();
            try
            {
                var desktop = automation.GetDesktop();
                var topLevelWindows = desktop.FindAllChildren(cf => cf.ByControlType(ControlType.Window));

                foreach (var candidate in topLevelWindows)
                {
                    string? name = candidate.Name;
                    if (string.IsNullOrEmpty(name)) continue;

                    bool isKnownWindow =
                        name.Contains("AJANCAM", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Добавить детали в раскрой", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Настройки", StringComparison.OrdinalIgnoreCase);

                    if (isKnownWindow) continue;

                    var okBtn = candidate.FindFirstDescendant(cf =>
                        cf.ByName("OK").Or(cf.ByName("ОК")).Or(cf.ByName("Ok")))?.AsButton();

                    if (okBtn != null)
                    {
                        Debug.WriteLine($"[FlaUI] Обнаружено всплывающее окно '{name}' (контекст: {context}). Текст окна может подсказать причину — закрываю кнопкой OK.");
                        ClickElement(okBtn);
                        Task.Delay(400).Wait();
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FlaUI] Ошибка при проверке всплывающих окон ({context}): {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// Поиск элемента списка по имени через ItemContainerPattern — работает даже если элемент
        /// сейчас не виден на экране (WPF-виртуализация). Если контейнер не поддерживает этот паттерн,
        /// используется запасной вариант — перебор стрелкой "Вниз".
        /// </summary>
        private AutomationElement? FindListItemRobust(Window dialogWindow, AutomationElement listElement, UIA3Automation automation,
            string cleanFileName, string fileName, string partId)
        {
            if (listElement.Patterns.ItemContainer.IsSupported)
            {
                var itemContainerPattern = listElement.Patterns.ItemContainer.Pattern;

                foreach (var candidate in new[] { cleanFileName, fileName, partId })
                {
                    if (string.IsNullOrEmpty(candidate)) continue;
                    try
                    {
                        var found = itemContainerPattern.FindItemByProperty(null, automation.PropertyLibrary.Element.Name, candidate);
                        if (found != null)
                        {
                            if (found.Patterns.ScrollItem.IsSupported)
                            {
                                try { found.Patterns.ScrollItem.Pattern.ScrollIntoView(); } catch { /* не критично */ }
                                Task.Delay(150).Wait();
                            }
                            return found;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[FlaUI] ItemContainerPattern: ошибка поиска '{candidate}': {ex.Message}");
                    }
                }

                Debug.WriteLine($"[FlaUI] ItemContainerPattern не нашёл '{cleanFileName}' — пробую перебор стрелками как запасной вариант.");
            }
            else
            {
                Debug.WriteLine("[FlaUI] Список не поддерживает ItemContainerPattern — перебор стрелками.");
            }

            return FindListItemByArrowScan(dialogWindow, listElement, cleanFileName, fileName, partId);
        }

        /// <summary>
        /// Запасной способ поиска — перебор стрелкой "Вниз" с "Home" в начале.
        /// Используется только если ItemContainerPattern недоступен или не сработал.
        /// </summary>
        private AutomationElement? FindListItemByArrowScan(Window dialogWindow, AutomationElement listElement,
            string cleanFileName, string fileName, string partId)
        {
            Rectangle rect = listElement.BoundingRectangle;
            Mouse.Click(new Point(rect.X + 40, rect.Y + 40));
            Task.Delay(150).Wait();
            Keyboard.Type(VirtualKeyShort.HOME);
            Task.Delay(150).Wait();

            for (int i = 0; i < 80; i++)
            {
                var fileItem = dialogWindow.FindFirstDescendant(cf =>
                    cf.ByName(cleanFileName).Or(cf.ByName(fileName)).Or(cf.ByName(partId)));

                if (fileItem != null && !fileItem.IsOffscreen)
                {
                    // Защита от гонки при виртуализации: WPF мог ещё не успеть обновить Name
                    // переиспользованного контейнера. Ждём чуть-чуть и перепроверяем имя ещё раз —
                    // если оно "поехало", это не наша деталь, продолжаем перебор дальше.
                    Task.Delay(120).Wait();
                    string? nameAfterSettle = null;
                    try { nameAfterSettle = fileItem.Name; } catch { /* элемент мог протухнуть — тоже сигнал не доверять */ }

                    bool stillMatches = nameAfterSettle != null &&
                        (nameAfterSettle == cleanFileName || nameAfterSettle == fileName || nameAfterSettle == partId);

                    if (stillMatches)
                    {
                        return fileItem;
                    }

                    Debug.WriteLine($"[FlaUI] Имя элемента изменилось после паузы ('{nameAfterSettle}' вместо '{cleanFileName}') — похоже на гонку виртуализации, продолжаю перебор.");
                }

                Keyboard.Type(VirtualKeyShort.DOWN);
                Task.Delay(100).Wait();
            }

            return null;
        }

        /// <summary>
        /// Находит строку в таблице добавленных деталей (тоже виртуализированной при большом числе
        /// строк) через ItemContainerPattern и выставляет количество через F2 + ввод текста.
        /// </summary>
        private void SetQuantityForPart(Window dialogWindow, UIA3Automation automation, string cleanFileName, int quantity)
        {
            try
            {
                var grid = dialogWindow.FindFirstDescendant(cf =>
                    cf.ByControlType(ControlType.DataGrid).Or(cf.ByControlType(ControlType.Table)));

                AutomationElement? row = null;

                if (grid != null && grid.Patterns.ItemContainer.IsSupported)
                {
                    try
                    {
                        row = grid.Patterns.ItemContainer.Pattern
                            .FindItemByProperty(null, automation.PropertyLibrary.Element.Name, cleanFileName);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[FlaUI] ItemContainerPattern (таблица деталей) ошибка: {ex.Message}");
                    }
                }

                // Запасной вариант — как раньше, поиск среди всех потомков
                row ??= dialogWindow.FindAllDescendants(cf =>
                        cf.ByControlType(ControlType.Custom).Or(cf.ByControlType(ControlType.DataItem)))
                    .FirstOrDefault(e => e.FindAllChildren()
                        .Any(c => c.Name != null && c.Name.Contains(cleanFileName, StringComparison.OrdinalIgnoreCase)));

                if (row == null)
                {
                    Debug.WriteLine($"[FlaUI] Не найдена строка таблицы для '{cleanFileName}' — количество не выставлено.");
                    return;
                }

                if (row.Patterns.ScrollItem.IsSupported)
                {
                    try { row.Patterns.ScrollItem.Pattern.ScrollIntoView(); } catch { /* не критично */ }
                    Task.Delay(150).Wait();
                }

                Rectangle rowRect = row.BoundingRectangle;
                Point nameCellPoint = new Point(rowRect.X + (int)(rowRect.Width * 0.35), rowRect.Y + rowRect.Height / 2);
                Mouse.Click(nameCellPoint);
                Task.Delay(200).Wait();

                Keyboard.Type(VirtualKeyShort.TAB);
                Task.Delay(200).Wait();
                Keyboard.Type(VirtualKeyShort.F2);
                Task.Delay(150).Wait();
                Keyboard.Type(VirtualKeyShort.BACK);
                Task.Delay(100).Wait();

                foreach (char ch in quantity.ToString())
                {
                    Keyboard.Type(ch);
                    Task.Delay(50).Wait();
                }
                Task.Delay(200).Wait();

                Keyboard.Type(VirtualKeyShort.RETURN);
                Task.Delay(200).Wait();

                Debug.WriteLine($"[FlaUI] Количество для '{cleanFileName}' успешно введено через F2 + Type: {quantity}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FlaUI] Ошибка установки количества для '{cleanFileName}': {ex.Message}");
            }
        }

        private void SetClipboardTextSTA(string text)
        {
            var thread = new System.Threading.Thread(() =>
            {
                try
                {
                    System.Windows.Forms.Clipboard.SetText(text);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[FlaUI] Ошибка записи в буфер обмена: {ex.Message}");
                }
            });
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.Start();
            thread.Join();
        }

        private string GetAmperageForMaterial(double thickness, string steelGrade)
        {
            if (steelGrade.Contains("Ст3", StringComparison.OrdinalIgnoreCase) && Math.Abs(thickness - 2.0) < 0.5)
                return "80A";

            if (thickness >= 4.0 && thickness <= 8.0)
                return "80A";

            if (thickness >= 10.0 && thickness <= 18.0)
                return "130A";

            if (thickness > 18.0)
                return "260A";

            return "80A";
        }

        /// <summary>
        /// Клик с защитой от протухшей ссылки на элемент (WPF переиспользует контейнеры при
        /// виртуализации списков). Если элемент отключён или UIA бросает
        /// ElementNotEnabledException/ElementNotAvailableException — элемент ищется заново через
        /// refetch и клик повторяется, вместо падения всей автоматизации.
        /// </summary>
        private bool ClickElementSafe(AutomationElement element, Func<AutomationElement?> refetch, int maxRetries = 3)
        {
            var current = element;

            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    if (current == null)
                    {
                        current = refetch();
                        if (current == null) return false;
                    }

                    bool enabled = true;
                    try { enabled = current.Properties.IsEnabled.Value; } catch { /* не все элементы сообщают IsEnabled */ }

                    if (!enabled)
                    {
                        Debug.WriteLine("[FlaUI] Элемент отключён — обновляю ссылку и повторяю попытку.");
                        current = refetch();
                        Task.Delay(150).Wait();
                        continue;
                    }

                    ClickElement(current);
                    return true;
                }
                catch (ElementNotEnabledException)
                {
                    Debug.WriteLine("[FlaUI] ElementNotEnabledException — ссылка устарела, обновляю и повторяю...");
                    current = refetch();
                    Task.Delay(200).Wait();
                }
                catch (ElementNotAvailableException)
                {
                    Debug.WriteLine("[FlaUI] ElementNotAvailableException — ссылка устарела, обновляю и повторяю...");
                    current = refetch();
                    Task.Delay(200).Wait();
                }
            }

            Debug.WriteLine("[FlaUI] Не удалось кликнуть элемент после нескольких попыток.");
            return false;
        }

        private void ClickElement(AutomationElement element)
        {
            if (element.Patterns.Invoke.IsSupported)
            {
                element.Patterns.Invoke.Pattern.Invoke();
            }
            else if (element.Patterns.SelectionItem.IsSupported)
            {
                element.Patterns.SelectionItem.Pattern.Select();
            }
            else
            {
                Rectangle rect = element.BoundingRectangle;
                Point centerPoint = new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
                Mouse.Click(centerPoint);
            }
        }

        private T? WaitForElement<T>(Func<T?> elementFetcher, int timeoutSeconds) where T : class
        {
            int maxAttempts = timeoutSeconds * 2;
            for (int i = 0; i < maxAttempts; i++)
            {
                var element = elementFetcher();
                if (element != null) return element;
                Task.Delay(500).Wait();
            }
            return null;
        }
    }
}
