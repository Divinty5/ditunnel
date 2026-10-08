namespace DiTunnel.App;

public enum ReleaseTarget { WindowsX64, AndroidArm64, LinuxX64 }

// Presentation capabilities describe what this build can expose. Actual network
// protection continues to come from IProfileVpnEngine.IsNetworkProtectionActive.
public sealed record AppPlatform(
    ReleaseTarget ReleaseTarget,
    string VersionMetadataKey,
    string RuntimeDescription,
    string ProfileStorageDescription,
    bool SupportsKillSwitch,
    bool SupportsHideToTray,
    bool CaseSensitiveApplicationPaths,
    bool SupportsAdvancedNetworkSettings = true)
{
    public static AppPlatform Windows { get; } = new(ReleaseTarget.WindowsX64, "WindowsVersion",
        "Windows TUN · Xray-core",
        "Профили сохраняются на этом компьютере и защищены вашей учётной записью Windows.", true, true, false);
    public static AppPlatform Android { get; } = new(ReleaseTarget.AndroidArm64, "AndroidVersion",
        "Android VPN · Xray-core",
        "Профили сохраняются только на этом устройстве и защищены Android Keystore.", false, false, false);
    public static AppPlatform Linux { get; } = new(ReleaseTarget.LinuxX64, "LinuxVersion",
        "Linux TUN · Xray-core",
        "Профили зашифрованы на этом компьютере. Ключ хранится в системном хранилище Secret Service.", true, false, true, true);
    public static AppPlatform Detect() => OperatingSystem.IsAndroid() ? Android
        : OperatingSystem.IsLinux() ? Linux : Windows;

    public StringComparer ApplicationPathComparer => CaseSensitiveApplicationPaths
        ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
}
