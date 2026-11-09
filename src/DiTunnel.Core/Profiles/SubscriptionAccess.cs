namespace DiTunnel.Core.Profiles;

public sealed class SubscriptionExhaustedException() : InvalidOperationException(SubscriptionAccess.ExhaustedMessage);

public static class SubscriptionAccess
{
    public const string ExhaustedMessage = "Подписка исчерпана. Обратитесь к сервису, предоставляющему VPN по этой подписке.";
    public static void EnsureAvailable(ImportedProfile profile)
    {
        if (profile.Usage?.IsExhausted(DateTimeOffset.UtcNow) == true)
            throw new SubscriptionExhaustedException();
    }
}
