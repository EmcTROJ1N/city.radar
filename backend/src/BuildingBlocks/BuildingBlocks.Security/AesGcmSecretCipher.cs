using System.Security.Cryptography;

namespace BuildingBlocks.Security;

/// <summary>
/// AES-256-GCM. Формат шифртекста:
/// <c>nonce(12) | tag(16) | data</c>; nonce — случайный на каждое шифрование, AAD не используется.
/// GCM одновременно даёт конфиденциальность и целостность: подмена любого байта nonce, tag или
/// data приводит к <see cref="CryptographicException"/> при расшифровке.
/// </summary>
public sealed class AesGcmSecretCipher : ISecretCipher, IDisposable
{
    /// <summary>Размер nonce в байтах (рекомендация NIST SP 800-38D для AES-GCM).</summary>
    public const int NonceSize = 12;

    /// <summary>Размер аутентификационного тега в байтах.</summary>
    public const int TagSize = 16;

    /// <summary>Минимальная длина шифртекста: nonce + tag, без данных.</summary>
    public const int MinimumCiphertextLength = NonceSize + TagSize;

    private readonly MasterKeyProvider _keys;
    private bool _disposed;

    /// <summary>Создаёт шифр поверх набора мастер-ключей.</summary>
    public AesGcmSecretCipher(MasterKeyProvider keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        _keys = keys;
    }

    /// <inheritdoc />
    public int CurrentKeyVersion => _keys.CurrentKeyVersion;

    /// <inheritdoc />
    public byte[] Encrypt(ReadOnlySpan<byte> plaintext, int keyVersion)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var key = _keys.GetKey(keyVersion);

        var ciphertext = new byte[MinimumCiphertextLength + plaintext.Length];
        var nonce = ciphertext.AsSpan(0, NonceSize);
        var tag = ciphertext.AsSpan(NonceSize, TagSize);
        var data = ciphertext.AsSpan(MinimumCiphertextLength);

        // Nonce обязан быть уникальным для каждой операции шифрования одним ключом:
        // повтор nonce с AES-GCM раскрывает открытый текст.
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, data, tag);

        return ciphertext;
    }

    /// <inheritdoc />
    public byte[] Decrypt(ReadOnlySpan<byte> ciphertext, int keyVersion)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (ciphertext.Length < MinimumCiphertextLength)
        {
            throw new ArgumentException(
                $"Шифртекст повреждён: длина {ciphertext.Length} байт меньше минимальной " +
                $"{MinimumCiphertextLength} байт (nonce {NonceSize} + tag {TagSize}).",
                nameof(ciphertext));
        }

        var key = _keys.GetKey(keyVersion);

        var nonce = ciphertext[..NonceSize];
        var tag = ciphertext.Slice(NonceSize, TagSize);
        var data = ciphertext[MinimumCiphertextLength..];

        var plaintext = new byte[data.Length];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, data, tag, plaintext);
        }
        catch
        {
            // Не оставляем в памяти частично расшифрованные данные повреждённого секрета.
            CryptographicOperations.ZeroMemory(plaintext);
            throw;
        }

        return plaintext;
    }

    /// <summary>Освобождает шифр; ключи обнуляются при освобождении <see cref="MasterKeyProvider"/>.</summary>
    public void Dispose()
    {
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
