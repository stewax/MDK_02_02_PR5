namespace LicenseClient;

/// <summary>Отрисовка информационной панели клиента (обновляется раз в секунду).</summary>
public static class ConsoleView
{
    private const int EventLines = 8;

    public static async Task RunAsync(ClientState state, ClientSettings settings, CancellationToken ct)
    {
        try
        {
            Console.CursorVisible = false;
            Console.Clear();
        }
        catch (IOException) { }

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        try
        {
            do
            {
                Render(state.Snapshot(), settings);
            } while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) { }
        finally
        {
            try { Console.CursorVisible = true; } catch (IOException) { }
        }
    }

    private static void Render(ClientSnapshot s, ClientSettings settings)
    {
        var lines = new List<(string Text, ConsoleColor Color)>
        {
            ("=================================================", ConsoleColor.Cyan),
            ("          КЛИЕНТ МЕНЕДЖЕРА ЛИЦЕНЗИЙ (TCP)", ConsoleColor.Cyan),
            ("=================================================", ConsoleColor.Cyan),
            ($" Сервер:                    {settings.ServerIp}:{settings.ServerPort}", ConsoleColor.Gray),
            ($" Состояние:                 {s.Status}", s.StatusColor),
            ($" Уникальный код:            {s.Code ?? "—"}", s.Code is null ? ConsoleColor.Gray : ConsoleColor.Green),
            ($" Дата и время подключения:  {s.ConnectedAt?.ToString("dd.MM.yyyy HH:mm:ss") ?? "—"}", ConsoleColor.Gray),
            ($" Продолжительность:         {(s.ConnectedAt is null ? "—" : FormatDuration(DateTime.Now - s.ConnectedAt.Value))}", ConsoleColor.Gray),
            ($" IP-адрес клиента:          {s.LocalIp ?? "—"}", ConsoleColor.Gray),
            ($" Порт клиента:              {s.LocalPort?.ToString() ?? "—"}", ConsoleColor.Gray),
            ($" До обновления кода:        {(s.RemainingSeconds is null ? "—" : $"{s.RemainingSeconds} сек")}", ConsoleColor.Gray),
            ($" Последняя проверка:        {(s.LastCheck is null ? "—" : $"{s.LastCheck:HH:mm:ss} ({s.LastCheckResult})")}" +
             $"  [каждые {settings.CheckIntervalSeconds} сек]", ConsoleColor.Gray),
            (s.RetryAt is { } retry && retry > DateTime.Now
                ? $" Повторное подключение через {Math.Ceiling((retry - DateTime.Now).TotalSeconds)} сек"
                : "", ConsoleColor.DarkYellow),
            ("------------------- События ---------------------", ConsoleColor.DarkCyan)
        };

        for (int i = 0; i < EventLines; i++)
        {
            int index = s.Events.Count - EventLines + i;
            lines.Add(index >= 0
                ? ($" {s.Events[index].Time:HH:mm:ss}  {s.Events[index].Text}", s.Events[index].Color)
                : ("", ConsoleColor.Gray));
        }

        lines.Add(("-------------------------------------------------", ConsoleColor.DarkCyan));
        lines.Add((" Q или Esc — выход", ConsoleColor.DarkGray));

        try
        {
            int width = Math.Max(20, Console.WindowWidth - 1);
            Console.SetCursorPosition(0, 0);
            foreach (var (text, color) in lines)
            {
                Console.ForegroundColor = color;
                Console.WriteLine(text.Length > width ? text[..width] : text.PadRight(width));
            }
            Console.ResetColor();
        }
        catch (Exception ex) when (ex is IOException or ArgumentOutOfRangeException)
        {
            // Окно консоли изменили во время отрисовки — перерисуем на следующем тике.
        }
    }

    public static string FormatDuration(TimeSpan value) =>
        $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}";
}
