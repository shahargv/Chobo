namespace ChoboServer.Services;

public sealed class ActorContext
{
    public Guid? UserId { get; set; }
    public string ActorName { get; set; } = "system";

    public static ActorContext System => new() { ActorName = "system" };
}

