namespace ChoboServer.Services;

public interface IActorContext
{
    Guid? UserId { get; set; }
    string ActorName { get; set; }
}

public sealed class ActorContext : IActorContext
{
    public Guid? UserId { get; set; }
    public string ActorName { get; set; } = "system";

    public static ActorContext System => new() { ActorName = "system" };
}

