namespace DiTunnel.App;

public static class AppBackNavigation
{
    private static Action? handler;

    public static void Set(Action action) => handler = action;
    public static void Clear() => handler = null;

    public static bool TryGoBack()
    {
        var action = handler;
        if (action is null) return false;
        action();
        return true;
    }
}
