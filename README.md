<div align="center">

# GigaCleaner

**A Windows cleaner that never touches your data.**

Cleans temp files, registry, and startup entries using a strict whitelist of paths.
Runs 100% locally, no telemetry. Supports scheduling and a silent mode.

[Download installer](https://github.com/TIZZ13/GigaCleaner/releases/latest) ·
[Website](https://tizz13.github.io/GigaCleaner/)

---

![C#](https://img.shields.io/badge/C%23-.NET_8-512BD4?logo=dotnet&logoColor=white)
![WPF](https://img.shields.io/badge/WPF-Windows-0078d4?logo=windows&logoColor=white)
![License](https://img.shields.io/github/license/TIZZ13/GigaCleaner)
![Version](https://img.shields.io/github/v/release/TIZZ13/GigaCleaner)
![Platform](https://img.shields.io/badge/platform-Windows%2010%2B-0078d4)
![PRs](https://img.shields.io/badge/PRs-welcome-brightgreen)

</div>

---

## English

### About

**GigaCleaner** is a desktop utility for cleaning and optimizing Windows. Its core principle: **never delete blindly**. Every path is checked against a strict whitelist — Program Files, Documents, System32, and your user profile are never touched.

Built for everyday users: one-click "Clean now" for quick maintenance, plus a detailed report showing what was found, freed, and skipped.

### Features

| Module | What it does |
|---|---|
| 🧹 **Cleaner** | Temp files `%TEMP%` (older than 24h), Chromium/Firefox/Edge caches, Recycle Bin — via a strict whitelist |
| ⚡ **Optimizer** | Startup and Windows services: less background load, faster boot. Every change is reversible |
| 🗂️ **Registry** | Finds and removes stale registry entries. Creates a System Restore point before changes |
| 🔄 **Updates** | Program updates (winget) and driver updates (Windows Update) in one place |
| 🛠️ **Tools** | Scheduler, global hotkey for quick clean, tray notifications |
| ⏰ **Automation** | Scheduled cleanups and silent mode `--silent` for unattended scenarios |

### Screenshot

![GigaCleaner overview](Assets/preview.png)

### Build from source

Requires [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) and Windows 10 1809+.

```bash
git clone https://github.com/TIZZ13/GigaCleaner.git
cd GigaCleaner

# build
dotnet build -c Release

# publish single-file exe (as in releases)
dotnet publish -c Release -p:PublishProfile=PortableSingleFile

# installer (needs Inno Setup 6.7+)
ISCC.exe installer.iss
```

This also builds the embedded notification module `GigaCleaner.Notifier`.

### Usage

```bash
# normal GUI launch
GigaCleaner.exe

# silent cleanup without a window (background mode)
GigaCleaner.exe --silent
```

### Architecture

```
GigaCleaner/
├── Services/          # cleaning, registry, optimizer, updates, notifications
├── ViewModels/        # MVVM view models
├── Views/             # WPF pages and windows
├── Controls/          # glass-style controls (GlassCard, LogoGlyph, ...)
├── Themes/            # dark/light palettes
├── Notifier/          # separate process for Windows toasts (no admin needed)
└── installer.iss      # Inno Setup installer
```

Key services:

- `CleanerEngine` + `CleanCatalog` — whitelist-based junk scanning and removal;
- `RegistryCleanerService` — registry cleanup with System Restore points;
- `WingetService` / `WindowsUpdateService` — software and driver updates;
- `NotificationService` — toast notifications from a non-elevated process;
- `SelfUpdateService` — checks for new app versions via URL;
- `ScheduleService` / `SilentRunner` — scheduling and `--silent` mode.

### Tech stack

- **C# / .NET 8**, WPF + `WPF-UI`
- **MVVM**, self-contained single-file publish
- **Inno Setup** — installer with a dark wizard theme
- **SQLite**, **TaskScheduler**, **Windows API**

### Contributing

Please read [CONTRIBUTING.md](CONTRIBUTING.md), [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md),
and [SECURITY.md](SECURITY.md) before opening issues or PRs.

### License

**MIT** — see [LICENSE](LICENSE). Use it, study it, improve it.

**Privacy** — see [PRIVACY.md](PRIVACY.md). The app is fully local: no telemetry, no accounts, nothing leaves your PC except a periodic self-update check.

**Third-party software** — see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for the licenses of used libraries and fonts.

---

## Русский

### О программе

**GigaCleaner** — настольная утилита для очистки и оптимизации Windows. Главный принцип: **не удалять наугад**. Каждый путь проверяется по белому списку — Program Files, «Документы», System32 и профиль пользователя не затрагиваются.

Приложение сделано для обычных людей: одна кнопка «Очистить в 1 клик» для быстрой уборки и подробный отчёт о том, что было найдено, освобождено и пропущено.

### Возможности

| Модуль | Что делает |
|---|---|
| 🧹 **Очистка** | Временные файлы `%TEMP%` (старше 24 ч), кэши Chromium/Firefox/Edge, корзина — по белому списку |
| ⚡ **Оптимизация** | Автозагрузка и службы Windows: меньше фоновой нагрузки, быстрее старт. Каждое изменение обратимо |
| 🗂️ **Реестр** | Поиск и удаление устаревших записей. Перед изменениями создаётся точка восстановления |
| 🔄 **Обновления** | Проверка обновлений программ (winget) и драйверов (Windows Update) |
| 🛠️ **Инструменты** | Планировщик, горячая клавиша быстрой очистки, уведомления в трее |
| ⏰ **Автоматизация** | Уборка по расписанию и тихий режим `--silent` |

### Скриншот

![GigaCleaner обзор](Assets/preview.png)

### Сборка из исходников

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

### Использование

```bash
# обычный запуск с графическим интерфейсом
GigaCleaner.exe

# тихая очистка без окна (фоновый режим)
GigaCleaner.exe --silent
```

### Архитектура

```
GigaCleaner/
├── Services/          # очистка, реестр, оптимизация, обновления, уведомления
├── ViewModels/        # MVVM-модели представлений
├── Views/             # WPF-страницы и окна
├── Controls/          # стеклянные контролы (GlassCard, LogoGlyph и др.)
├── Themes/            # палитры тёмной/светлой темы
├── Notifier/          # отдельный процесс для уведомлений (без прав администратора)
└── installer.iss      # установщик на Inno Setup
```

### Стек

- **C# / .NET 8**, WPF + `WPF-UI`
- **MVVM**, single-file publish (self-contained)
- **Inno Setup** — установщик с тёмной темой мастера
- **SQLite**, **TaskScheduler**, **Windows API**

### Вклад в проект

Прочитайте [CONTRIBUTING.md](CONTRIBUTING.md), [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md)
и [SECURITY.md](SECURITY.md) перед созданием issue или PR.

### Лицензия

Проект распространяется под лицензией **MIT** — см. [LICENSE](LICENSE). Просто используйте, изучайте и дорабатывайте.

---

*GigaCleaner runs locally and without telemetry. Your data never leaves your computer.*
*GigaCleaner работает локально и без телеметрии. Ваши данные не покидают ваш компьютер.*