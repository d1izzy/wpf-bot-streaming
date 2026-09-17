# BotSupp

Открывайте в Visual Studio файл `supp.sln` из этой папки.

В этой папке лежат все нужные исходные файлы для проекта:

- `bot.py` — Python Telegram-бот;
- `faq.json` — база FAQ;
- `requirements.txt` — зависимости Python;
- `proxy_runtime/config.json` — конфиг локального прокси;
- `supp/supp.csproj` — WPF-проект.

При сборке Visual Studio автоматически копирует `bot.py` и `faq.json` в:

```text
supp/bin/Release/RuntimeAssets/
```

Это настроено в `supp/supp.csproj`:

```xml
<None Include="..\bot.py">
  <Link>RuntimeAssets\bot.py</Link>
  <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
</None>
<None Include="..\faq.json">
  <Link>RuntimeAssets\faq.json</Link>
  <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
</None>
```

Для portable-сборки на Windows:

```powershell
.\prepare_python_runtime.ps1
.\build_portable_zip.ps1
```

Для автопрокси перед сборкой положите `xray.exe` или `sing-box.exe` в `proxy_runtime/` рядом с `config.json`.
