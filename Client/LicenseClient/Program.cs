using System.Text;
using LicenseClient;

try
{
    Console.OutputEncoding = Encoding.UTF8;
    Console.InputEncoding = Encoding.UTF8;
    if (!Console.IsOutputRedirected)
        Console.Title = "Клиент менеджера лицензий";
}
catch (IOException)
{
    // Консоль не поддерживает смену кодировки/заголовка — не критично.
}

Console.WriteLine("=================================================");
Console.WriteLine("          КЛИЕНТ МЕНЕДЖЕРА ЛИЦЕНЗИЙ (TCP)");
Console.WriteLine("=================================================");

var settings = ClientSettings.AskFromConsole();
var state = new ClientState();
var client = new LicenseClientService(settings, state);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

bool interactive = !Console.IsOutputRedirected && !Console.IsInputRedirected;
Task viewTask = Task.CompletedTask;

if (interactive)
{
    viewTask = ConsoleView.RunAsync(state, settings, cts.Token);

    // Ожидание нажатия Q / Esc для выхода
    _ = Task.Run(async () =>
    {
        while (!cts.IsCancellationRequested)
        {
            if (Console.KeyAvailable)
            {
                var key = Console.ReadKey(intercept: true).Key;
                if (key is ConsoleKey.Q or ConsoleKey.Escape)
                    cts.Cancel();
            }
            await Task.Delay(100);
        }
    });
}
else
{
    // Вывод в файл/конвейер: печатаем события построчно.
    state.EventLogged += (time, text, _) => Console.WriteLine($"[{time:HH:mm:ss}] {text}");
}

await client.RunAsync(cts.Token);
await viewTask;

if (interactive)
    Console.Clear();
Console.WriteLine("Клиент завершил работу.");
