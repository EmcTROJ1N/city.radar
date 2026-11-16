using System.Security.Cryptography;
using System.Text;

using BuildingBlocks.Security;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

using Shouldly;

namespace Api.IntegrationTests.Security;

/// <summary>
/// Шифрование секретов: AES-256-GCM, формат
/// <c>nonce(12) | tag(16) | data</c>, мастер-ключ из файла. Тесты чистые, БД не нужна.
/// </summary>
public sealed class SecretCipherTests
{
    private static readonly Dictionary<string, string?> ClearKeyVariables = new()
    {
        [MasterKeyProvider.KeyFileVariable] = null,
        [MasterKeyProvider.PreviousKeyFileVariable] = null,
        [MasterKeyProvider.KeyVersionVariable] = null,
    };

    private static readonly byte[] TestKey = CreateKeyMaterial(seed: 1);

    [Theory]
    [InlineData("")]
    [InlineData("Вода есть")]
    [InlineData("Донецк: улица Артёма, 15 — воды нет")]
    [InlineData("AES-256-GCM: nonce | tag | data")]
    public void Round_trip_preserves_plaintext(string plaintext)
    {
        using var fixture = CipherFixture.Create();

        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = fixture.Cipher.Encrypt(bytes, fixture.Cipher.CurrentKeyVersion);

        fixture.Cipher.Decrypt(ciphertext, fixture.Cipher.CurrentKeyVersion).ShouldBe(bytes);
    }

    [Fact]
    public void Round_trip_large_plaintext()
    {
        using var fixture = CipherFixture.Create();

        var plaintext = RandomNumberGenerator.GetBytes(1024 * 1024);
        var ciphertext = fixture.Cipher.Encrypt(plaintext, 1);

        ciphertext.Length.ShouldBe(plaintext.Length + AesGcmSecretCipher.MinimumCiphertextLength);
        fixture.Cipher.Decrypt(ciphertext, 1).ShouldBe(plaintext);
    }

    [Fact]
    public void Two_encryptions_of_same_plaintext_differ_by_nonce()
    {
        using var fixture = CipherFixture.Create();

        var first = fixture.Cipher.Encrypt("Вода есть"u8, 1);
        var second = fixture.Cipher.Encrypt("Вода есть"u8, 1);

        first.ShouldNotBe(second);
        first.AsSpan(0, AesGcmSecretCipher.NonceSize)
            .SequenceEqual(second.AsSpan(0, AesGcmSecretCipher.NonceSize))
            .ShouldBeFalse();
    }

    [Theory]
    [InlineData(0)] // первый байт nonce
    [InlineData(AesGcmSecretCipher.NonceSize)] // первый байт tag
    [InlineData(AesGcmSecretCipher.MinimumCiphertextLength)] // первый байт data
    public void Tampered_ciphertext_throws(int index)
    {
        using var fixture = CipherFixture.Create();

        var ciphertext = fixture.Cipher.Encrypt("Вода есть"u8, 1);
        ciphertext[index] ^= 0xFF;

        Should.Throw<CryptographicException>(() => fixture.Cipher.Decrypt(ciphertext, 1));
    }

    [Fact]
    public void Wrong_key_version_throws_on_encrypt()
    {
        using var fixture = CipherFixture.Create();

        var exception = Should.Throw<UnknownKeyVersionException>(
            () => fixture.Cipher.Encrypt("секрет"u8, 42));

        exception.KeyVersion.ShouldBe(42);
        exception.AvailableVersions.ShouldBe([1]);
    }

    [Fact]
    public void Wrong_key_version_throws_on_decrypt()
    {
        using var fixture = CipherFixture.Create();

        var ciphertext = fixture.Cipher.Encrypt("секрет"u8, 1);

        var exception = Should.Throw<UnknownKeyVersionException>(
            () => fixture.Cipher.Decrypt(ciphertext, 42));

        exception.KeyVersion.ShouldBe(42);
        exception.Message.ShouldContain("42");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(AesGcmSecretCipher.NonceSize)]
    [InlineData(AesGcmSecretCipher.MinimumCiphertextLength - 1)]
    public void Too_short_ciphertext_throws(int length)
    {
        using var fixture = CipherFixture.Create();

        Should.Throw<ArgumentException>(() => fixture.Cipher.Decrypt(new byte[length], 1));
    }

    [Fact]
    public void FromFile_reads_master_key()
    {
        using var file = new TempKeyFile(Convert.ToBase64String(TestKey));
        using var keys = MasterKeyProvider.FromFile(file.FilePath);
        using var cipher = new AesGcmSecretCipher(keys);

        keys.CurrentKeyVersion.ShouldBe(1);

        var plaintext = "телеграм-сессия"u8.ToArray();
        cipher.Decrypt(cipher.Encrypt(plaintext, 1), 1).ShouldBe(plaintext);
    }

    [Fact]
    public void FromFile_trims_surrounding_whitespace()
    {
        using var file = new TempKeyFile($"\n  {Convert.ToBase64String(TestKey)}  \r\n");

        using var keys = MasterKeyProvider.FromFile(file.FilePath);

        keys.CurrentKeyVersion.ShouldBe(1);
    }

    [Fact]
    public void FromFile_missing_file_throws()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mayak-missing-key-{Guid.NewGuid():N}");

        var exception = Should.Throw<InvalidOperationException>(() => MasterKeyProvider.FromFile(path));

        exception.Message.ShouldContain("не найден");
    }

    [Fact]
    public void FromFile_wrong_key_length_throws()
    {
        using var file = new TempKeyFile(Convert.ToBase64String(new byte[16]));

        var exception = Should.Throw<InvalidOperationException>(() => MasterKeyProvider.FromFile(file.FilePath));

        exception.Message.ShouldContain("16");
        exception.Message.ShouldContain(MasterKeyProvider.KeySize.ToString());
    }

    [Fact]
    public void FromFile_rejects_raw_bytes_instead_of_base64()
    {
        // Правило однозначно: в файле base64 32 байт, а не сырые 32 байта.
        using var file = new TempKeyFile(CreateKeyMaterial(seed: 9));

        var exception = Should.Throw<InvalidOperationException>(() => MasterKeyProvider.FromFile(file.FilePath));

        exception.Message.ShouldContain("base64");
    }

    [Fact]
    public void FromFile_rejects_empty_file()
    {
        using var file = new TempKeyFile(" \n\t ");

        var exception = Should.Throw<InvalidOperationException>(() => MasterKeyProvider.FromFile(file.FilePath));

        exception.Message.ShouldContain("пустое");
    }

    [Fact]
    public void FromEnvironment_without_variable_throws()
    {
        WithEnvironmentVariables(ClearKeyVariables, () =>
        {
            var exception = Should.Throw<InvalidOperationException>(() => MasterKeyProvider.FromEnvironment());

            exception.Message.ShouldContain(MasterKeyProvider.KeyFileVariable);
        });
    }

    [Fact]
    public void FromEnvironment_reads_current_and_previous_keys()
    {
        using var current = new TempKeyFile(Convert.ToBase64String(TestKey));
        using var previous = new TempKeyFile(Convert.ToBase64String(CreateKeyMaterial(seed: 2)));

        WithEnvironmentVariables(
            new Dictionary<string, string?>
            {
                [MasterKeyProvider.KeyFileVariable] = current.FilePath,
                [MasterKeyProvider.PreviousKeyFileVariable] = previous.FilePath,
            },
            () =>
            {
                using var keys = MasterKeyProvider.FromEnvironment();
                using var cipher = new AesGcmSecretCipher(keys);

                // Текущая версия — максимальная; предыдущая (0) нужна для расшифровки старых секретов.
                keys.CurrentKeyVersion.ShouldBe(1);

                var oldCiphertext = cipher.Encrypt("старый секрет"u8, 0);
                cipher.Decrypt(oldCiphertext, 0).ShouldBe("старый секрет"u8.ToArray());
                cipher.Decrypt(cipher.Encrypt("новый секрет"u8, 1), 1)
                    .ShouldBe("новый секрет"u8.ToArray());
            });
    }

    [Fact]
    public void FromEnvironment_rotation_reads_old_key_and_writes_new_version()
    {
        using var current = new TempKeyFile(Convert.ToBase64String(CreateKeyMaterial(seed: 11)));
        using var previous = new TempKeyFile(Convert.ToBase64String(TestKey));

        WithEnvironmentVariables(
            new Dictionary<string, string?>
            {
                [MasterKeyProvider.KeyFileVariable] = current.FilePath,
                [MasterKeyProvider.PreviousKeyFileVariable] = previous.FilePath,
                [MasterKeyProvider.KeyVersionVariable] = "2",
            },
            () =>
            {
                using var keys = MasterKeyProvider.FromEnvironment();
                using var cipher = new AesGcmSecretCipher(keys);

                keys.CurrentKeyVersion.ShouldBe(2);

                // Секрет, записанный до ротации (версия 1), читается предыдущим ключом.
                using var oldKeys = MasterKeyProvider.FromFile(previous.FilePath, keyVersion: 1);
                using var oldCipher = new AesGcmSecretCipher(oldKeys);
                var legacy = oldCipher.Encrypt("старый секрет"u8, 1);

                cipher.Decrypt(legacy, 1).ShouldBe("старый секрет"u8.ToArray());

                // Новые секреты шифруются текущей версией.
                cipher.Decrypt(cipher.Encrypt("новый секрет"u8, 2), 2)
                    .ShouldBe("новый секрет"u8.ToArray());
            });
    }

    [Theory]
    [InlineData("0")]
    [InlineData("не число")]
    public void Invalid_key_version_throws(string version)
    {
        using var current = new TempKeyFile(Convert.ToBase64String(TestKey));

        Should.Throw<InvalidOperationException>(() =>
            WithEnvironmentVariables(
                new Dictionary<string, string?>
                {
                    [MasterKeyProvider.KeyFileVariable] = current.FilePath,
                    [MasterKeyProvider.PreviousKeyFileVariable] = null,
                    [MasterKeyProvider.KeyVersionVariable] = version,
                },
                () => MasterKeyProvider.FromEnvironment()))
            .Message.ShouldContain(MasterKeyProvider.KeyVersionVariable);
    }

    [Fact]
    public void Production_without_master_key_fails_at_registration()
    {
        WithEnvironmentVariables(ClearKeyVariables, () =>
        {
            var services = new ServiceCollection();

            var exception = Should.Throw<InvalidOperationException>(
                () => services.AddSecretCipher(new FakeHostEnvironment("Production")));

            exception.Message.ShouldContain(MasterKeyProvider.KeyFileVariable);
        });
    }

    [Fact]
    public void Production_with_master_key_file_registers_cipher()
    {
        using var file = new TempKeyFile(Convert.ToBase64String(TestKey));

        WithEnvironmentVariables(
            new Dictionary<string, string?>
            {
                [MasterKeyProvider.KeyFileVariable] = file.FilePath,
                [MasterKeyProvider.PreviousKeyFileVariable] = null,
            },
            () =>
            {
                var services = new ServiceCollection();
                services.AddSecretCipher(new FakeHostEnvironment("Production"));

                using var provider = services.BuildServiceProvider();
                var cipher = provider.GetRequiredService<ISecretCipher>();

                cipher.CurrentKeyVersion.ShouldBe(1);
                var plaintext = "секрет"u8.ToArray();
                cipher.Decrypt(cipher.Encrypt(plaintext, 1), 1).ShouldBe(plaintext);
            });
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void Non_production_without_master_key_generates_process_key(string environmentName)
    {
        WithEnvironmentVariables(ClearKeyVariables, () =>
        {
            var services = new ServiceCollection();
            services.AddSecretCipher(new FakeHostEnvironment(environmentName));

            using var provider = services.BuildServiceProvider();
            var cipher = provider.GetRequiredService<ISecretCipher>();

            cipher.CurrentKeyVersion.ShouldBe(1);
            var plaintext = "секрет"u8.ToArray();
            cipher.Decrypt(cipher.Encrypt(plaintext, 1), 1).ShouldBe(plaintext);
        });
    }

    [Fact]
    public void Non_production_uses_security_master_key_from_configuration()
    {
        var configuredKey = Convert.ToBase64String(CreateKeyMaterial(seed: 7));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [MasterKeyProvider.ConfigurationKey] = configuredKey,
            })
            .Build();

        WithEnvironmentVariables(ClearKeyVariables, () =>
        {
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddSecretCipher(new FakeHostEnvironment("Development"));

            using var provider = services.BuildServiceProvider();
            var cipher = provider.GetRequiredService<ISecretCipher>();

            using var expectedKeys = MasterKeyProvider.FromBase64(configuredKey);
            using var expectedCipher = new AesGcmSecretCipher(expectedKeys);

            // Секрет, зашифрованный настроенным ключом, читается независимым провайдером.
            var ciphertext = cipher.Encrypt("секрет"u8, 1);
            expectedCipher.Decrypt(ciphertext, 1).ShouldBe("секрет"u8.ToArray());
        });
    }

    private static byte[] CreateKeyMaterial(int seed) =>
        Enumerable.Range(seed, MasterKeyProvider.KeySize).Select(value => (byte)value).ToArray();

    private static void WithEnvironmentVariables(
        IReadOnlyDictionary<string, string?> values,
        Action action)
    {
        var previous = values.Keys.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var (name, value) in values)
            {
                Environment.SetEnvironmentVariable(name, value);
            }

            action();
        }
        finally
        {
            foreach (var (name, value) in previous)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    private sealed class CipherFixture : IDisposable
    {
        private CipherFixture(MasterKeyProvider keys, AesGcmSecretCipher cipher)
        {
            Keys = keys;
            Cipher = cipher;
        }

        public MasterKeyProvider Keys { get; }

        public AesGcmSecretCipher Cipher { get; }

        public static CipherFixture Create()
        {
            var keys = MasterKeyProvider.FromBase64(Convert.ToBase64String(TestKey));
            return new CipherFixture(keys, new AesGcmSecretCipher(keys));
        }

        public void Dispose()
        {
            Cipher.Dispose();
            Keys.Dispose();
        }
    }

    private sealed class TempKeyFile : IDisposable
    {
        public TempKeyFile(string content)
            : this(Encoding.UTF8.GetBytes(content))
        {
        }

        public TempKeyFile(byte[] content)
        {
            FilePath = Path.Combine(Path.GetTempPath(), $"mayak-key-{Guid.NewGuid():N}");
            File.WriteAllBytes(FilePath, content);
        }

        public string FilePath { get; }

        public void Dispose() => File.Delete(FilePath);
    }

    private sealed class FakeHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "Api.IntegrationTests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
