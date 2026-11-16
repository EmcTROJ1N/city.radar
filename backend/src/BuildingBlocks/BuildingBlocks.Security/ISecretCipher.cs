namespace BuildingBlocks.Security;

/// <summary>
/// Симметричное шифрование секретов источников.
/// Шифртекст хранится в БД (<c>secrets.ciphertext</c>) и наружу не отдаётся никогда:
/// API возвращает только вид, название и признак наличия значения.
/// </summary>
public interface ISecretCipher
{
    /// <summary>Версия ключа, которой шифруются новые секреты (<c>secrets.key_version</c>).</summary>
    int CurrentKeyVersion { get; }

    /// <summary>
    /// Шифрует <paramref name="plaintext"/> ключом версии <paramref name="keyVersion"/>.
    /// </summary>
    /// <exception cref="UnknownKeyVersionException">Версия ключа не загружена.</exception>
    byte[] Encrypt(ReadOnlySpan<byte> plaintext, int keyVersion);

    /// <summary>
    /// Расшифровывает <paramref name="ciphertext"/> ключом версии <paramref name="keyVersion"/>.
    /// </summary>
    /// <exception cref="UnknownKeyVersionException">Версия ключа не загружена.</exception>
    /// <exception cref="ArgumentException">Шифртекст короче nonce + tag.</exception>
    /// <exception cref="System.Security.Cryptography.CryptographicException">
    /// Шифртекст повреждён или ключ не тот.
    /// </exception>
    byte[] Decrypt(ReadOnlySpan<byte> ciphertext, int keyVersion);
}
