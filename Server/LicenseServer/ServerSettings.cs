using System.Net;

namespace LicenseServer;

/// <summary>Настройки сервера, запрашиваемые у пользователя при запуске.</summary>
public sealed class ServerSettings
{
    public IPAddress IpAddress { get; set; } = IPAddress.Loopback;
    public int Port { get; set; } = 5000;

    /// <summary>Максимальное количество одновременно выданных уникальных кодов (лицензий).</summary>
    public int MaxClients { get; set; } = 5;

    /// <summary>Через сколько секунд после выдачи кода клиент отключается для обновления кода.</summary>
    public int SessionTimeoutSeconds { get; set; } = 60;

    /// <summary>Сколько секунд даётся клиенту на подтверждение полученного кода.</summary>
    public int ConfirmTimeoutSeconds { get; set; } = 10;

    public static ServerSettings AskFromConsole()
    {
        Console.WriteLine("Настройка сервера (Enter — значение по умолчанию):");
        var settings = new ServerSettings
        {
            IpAddress = ConsoleInput.AskIp("  IP-адрес сервера (0.0.0.0 — все интерфейсы)", "127.0.0.1"),
            Port = ConsoleInput.AskInt("  Порт сервера", 5000, 1, 65535),
            MaxClients = ConsoleInput.AskInt("  Количество уникальных кодов (лицензий)", 5, 1, 10000),
            SessionTimeoutSeconds = ConsoleInput.AskInt("  Время до отключения клиента для обновления кода, сек", 60, 5, 86400),
            ConfirmTimeoutSeconds = ConsoleInput.AskInt("  Время на подтверждение уникального кода, сек", 10, 1, 3600)
        };

        if (settings.ConfirmTimeoutSeconds >= settings.SessionTimeoutSeconds)
        {
            settings.ConfirmTimeoutSeconds = Math.Max(1, settings.SessionTimeoutSeconds / 2);
            Log.Warn($"Время подтверждения должно быть меньше времени сессии. Установлено: {settings.ConfirmTimeoutSeconds} сек.");
        }

        return settings;
    }

    public override string ToString() =>
        $"Адрес: {IpAddress}:{Port}{Environment.NewLine}" +
        $"Лицензий (уникальных кодов): {MaxClients}{Environment.NewLine}" +
        $"Отключение для обновления кода через: {SessionTimeoutSeconds} сек{Environment.NewLine}" +
        $"Время на подтверждение кода: {ConfirmTimeoutSeconds} сек";
}
