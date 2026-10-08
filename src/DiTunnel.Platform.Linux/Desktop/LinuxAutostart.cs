namespace DiTunnel.Platform.Linux.Desktop;

public static class LinuxAutostart
{
    private const string Marker = "# Managed by Di-Tunnel\n";
    public static void SetEnabled(string configHome, bool enabled)
    {
        if (!Path.IsPathFullyQualified(configHome)) throw new ArgumentException("Требуется абсолютный каталог XDG.");
        var directory = Path.Combine(configHome, "autostart");
        var path = Path.Combine(directory, "org.divinty5.DiTunnel.desktop");
        if (new FileInfo(path).LinkTarget is not null) throw new IOException("Файл автозапуска не должен быть ссылкой.");
        if (File.Exists(path) && (new FileInfo(path).Length > 4096 || !File.ReadAllText(path).StartsWith(Marker, StringComparison.Ordinal)))
            throw new IOException("Существующий файл автозапуска не принадлежит Di-Tunnel.");
        if (!enabled) { File.Delete(path); return; }
        Directory.CreateDirectory(directory);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, Marker + "[Desktop Entry]\nType=Application\nName=Di-Tunnel\nExec=/usr/bin/di-tunnel --autostart\nIcon=di-tunnel\nTerminal=false\nX-GNOME-Autostart-enabled=true\n");
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}
