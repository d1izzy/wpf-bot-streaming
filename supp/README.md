# BotSupp Studio

Открывайте в Visual Studio файл `supp.sln` из этой папки.

## Что изменилось в ветке multibot

Ветка `feature/multibot-studio` начинает переделку проекта в универсальный конструктор/лаунчер ботов.

Главное изменение: `bot.py` теперь автономный. Он читает файлы из своей папки:

```text
.env
bot_config.json
scenario.json
faq.json
logs/bot.log
```

Это значит, что один и тот же `bot.py` можно копировать в разные папки ботов:

```text
%LOCALAPPDATA%\BotSuppRuntime\bots\support_bot\bot.py
%LOCALAPPDATA%\BotSuppRuntime\bots\shop_bot\bot.py
%LOCALAPPDATA%\BotSuppRuntime\bots\survey_bot\bot.py
```

## Запуск в Visual Studio

1. Откройте:

```text
supp.sln
```

2. Запустите проект `supp`.

При сборке Visual Studio копирует `bot.py` и `faq.json` в `RuntimeAssets`.

## Универсальный сценарий

Новый формат сценария лежит в:

```text
scenario.json
```

Пример кнопки перехода:

```json
{
  "text": "Информация",
  "action": "go",
  "target": "info"
}
```

Поддерживаемые действия:

- `go` — перейти к другому шагу;
- `message` — отправить сообщение без перехода;
- `operator` — запросить оператора;
- `start` — вернуться в начало;
- `end` — завершить диалог.

## Прокси

WPF должен запускать общий прокси и передавать боту переменную:

```text
BOTSUPP_PROXY_URL=socks5://127.0.0.1:10808
```

`bot.py` автоматически использует эту переменную.

## Следующий этап

Следующий шаг — переделать WPF:

- добавить список ботов слева;
- добавить `bots_registry.json`;
- сделать отдельный процесс Python для каждого бота;
- сделать общую консоль с фильтром по выбранному боту.
