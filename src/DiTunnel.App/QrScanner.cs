namespace DiTunnel.App;

public static class QrScanner
{
    public static Func<Task<string?>> ScanAsync { get; set; } =
        () => Task.FromResult<string?>(null);
}
