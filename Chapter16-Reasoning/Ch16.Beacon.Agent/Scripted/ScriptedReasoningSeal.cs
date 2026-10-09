using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Ch16.Beacon.Agent.Scripted;

/// <summary>
/// The scripted model's stand-in for a provider's reasoning encryption. A provider that keeps its chain of
/// thought private encrypts it under a key only it holds and hands the client an opaque artifact to return on
/// the next turn. This does the same with AES-GCM and a key made when the process starts, so a value is
/// unreadable to everyone but the scripted model, and any change to it fails verification.
/// </summary>
/// <remarks>Nothing else in the agent calls <see cref="Open"/>: the agent passes the value on without reading it.</remarks>
public sealed class ScriptedReasoningSeal
{
    private const string Prefix = "bx1.";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

    /// <summary>Encrypts a chain of reasoning into an opaque value.</summary>
    public string Seal(string reasoning)
    {
        var plaintext = Encoding.UTF8.GetBytes(reasoning);
        var sealedBytes = new byte[NonceSize + plaintext.Length + TagSize];
        var nonce = sealedBytes.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plaintext, sealedBytes.AsSpan(NonceSize, plaintext.Length), sealedBytes.AsSpan(NonceSize + plaintext.Length));
        return Prefix + Base64Url.EncodeToString(sealedBytes);
    }

    /// <summary>Decrypts a value this seal produced; <see langword="null"/> if it was altered or isn't one of ours.</summary>
    public string? Open(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        byte[] sealedBytes;
        try
        {
            sealedBytes = Base64Url.DecodeFromChars(value.AsSpan(Prefix.Length));
        }
        catch (FormatException)
        {
            return null;
        }

        if (sealedBytes.Length < NonceSize + TagSize)
        {
            return null;
        }

        var length = sealedBytes.Length - NonceSize - TagSize;
        var plaintext = new byte[length];
        try
        {
            using var aes = new AesGcm(_key, TagSize);
            aes.Decrypt(sealedBytes.AsSpan(0, NonceSize), sealedBytes.AsSpan(NonceSize, length), sealedBytes.AsSpan(NonceSize + length), plaintext);
        }
        catch (AuthenticationTagMismatchException)
        {
            return null;
        }

        return Encoding.UTF8.GetString(plaintext);
    }
}
