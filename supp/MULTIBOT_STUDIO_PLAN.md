# BotSupp Studio: план переделки под мультиботы

Цель: превратить текущий WPF-конфигуратор в программу, через которую можно создавать, настраивать и запускать несколько Telegram-ботов, а не только бота поддержки.

## Выбранная архитектура

Используем гибридный вариант:

- один WPF-процесс управляет всеми ботами;
- каждый бот запускается отдельным процессом Python;
- у каждого бота своя рабочая папка, `.env`, сценарий и логи;
- локальный прокси общий для всей программы;
- консоль в интерфейсе одна, но с фильтром по выбранному боту или режимом "все боты".

## Рабочая папка

Новая структура рантайма:

```text
%LOCALAPPDATA%\BotSuppRuntime\
├── bots_registry.json
├── bots\
│   ├── support_bot\
│   │   ├── .env
│   │   ├── bot.py
│   │   ├── faq.json
│   │   ├── scenario.json
│   │   └── logs\bot.log
│   ├── shop_bot\
│   │   ├── .env
│   │   ├── bot.py
│   │   ├── scenario.json
│   │   └── logs\bot.log
│   └── survey_bot\
│       └── ...
└── shared\
    └── proxy_runtime\
        ├── xray.exe / sing-box.exe
        └── config.json
```

## bots_registry.json

```json
{
  "version": 1,
  "bots": [
    {
      "id": "support_bot",
      "name": "Бот поддержки",
      "folder": "bots/support_bot",
      "enabled": true,
      "autostart": false,
      "useProxy": true,
      "entryPoint": "bot.py",
      "template": "support"
    }
  ]
}
```

## Как будет работать запуск

1. Пользователь выбирает бота слева.
2. Нажимает Start.
3. WPF проверяет, нужен ли этому боту прокси.
4. Если нужен и общий прокси не запущен — WPF запускает `xray.exe` или `sing-box.exe`.
5. WPF запускает отдельный процесс Python в папке выбранного бота.
6. В переменные окружения процесса передается `BOTSUPP_PROXY_URL=socks5://127.0.0.1:10808`.
7. Логи процесса пишутся в `bots/<bot_id>/logs/bot.log` и отображаются в общей консоли с префиксом имени бота.

## UI

План интерфейса:

```text
BotSupp Studio                                      Прокси: online/offline
┌────────────────────┬────────────────────────────────────────────────────┐
│ Боты               │ [Настройки] [Конструктор] [Логи]                   │
│                    │                                                    │
│ 🟢 Support         │ Контент выбранной вкладки для выбранного бота       │
│ ⚪ Shop            │                                                    │
│ 🔴 Survey          │                                                    │
│                    │                                                    │
│ [+ Добавить]       │                                                    │
│ [Удалить]          │                                                    │
├────────────────────┴────────────────────────────────────────────────────┤
│ [Start] [Stop] [Restart]  Статус выбранного бота                         │
├─────────────────────────────────────────────────────────────────────────┤
│ Консоль: [Выбранный бот ▼] [ ] Все боты                                  │
└─────────────────────────────────────────────────────────────────────────┘
```

## Изменения в WPF

### Новые классы

```csharp
public class BotProfile : INotifyPropertyChanged
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Folder { get; set; }
    public bool Enabled { get; set; }
    public bool Autostart { get; set; }
    public bool UseProxy { get; set; }
    public string EntryPoint { get; set; } = "bot.py";
    public string Status { get; set; } = "offline";
}
```

### Процессы

Вместо одного процесса:

```csharp
private Process _botProcess;
```

нужно перейти на словари:

```csharp
private readonly Dictionary<string, Process> _botProcesses = new Dictionary<string, Process>();
private readonly Dictionary<string, List<string>> _botLogs = new Dictionary<string, List<string>>();
private readonly ObservableCollection<BotProfile> _bots = new ObservableCollection<BotProfile>();
private BotProfile _selectedBot;
```

### Логи

```csharp
private void AppendLog(string botId, string message)
{
    var line = $"[{DateTime.Now:HH:mm:ss}] [{botId}] {message}";
    File.AppendAllText(GetBotLogPath(botId), line + Environment.NewLine);

    Dispatcher.Invoke(() =>
    {
        if (_showAllLogs || _selectedBot?.Id == botId)
        {
            LogTextBox.AppendText(line + Environment.NewLine);
            LogTextBox.ScrollToEnd();
        }
    });
}
```

## Изменения в bot.py

Первый этап — сделать `bot.py` автономным:

```python
BASE_DIR = Path(__file__).resolve().parent
load_dotenv(BASE_DIR / ".env")
FAQ_FILE_PATH = BASE_DIR / "faq.json"
SCENARIO_FILE_PATH = BASE_DIR / "scenario.json"
```

После этого один и тот же `bot.py` можно копировать в разные папки ботов.

## Переименование FAQ-редактора

Старую вкладку `FAQ-редактор` нужно постепенно переделать в:

- `Конструктор`
- или `Редактор сценария`

На первом этапе можно оставить старый формат `faq.json`, но в интерфейсе убрать привязку к слову FAQ.

## Этапы реализации

### Этап 1 — безопасная основа

- [ ] Создать ветку `feature/multibot-studio`.
- [ ] Добавить этот план.
- [ ] Сделать `bot.py` автономным относительно своей папки.
- [ ] Подготовить `bots_registry.json`.
- [ ] Добавить создание дефолтного бота при первом запуске.

### Этап 2 — WPF мультибот

- [ ] Добавить класс `BotProfile`.
- [ ] Добавить список ботов слева.
- [ ] Перевести `_botProcess` на `Dictionary<string, Process>`.
- [ ] Переделать Start/Stop/Restart под выбранного бота.
- [ ] Сделать отдельные `.env` для каждого бота.

### Этап 3 — консоль и прокси

- [ ] Сделать общий `ProxyService`.
- [ ] Запускать прокси при старте первого бота.
- [ ] Останавливать прокси при остановке последнего бота или оставить настройку.
- [ ] Сделать лог выбранного бота и режим "все боты".

### Этап 4 — универсальный конструктор

- [ ] Переименовать FAQ-редактор в `Конструктор`.
- [ ] Добавить `scenario.json`.
- [ ] Сделать шаблоны: Поддержка, Меню, Магазин, Опрос.
- [ ] Добавить экспертный режим для запуска внешнего Python-файла.

## Важно

Текущая рабочая версия в `main` не ломается. Все изменения нужно делать в ветке:

```text
feature/multibot-studio
```
