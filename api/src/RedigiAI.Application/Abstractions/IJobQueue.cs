namespace RedigiAI.Application.Abstractions;

public interface IJobQueue
{
    /// <summary>
    /// Enfileira um job na transação do caso de uso: ele só existe se a mudança que o disparou for gravada.
    /// </summary>
    /// <param name="payload">Só ids; nunca conteúdo de redação nem dados pessoais.</param>
    void Enqueue(string type, object payload, int maxAttempts);
}

/// <summary>Payload dos jobs que agem sobre uma redação.</summary>
public sealed record EssayJobPayload(Guid EssayId);
