# Бот поддержки для Telegram

WPF-конфигуратор + Python Telegram-бот поддержки.

## Как открыть проект

Открывайте в Visual Studio именно файл:

```text
supp/supp.sln
```

Не переносите отдельно папку `supp/supp`, потому что `bot.py` и `faq.json` лежат в корне репозитория и подключаются в проект как `RuntimeAssets`.

## RuntimeAssets

В `supp/supp/supp.csproj` файлы подключены так:

```xml
<None Include="..\..\bot.py">
  <Link>RuntimeAssets\bot.py</Link>
  <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
</None>
<None Include="..\..\faq.json">
  <Link>RuntimeAssets\faq.json</Link>
  <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
</None>
```

После сборки Visual Studio автоматически создаст рядом с `supp.exe`:

```text
RuntimeAssets/bot.py
RuntimeAssets/faq.json
```

## Прокси

Логика программы такая: пользователь вводит `BOT_TOKEN` и `ADMIN_ID`, нажимает Start в WPF-консоли, WPF сначала запускает локальный прокси, потом запускает Python-бота через этот прокси.

Для этого в готовой portable-папке рядом с `supp.exe` должна быть папка:

```text
proxy_runtime/
  config.json
  xray.exe
```

или `sing-box.exe` вместо `xray.exe`.

В репозитории хранится только `proxy_runtime/config.json`. Большой `xray.exe` лучше не хранить в исходниках — положите его локально в `proxy_runtime/` перед сборкой portable ZIP или прикрепите готовый ZIP в GitHub Releases.

## Сборка portable ZIP

На Windows:

```powershell
.\prepare_python_runtime.ps1
.\build_portable_zip.ps1
```

Готовый архив будет тут:

```text
dist/BotSuppPortable.zip
```

## Настройка

`.env` не коммитится. Его создаёт/редактирует WPF-программа. Минимальные переменные:

```text
BOT_TOKEN=ваш_токен
ADMIN_ID=ваш_telegram_id
```
