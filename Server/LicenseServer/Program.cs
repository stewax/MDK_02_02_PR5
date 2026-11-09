using System.Net.Sockets;
using System.Text;
using LicenseServer;

try
{
    Console.OutputEncoding = Encoding.UTF8;
    Console.InputEncoding = Encoding.UTF8;
    if (!Console.IsOutputRedirected)
        Console.Title = "Сервер менеджера лицензий";
}
catch (IOException)
{
    // Консоль не поддерживает смену кодировки/заголовка — не критично.
}

Console.WriteLine("=========================================");
Console.WriteLine("      СЕРВЕР МЕНЕДЖЕРА ЛИЦЕНЗИЙ (TCP)");
Console.WriteLine("=========================================");

var settings = ServerSettings.AskFromConsole();
var blacklist = new Blacklist(Path.Combine(AppContext.BaseDirectory, "blacklist.txt"));
var server = new LicenseManagerServer(settings, blacklist);

try
{
    server.Start();
}
catch (SocketException ex)
{
    Log.Error($"Не удалось запустить сервер на {settings.IpAddress}:{settings.Port}: {ex.Message}");
    return 1;
}

Console.WriteLine();
Log.Success($"Сервер запущен на {settings.IpAddress}:{settings.Port}");
Log.Info($"Лицензий: {settings.MaxClients}, сессия: {settings.SessionTimeoutSeconds} сек, " +
         $"подтверждение кода: {settings.ConfirmTimeoutSeconds} сек");
Log.Info($"Чёрный список: {blacklist.Count} адрес(ов), файл {blacklist.FilePath}");
PrintHelp();

var stopping = false;
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stopping = true;
};

while (!stopping)
{
    string? line = Console.ReadLine();
    if (line is null)
        break; // ввод закрыт

    string[] parts = line.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (parts.Length == 0)
        continue;

    string command = parts[0].ToLowerInvariant();
    string argument = parts.Length > 1 ? parts[1] : "";

    switch (command)
    {
        case "help" or "?" or "помощь":
            PrintHelp();
            break;

        case "list" or "clients" or "ls":
            PrintClients(server);
            break;

        case "kick":
            if (argument.Length == 0)
                Log.Error("Использование: kick <№ клиента | код>");
            else if (await server.KickAsync(argument))
                Log.Success($"Клиент {argument} отключён");
            else
                Log.Error($"Клиент «{argument}» не найден. Посмотрите номера командой list.");
            break;

        case "ban":
            if (argument.Length == 0)
            {
                Log.Error("Использование: ban <№ клиента | код | IP-адрес>");
                break;
            }
            var ban = await server.BanAsync(argument);
            if (ban.Ip is null)
                Log.Error($"«{argument}» — не номер клиента, не код и не IP-адрес");
            else
                Log.Success((ban.Added ? $"IP {ban.Ip} добавлен в чёрный список" : $"IP {ban.Ip} уже в чёрном списке") +
                            $". Отключено клиентов: {ban.Disconnected}");
            break;

        case "unban":
            if (argument.Length == 0)
                Log.Error("Использование: unban <IP-адрес>");
            else if (server.Unban(argument))
                Log.Success($"IP {argument} удалён из чёрного списка");
            else
                Log.Error($"IP {argument} нет в чёрном списке");
            break;

        case "blacklist" or "bl":
            PrintBlacklist(blacklist);
            break;

        case "settings":
            Log.Block(() => Console.WriteLine(settings));
            break;

        case "clear" or "cls":
            if (!Console.IsOutputRedirected)
                Console.Clear();
            break;

        case "exit" or "quit" or "stop":
            stopping = true;
            break;

        default:
            Log.Error($"Неизвестная команда «{command}». Введите help для списка команд.");
            break;
    }
}

Log.Warn("Остановка сервера...");
await server.StopAsync();
Log.Info("Сервер остановлен");
return 0;


static void PrintHelp()
{
    Log.Block(() =>
    {
        Console.WriteLine();
        Console.WriteLine("Команды:");
        Console.WriteLine("  list               — список подключённых клиентов");
        Console.WriteLine("  kick <№|код>       — отключить клиента");
        Console.WriteLine("  ban <№|код|IP>     — добавить клиента (его IP) в чёрный список и отключить");
        Console.WriteLine("  unban <IP>         — удалить IP из чёрного списка");
        Console.WriteLine("  blacklist          — показать чёрный список");
        Console.WriteLine("  settings           — текущие настройки сервера");
        Console.WriteLine("  clear              — очистить экран");
        Console.WriteLine("  help               — эта справка");
        Console.WriteLine("  exit               — остановить сервер");
        Console.WriteLine();
    });
}

static void PrintClients(LicenseManagerServer server)
{
    var sessions = server.Sessions;
    Log.Block(() =>
    {
        Console.WriteLine();
        Console.WriteLine($"Подключено клиентов: {sessions.Count}. " +
                          $"Занято лицензий: {server.UsedLicenses}/{server.Settings.MaxClients}");
        if (sessions.Count == 0)
        {
            Console.WriteLine();
            return;
        }

        const string row = "{0,-4} {1,-15} {2,-15} {3,-6} {4,-19} {5,-9} {6,-7} {7}";
        Console.WriteLine(string.Format(row, "№", "Код", "IP-адрес", "Порт", "Подключён", "Длит.", "Осталось", "Статус"));
        Console.WriteLine(new string('-', 100));
        foreach (var s in sessions)
        {
            Console.WriteLine(string.Format(row,
                s.Id,
                s.Code ?? "—",
                s.Ip,
                s.Port,
                s.ConnectedAt.ToString("dd.MM.yyyy HH:mm:ss"),
                Format.Duration(s.Duration),
                s.Code is null ? "—" : $"{s.GetRemainingSeconds(server.Settings.SessionTimeoutSeconds)} с",
                s.Status));
        }
        Console.WriteLine();
    });
}

static void PrintBlacklist(Blacklist blacklist)
{
    var items = blacklist.GetAll();
    Log.Block(() =>
    {
        Console.WriteLine();
        Console.WriteLine(items.Count == 0 ? "Чёрный список пуст" : $"Чёрный список ({items.Count}):");
        foreach (string ip in items)
            Console.WriteLine($"  {ip}");
        Console.WriteLine();
    });
}
