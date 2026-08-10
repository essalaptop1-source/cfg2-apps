namespace Configuration2App.Services;

public enum ToastType
{
    Success,
    Info,
    Error,
}

public static class ToastService
{
    public static event Action<ToastType, string, string?>? ToastRequested;

    public static void Show(ToastType type, string message, string? title = null)
    {
        ToastRequested?.Invoke(type, message, title);
    }

    public static void Success(string message, string? title = null) => Show(ToastType.Success, message, title);
    public static void Info(string message, string? title = null) => Show(ToastType.Info, message, title);
    public static void Error(string message, string? title = null) => Show(ToastType.Error, message, title);
}
