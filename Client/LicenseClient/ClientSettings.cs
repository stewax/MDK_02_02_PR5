using System.Net;

namespace LicenseClient;

/// <summary>Настройки клиента, запрашиваемые у пользователя при запуске.</summary>
public sealed class ClientSettings
{
    public IPAddress ServerIp { get; set; } = IPAddress.Loopback;
    public int ServerPort { get; set; } = 5000;

    /// <summary>Как часто проверять у сервера, действителен ли код и не заблокирован ли клиент.</summary>
    public int CheckIntervalSeconds { get; set; } = 5;

    /// <summary>Пауза перед повторным подключением после ошибки/отказа.</summary>
    public int RetrySeconds { get; set; } = 5;

    public static ClientSettings AskFromConsole()
    {
        Console.WriteLine("Настройка клиента (Enter — значение по умолчанию):");
        return new ClientSettings
        {
            ServerIp = AskIp("  IP-адрес сервера", "127.0.0.1"),
            ServerPort = AskInt("  Порт сервера", 5000, 1, 65535),
            CheckIntervalSeconds = AskInt("  Интервал проверки кода, сек", 5, 1, 3600)
        };
    }

    private static int AskInt(string label, int defaultValue, int min, int max)
    {
        while (true)
        {
            Console.Write($"{label} [{defaultValue}]: ");
            string? input = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(input))
                return defaultValue;
            if (int.TryParse(input, out int value) && value >= min && value <= max)
                return value;
            Console.WriteLine($"  Ошибка: введите целое число от {min} до {max}.");
        }
    }

    private static IPAddress AskIp(string label, string defaultValue)
    {
        while (true)
        {
            Console.Write($"{label} [{defaultValue}]: ");
            string? input = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(input))
                input = defaultValue;
            if (input.Contains('.') && IPAddress.TryParse(input, out var address))
                return address;
            Console.WriteLine("  Ошибка: некорректный IPv4-адрес. Пример: 127.0.0.1");
        }
    }
}
