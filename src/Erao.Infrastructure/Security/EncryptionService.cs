using System.Security.Cryptography;
using System.Text;
using Erao.Core.Interfaces;
using Microsoft.Extensions.Configuration;

namespace Erao.Infrastructure.Security;

public class EncryptionService : IEncryptionService
{
    private readonly byte[] _key;
    private readonly byte[] _legacyIv; // Only used for decrypting old data

    public EncryptionService(IConfiguration configuration)
    {
        var encryptionKey = configuration["Encryption:Key"]
            ?? throw new ArgumentNullException("Encryption:Key not configured");

        _key = Convert.FromBase64String(encryptionKey);

        if (_key.Length != 32)
            throw new ArgumentException("Encryption key must be 32 bytes (256 bits)");

        // Legacy IV for backward-compatible decryption of existing data
        var encryptionIv = configuration["Encryption:IV"] ?? "";
        _legacyIv = string.IsNullOrEmpty(encryptionIv) ? new byte[16] : Convert.FromBase64String(encryptionIv);
    }

    public string Encrypt(string plainText)
    {
        if (string.IsNullOrEmpty(plainText))
            return plainText;

        using var aes = Aes.Create();
        aes.Key = _key;
        aes.GenerateIV(); // Random IV per encryption
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var encryptor = aes.CreateEncryptor();
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var encryptedBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

        // Prepend IV to ciphertext: [16-byte IV][ciphertext]
        var result = new byte[aes.IV.Length + encryptedBytes.Length];
        Buffer.BlockCopy(aes.IV, 0, result, 0, aes.IV.Length);
        Buffer.BlockCopy(encryptedBytes, 0, result, aes.IV.Length, encryptedBytes.Length);

        return Convert.ToBase64String(result);
    }

    public string Decrypt(string cipherText)
    {
        if (string.IsNullOrEmpty(cipherText))
            return cipherText;

        var allBytes = Convert.FromBase64String(cipherText);

        // Try new format first: [16-byte IV][ciphertext]
        if (allBytes.Length > 16)
        {
            try
            {
                var iv = new byte[16];
                var encrypted = new byte[allBytes.Length - 16];
                Buffer.BlockCopy(allBytes, 0, iv, 0, 16);
                Buffer.BlockCopy(allBytes, 16, encrypted, 0, encrypted.Length);

                using var aes = Aes.Create();
                aes.Key = _key;
                aes.IV = iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;

                using var decryptor = aes.CreateDecryptor();
                var decryptedBytes = decryptor.TransformFinalBlock(encrypted, 0, encrypted.Length);
                return Encoding.UTF8.GetString(decryptedBytes);
            }
            catch (CryptographicException)
            {
                // Fall through to legacy format
            }
        }

        // Legacy format: static IV, no IV prepended
        {
            using var aes = Aes.Create();
            aes.Key = _key;
            aes.IV = _legacyIv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var decryptor = aes.CreateDecryptor();
            var decryptedBytes = decryptor.TransformFinalBlock(allBytes, 0, allBytes.Length);
            return Encoding.UTF8.GetString(decryptedBytes);
        }
    }

    public static (string Key, string IV) GenerateKeyAndIV()
    {
        using var aes = Aes.Create();
        aes.KeySize = 256;
        aes.GenerateKey();
        aes.GenerateIV();

        return (Convert.ToBase64String(aes.Key), Convert.ToBase64String(aes.IV));
    }
}
