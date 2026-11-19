// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Security.Cryptography;
using System.Text;

namespace ClassroomManager.Shared;

/// <summary>Encrypts a file's content with a key derived from a password (PBKDF2-SHA256, AES-256-GCM).</summary>
public static class PasswordCipher
{
    const int Iterations = 600_000;

    public sealed record Sealed(string Salt, string Nonce, string Tag, string Data);

    public static Sealed Encrypt(byte[] plain, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(DeriveKey(password, salt), tag.Length))
            aes.Encrypt(nonce, plain, cipher, tag);
        return new Sealed(Convert.ToBase64String(salt), Convert.ToBase64String(nonce), Convert.ToBase64String(tag), Convert.ToBase64String(cipher));
    }

    /// <summary>Throws <see cref="CryptographicException"/>, <see cref="FormatException"/> or <see cref="ArgumentException"/> for a wrong password or a changed file.</summary>
    public static byte[] Decrypt(Sealed content, string password)
    {
        var salt = Convert.FromBase64String(content.Salt ?? "");
        var nonce = Convert.FromBase64String(content.Nonce ?? "");
        var tag = Convert.FromBase64String(content.Tag ?? "");
        var cipher = Convert.FromBase64String(content.Data ?? "");
        var plain = new byte[cipher.Length];
        using (var aes = new AesGcm(DeriveKey(password, salt), tag.Length))
            aes.Decrypt(nonce, cipher, tag, plain);
        return plain;
    }

    static byte[] DeriveKey(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, 32);
}
