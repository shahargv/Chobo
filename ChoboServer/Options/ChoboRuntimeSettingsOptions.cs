namespace ChoboServer.Options;

public sealed class ChoboRuntimeSettingsOptions
{
    public List<string> HiddenKeys { get; set; } =
    [
        "Chobo:EncryptionKeyBase64",
        "Chobo:Init:AdminUser",
        "Chobo:Init:AccessToken",
        "Chobo:Settings:HiddenKeys"
    ];
}