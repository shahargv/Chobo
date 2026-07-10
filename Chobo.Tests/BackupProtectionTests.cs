using System.Text.Json;
using Chobo.Contracts;
using ChoboServer.Options;
using ChoboServer.Repositories;
using ChoboServer.Services;
using Microsoft.Extensions.Options;

namespace Chobo.Tests;

public sealed class BackupProtectionTests
{
    [Fact]
    public void Generated_passwords_have_the_required_length_alphabet_and_per_shard_uniqueness()
    {
        var generator = new BackupPasswordGenerator();
        var passwords = Enumerable.Range(0, 256).Select(_ => generator.Generate()).ToArray();

        Assert.All(passwords, password =>
        {
            Assert.Equal(20, password.Length);
            Assert.All(password, character => Assert.Contains(character, BackupPasswordGenerator.Alphabet));
        });
        Assert.Equal(passwords.Length, passwords.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(BackupCompressionMethod.Store, "store")]
    [InlineData(BackupCompressionMethod.Deflate, "deflate")]
    [InlineData(BackupCompressionMethod.Bzip2, "bzip2")]
    [InlineData(BackupCompressionMethod.Lzma, "lzma")]
    [InlineData(BackupCompressionMethod.Zstd, "zstd")]
    [InlineData(BackupCompressionMethod.Xz, "xz")]
    public void Policy_compression_maps_to_clickhouse_and_requires_zip(BackupCompressionMethod method, string expected)
    {
        var settings = ClickHouseAdvancedSettings.WithPolicyCompression(ClickHouseAdvancedSettings.Empty, method, 3);

        Assert.Equal(expected, settings["compression_method"].GetString());
        Assert.Equal(3, settings["compression_level"].GetInt32());
        Assert.Equal(method, ClickHouseAdvancedSettings.CompressionMethod(settings));
        Assert.Equal(3, ClickHouseAdvancedSettings.CompressionLevel(settings));
        Assert.True(ClickHouseAdvancedSettings.RequiresZipArchive(settings));
    }

    [Fact]
    public void No_policy_compression_preserves_directory_backup_behavior()
    {
        var settings = ClickHouseAdvancedSettings.WithPolicyCompression(ClickHouseAdvancedSettings.Empty, null, null);

        Assert.Null(ClickHouseAdvancedSettings.CompressionMethod(settings));
        Assert.Null(ClickHouseAdvancedSettings.CompressionLevel(settings));
        Assert.False(ClickHouseAdvancedSettings.RequiresZipArchive(settings));
    }

    [Theory]
    [InlineData("password")]
    [InlineData("use_same_password_for_base_backup")]
    public void Password_execution_settings_are_chobo_managed(string name)
    {
        using var document = JsonDocument.Parse($"{{\"{name}\":true}}");
        var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(document.RootElement.GetRawText())!;

        Assert.Throws<ArgumentException>(() => ClickHouseAdvancedSettings.Normalize(values, ClickHouseAdvancedSettingsKind.Backup));
    }

    [Fact]
    public void Incremental_restore_reuses_the_password_for_its_base_backup()
    {
        var backupTable = new ChoboServer.Data.BackupTableEntity { Database = "sales", Table = "orders" };
        var restoreShard = new ChoboServer.Data.RestoreTableShardEntity { RestoreDatabase = "sales", RestoreTableName = "orders" };
        var destination = new ClickHouseStorageDestination("S3('bucket/path')", [], [], [], []);

        var sql = ClickHouseRestoreSqlBuilder.Build(backupTable, restoreShard, destination, false, false, ClickHouseAdvancedSettings.Empty, "password", true);

        Assert.Contains("password = 'password'", sql, StringComparison.Ordinal);
        Assert.Contains("use_same_password_for_base_backup = 1", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Aes_key_cache_observes_delete_invalid_and_restore_without_restart()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "chobo-aes-cache-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDirectory);
        try
        {
            using var repository = new FileAesKeyRepository(
                Options.Create(new ChoboStorageOptions { DataDirectory = dataDirectory }),
                Options.Create(new ChoboSecurityOptions()));
            var material = await repository.CreateNewAsync();
            var keyPath = Path.Combine(dataDirectory, "secrets", "aes-keys", material.KeyId.ToString());
            var keyText = await File.ReadAllTextAsync(keyPath);

            Assert.Equal(AesKeyAvailability.Available, await repository.GetAvailabilityAsync(material.KeyId));
            Assert.Same(material, await repository.GetKeyByIdAsync(material.KeyId));

            await File.WriteAllTextAsync(keyPath, "not-a-valid-key");
            await EventuallyAsync(async () => await repository.GetAvailabilityAsync(material.KeyId) == AesKeyAvailability.Invalid);

            File.Delete(keyPath);
            await EventuallyAsync(async () => await repository.GetAvailabilityAsync(material.KeyId) == AesKeyAvailability.Missing);

            await File.WriteAllTextAsync(keyPath, keyText);
            await EventuallyAsync(async () => await repository.GetAvailabilityAsync(material.KeyId) == AesKeyAvailability.Available);
            Assert.Equal(material.KeyBytes, (await repository.GetKeyByIdAsync(material.KeyId))!.KeyBytes);
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Malformed_ciphertext_is_rejected_before_restore_can_use_it()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "chobo-aes-malformed-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDirectory);
        try
        {
            using var repository = new FileAesKeyRepository(
                Options.Create(new ChoboStorageOptions { DataDirectory = dataDirectory }),
                Options.Create(new ChoboSecurityOptions()));
            var material = await repository.CreateNewAsync();
            var protector = new CredentialProtector(repository);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                protector.DecryptAsync("not-a-ciphertext", material.KeyId));
            await Assert.ThrowsAsync<FormatException>(() =>
                protector.DecryptAsync("%%% .%%% .%%%".Replace(" ", string.Empty), material.KeyId));
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    private static async Task EventuallyAsync(Func<Task<bool>> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (await predicate())
            {
                return;
            }
            await Task.Delay(25);
        }
        Assert.True(await predicate(), "The AES key cache did not observe the filesystem change in time.");
    }
}
