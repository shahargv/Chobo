using System.Security.Cryptography;

namespace ChoboServer.Services;

public interface IBackupPasswordGenerator
{
    string Generate();
}

public sealed class BackupPasswordGenerator : IBackupPasswordGenerator
{
    public const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789abcdefghijklmnopqrstuvwxyz!@#$%^&*()";
    public string Generate() => new(Enumerable.Range(0, 20).Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]).ToArray());
}
