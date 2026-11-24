namespace GigaCleaner.Services;

public static class NavigationService
{
    public static event Action<string>? Requested;

    public static void Navigate(string page) => Requested?.Invoke(page);
}
