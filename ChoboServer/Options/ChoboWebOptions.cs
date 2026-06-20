namespace ChoboServer.Options;

public sealed class ChoboWebOptions
{
    public bool IsGuiEnabled { get; set; } = true;
    public int? GuiPort { get; set; }
}
