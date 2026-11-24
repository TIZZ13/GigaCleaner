using System.Collections.Generic;
using System.Windows;

namespace GigaCleaner.Services;

/// <summary>Языки интерфейса, поддерживаемые приложением.</summary>
public enum AppLanguage
{
    Ru,
    En,
    De,
    Fr,
    Es,
}

/// <summary>
/// Простая встроенная локализация: словарь ключевых строк для RU/EN/DE/FR/ES.
/// Хранение выбранного языка — в SettingsStore; переключение меняет словарь на лету.
/// </summary>
public static class Localization
{
    private static readonly Dictionary<AppLanguage, string> Codes = new()
    {
        [AppLanguage.Ru] = "ru",
        [AppLanguage.En] = "en",
        [AppLanguage.De] = "de",
        [AppLanguage.Fr] = "fr",
        [AppLanguage.Es] = "es",
    };

    private static readonly Dictionary<AppLanguage, string> Names = new()
    {
        [AppLanguage.Ru] = "Русский",
        [AppLanguage.En] = "English",
        [AppLanguage.De] = "Deutsch",
        [AppLanguage.Fr] = "Français",
        [AppLanguage.Es] = "Español",
    };

    private static AppLanguage _current = AppLanguage.Ru;

    public static AppLanguage Current => _current;

    /// <summary>Выбранный язык, который сохраняется в настройках.</summary>
    public static string CurrentCode => Codes[_current];

    /// <summary>Человекочитаемое имя языка для списка выбора.</summary>
    public static string NameOf(AppLanguage language) => Names[language];

    public static IReadOnlyList<AppLanguage> Supported { get; } =
        [AppLanguage.Ru, AppLanguage.En, AppLanguage.De, AppLanguage.Fr, AppLanguage.Es];

    /// <summary>Применяет сохранённый язык (вызывается при старте приложения).</summary>
    public static void ApplySaved()
    {
        var saved = SettingsStore.Language;
        _current = TryParse(saved, out var parsed) ? parsed : AppLanguage.Ru;
    }

    public static void Set(AppLanguage language, bool persist = true)
    {
        _current = language;
        if (persist) SettingsStore.SaveLanguage(Codes[language]);
    }

    public static bool TryParse(string? code, out AppLanguage language)
    {
        foreach (var pair in Codes)
        {
            if (string.Equals(pair.Value, code, System.StringComparison.OrdinalIgnoreCase))
            {
                language = pair.Key;
                return true;
            }
        }
        language = AppLanguage.Ru;
        return false;
    }

    // ---------- Словарь ключевых строк ----------

    private static readonly Dictionary<string, IReadOnlyDictionary<AppLanguage, string>> Strings = new()
    {
        ["app.name"] = Map("GigaCleaner", "GigaCleaner", "GigaCleaner", "GigaCleaner", "GigaCleaner"),
        ["nav.overview"] = Map("Обзор", "Overview", "Übersicht", "Aperçu", "Resumen"),
        ["nav.clean"] = Map("Очистка", "Cleaner", "Reinigung", "Nettoyage", "Limpieza"),
        ["nav.optimize"] = Map("Оптимизация", "Optimizer", "Optimierung", "Optimisation", "Optimización"),
        ["nav.registry"] = Map("Реестр", "Registry", "Registry", "Registre", "Registro"),
        ["nav.updates"] = Map("Обновления", "Updates", "Updates", "Mises à jour", "Actualizaciones"),
        ["nav.tools"] = Map("Инструменты", "Tools", "Werkzeuge", "Outils", "Herramientas"),
        ["nav.automation"] = Map("Автоматизация", "Automation", "Automatisierung", "Automatisation", "Automatización"),
        ["clean.now"] = Map("Очистить в 1 клик", "Clean now", "Jetzt reinigen", "Nettoyer", "Limpiar"),
        ["clean.check"] = Map("Проверить", "Check", "Prüfen", "Vérifier", "Comprobar"),
        ["theme.dark"] = Map("Тёмная тема", "Dark theme", "Dunkles Design", "Thème sombre", "Tema oscuro"),
        ["theme.light"] = Map("Светлая тема", "Light theme", "Helles Design", "Thème clair", "Tema claro"),
        ["about"] = Map("О программе", "About", "Über", "À propos", "Acerca de"),
        ["settings.language"] = Map("Язык", "Language", "Sprache", "Langue", "Idioma"),
        ["hotkey"] = Map("Хоткей", "Hotkey", "Tastenkürzel", "Raccourci", "Atajo"),
        ["rights.admin"] = Map("Права администратора", "Administrator rights", "Administratorrechte", "Droits administrateur", "Derechos de administrador"),
        ["rights.restricted"] = Map("Ограниченный режим", "Restricted mode", "Eingeschränkter Modus", "Mode restreint", "Modo restringido"),
        ["exit"] = Map("Выход", "Exit", "Beenden", "Quitter", "Salir"),
        ["open"] = Map("Открыть GigaCleaner", "Open GigaCleaner", "GigaCleaner öffnen", "Ouvrir GigaCleaner", "Abrir GigaCleaner"),
    };

    private static IReadOnlyDictionary<AppLanguage, string> Map(string ru, string en, string de, string fr, string es) =>
        new Dictionary<AppLanguage, string>
        {
            [AppLanguage.Ru] = ru,
            [AppLanguage.En] = en,
            [AppLanguage.De] = de,
            [AppLanguage.Fr] = fr,
            [AppLanguage.Es] = es,
        };

    /// <summary>Переводит ключ на текущий язык; при отсутствии возвращает ключ.</summary>
    public static string T(string key)
    {
        return Strings.TryGetValue(key, out var table) && table.TryGetValue(_current, out var value)
            ? value
            : key;
    }
}