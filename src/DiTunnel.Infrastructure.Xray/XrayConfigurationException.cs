namespace DiTunnel.Infrastructure.Xray;

public sealed class XrayConfigurationException : Exception
{
    public XrayConfigurationException(XrayValidationResult validationResult)
        : base("Xray-core отклонил конфигурацию.")
    {
        ValidationResult = validationResult;
    }

    public XrayValidationResult ValidationResult { get; }
}
