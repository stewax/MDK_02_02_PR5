namespace LicenseServer;

/// <summary>
/// Чёрный список IP-адресов клиентов. Хранится в текстовом файле (один адрес на строку),
/// поэтому сохраняется между перезапусками сервера.
/// </summary>
public sealed class Blacklist
{
    private readonly HashSet<string> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    public Blacklist(string filePath)
    {
        FilePath = filePath;
        Load();
    }

    public string FilePath { get; }

    public int Count
    {
        get { lock (_sync) return _items.Count; }
    }

    public bool Contains(string ip)
    {
        lock (_sync)
            return _items.Contains(ip);
    }

    /// <summary>Добавляет адрес. Возвращает false, если он уже был в списке.</summary>
    public bool Add(string ip)
    {
        lock (_sync)
        {
            if (!_items.Add(ip))
                return false;
            Save();
            return true;
        }
    }

    /// <summary>Удаляет адрес. Возвращает false, если его не было в списке.</summary>
    public bool Remove(string ip)
    {
        lock (_sync)
        {
            if (!_items.Remove(ip))
                return false;
            Save();
            return true;
        }
    }

    public IReadOnlyList<string> GetAll()
    {
        lock (_sync)
            return _items.OrderBy(x => x).ToList();
    }

    private void Load()
    {
        if (!File.Exists(FilePath))
            return;

        try
        {
            foreach (string raw in File.ReadAllLines(FilePath))
            {
                string line = raw.Trim();
                if (line.Length > 0 && !line.StartsWith('#'))
                    _items.Add(line);
            }
        }
        catch (IOException ex)
        {
            Log.Error($"Не удалось прочитать чёрный список: {ex.Message}");
        }
    }

    private void Save()
    {
        try
        {
            var lines = new List<string> { "# Чёрный список IP-адресов сервера лицензий" };
            lines.AddRange(_items.OrderBy(x => x));
            File.WriteAllLines(FilePath, lines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Не удалось сохранить чёрный список: {ex.Message}");
        }
    }
}
