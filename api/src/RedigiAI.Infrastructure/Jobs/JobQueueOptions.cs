namespace RedigiAI.Infrastructure.Jobs;

public sealed class JobQueueOptions
{
    public const string Section = "Jobs";

    /// <summary>
    /// De quanto em quanto tempo o worker renova a trava (<c>locked_until</c>) do job que está rodando. Uma chamada
    /// à IA pode levar até 10 minutos, o mesmo tempo da trava: sem renovar, outro worker pegaria o mesmo job no
    /// meio e a IA seria chamada duas vezes. Se o worker morrer, a trava vence 10 minutos depois da última renovação.
    /// </summary>
    public TimeSpan Heartbeat { get; set; } = TimeSpan.FromMinutes(2);
}
