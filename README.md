<div align="center">

# GigaCleaner

**Уборка Windows, которая не трогает ваши данные.**

Очистка временных файлов, реестра и автозагрузки по белому списку путей. Работает локально, без телеметрии. Умеет по расписанию и в тихом режиме.

[Скачать установщик](https://github.com/TIZZ13/GigaCleaner/releases/latest) ·
[Сайт](https://tizz13.github.io/GigaCleaner/) ·
[Документация](#)

---

![C#](https://img.shields.io/badge/C%23-.NET_8-512BD4?logo=dotnet&logoColor=white)
![WPF](https://img.shields.io/badge/WPF-Windows-0078d4?logo=windows&logoColor=white)
![License](https://img.shields.io/github/license/TIZZ13/GigaCleaner)
![Version](https://img.shields.io/github/v/release/TIZZ13/GigaCleaner)
![Platform](https://img.shields.io/badge/platform-Windows%2010%2B-0078d4)

</div>

---

## О программе

**GigaCleaner** — десктопный помощник по очистке и оптимизации Windows. Главный принцип: **не удалять наугад**. Каждый путь проверяется по белому списку — Program Files, «Документы», System32 и профиль пользователя не затрагиваются.

Приложение сделано для обычных людей: одна кнопка «Очистить в 1 клик» для быстрой уборки и подробный отчёт о том, что было найдено, освобождено и пропущено.

## Возможности

| Модуль | Что делает |
|---|---|
| 🧹 **Очистка** | Временные файлы `%TEMP%` (старше 24 ч), кэши Chromium/Firefox/Edge, корзина — по белому списку путей |
| ⚡ **Оптимизация** | Автозагрузка и службы Windows: меньше фоновой нагрузки, быстрее старт. Каждое изменение обратимо |
| 🗂️ **Реестр** | Поиск и удаление устаревших записей. Перед изменениями создаётся точка восстановления |
| 🔄 **Обновления** | Проверка обновлений программ (winget) и драйверов (Windows Update) |
| 🛠️ **Инструменты** | Планировщик, горячая клавиша быстрой очистки, уведомления в трее |
| ⏰ **Автоматизация** | Уборка по расписанию и тихий режим `--silent` для автоматических сценариев |

## Скриншот

![GigaCleaner обзор](Assets/preview.png)

## Быстрый старт (сборка из исходников)

Требуется [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) и Windows 10 1809+.

```bash
git clone https://github.com/TIZZ13/GigaCleaner.git
cd GigaCleaner

# сборка
dotnet build -c Release

# публикация single-file exe (как в релизах)
dotnet publish -c Release -p:PublishProfile=PortableSingleFile

# установщик (нужен Inno Setup 6.7+)
ISCC.exe installer.iss
```

Этот процесс также собирает встроенный модуль уведомлений `GigaCleaner.Notifier`.

## Использование

```bash
# обычный запуск с графическим интерфейсом
GigaCleaner.exe

# тихая очистка без окна (фоновый режим)
GigaCleaner.exe --silent
```

Полные параметры и описание см. в [документации](docs/).

## Архитектура

```
GigaCleaner/
├── Services/          # Очистка, реестр, оптимизация, обновления, уведомления
├── ViewModels/        # MVVM-модели представлений
├── Views/             # WPF-страницы и окна
├── Controls/          # Стеклянные контролы (GlassCard, LogoGlyph и др.)
├── Themes/            # Палитры тёмной/светлой темы
├── Notifier/          # Отдельный процесс для Windows-уведомлений (без админа)
└── installer.iss      # Установщик на Inno Setup
```

Ключевые сервисы:

- `CleanerEngine` + `CleanCatalog` — поиск и удаление мусора по белому списку;
- `RegistryCleanerService` — очистка реестра с точкой восстановления;
- `WingetService` / `WindowsUpdateService` — обновления программ и драйверов;
- `NotificationService` — toast-уведомления через отдельный процесс без прав администратора;
- `SelfUpdateService` — проверка новой версии приложения по URL;
- `ScheduleService` / `SilentRunner` — расписание и фоновый режим `--silent`.

## Стек

- **C# / .NET 8**, WPF + `WPF-UI` (панели, темы)
- **MVVM**, Single-file publish (self-contained)
- **Inno Setup** — установщик с тёмной темой мастера
- **SQLite**, **TaskScheduler**, **Windows API** (реестр, службы, уведомления)

## Вклад в проект

Хотите помочь? Откройте [issue](https://github.com/TIZZ13/GigaCleaner/issues) или создайте pull request.

- [CONTRIBUTING.md](CONTRIBUTING.md) — гайд по разработке
- [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md) — кодекс поведения
- [SECURITY.md](SECURITY.md) — как сообщить об уязвимости

## Лицензия

Проект распространяется под лицензией **MIT** — [LICENSE](LICENSE). Просто используйте, изучайте и дорабатывайте.

---

*GigaCleaner работает локально и без телеметрии. Данные не покидают ваш компьютер.*