using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StatusBar.Core.Sync;

/// <summary>Authenticated, signed per-recipient snapshots; only public keys are shared through Drive.</summary>
public static class LiveTaskEncryption
{
    sealed record Envelope(int Version, string Sender, string Recipient, string Nonce, string WrappedKey,
        string Payload, string Tag, string Signature);

    /// <summary>Encrypts display state for one paired device and authenticates its publisher.</summary>
    public static byte[] Seal(LiveTaskSnapshot snapshot, string recipientId, RSA sender, RSA recipient)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(12);
            var plaintext = JsonSerializer.SerializeToUtf8Bytes(snapshot);
            if (plaintext.Length > LiveTaskExchange.MaximumBytes) throw new InvalidDataException("ExchangeTooLarge");
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[16];
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, Binding(snapshot.DeviceId, recipientId));
            var envelope = new Envelope(1, snapshot.DeviceId, recipientId, Convert.ToBase64String(nonce),
                Convert.ToBase64String(recipient.Encrypt(key, RSAEncryptionPadding.OaepSHA256)),
                Convert.ToBase64String(ciphertext), Convert.ToBase64String(tag), "");
            var signature = sender.SignData(JsonSerializer.SerializeToUtf8Bytes(envelope), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
            return JsonSerializer.SerializeToUtf8Bytes(envelope with { Signature = Convert.ToBase64String(signature) });
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    /// <summary>Checks the pinned publisher key and intended recipient before releasing any text.</summary>
    public static LiveTaskSnapshot Open(byte[] bytes, string senderId, string recipientId, RSA sender, RSA recipient)
    {
        if (bytes.Length > LiveTaskExchange.MaximumBytes * 2) throw new InvalidDataException("ExchangeTooLarge");
        var envelope = JsonSerializer.Deserialize<Envelope>(bytes, new JsonSerializerOptions { MaxDepth = 8 });
        if (envelope is null || envelope.Version != 1 || envelope.Sender != senderId || envelope.Recipient != recipientId)
            throw new InvalidDataException("ExchangeIdentityMismatch");
        if (!sender.VerifyData(JsonSerializer.SerializeToUtf8Bytes(envelope with { Signature = "" }),
                Convert.FromBase64String(envelope.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new CryptographicException("ExchangeSignatureInvalid");
        var key = recipient.Decrypt(Convert.FromBase64String(envelope.WrappedKey), RSAEncryptionPadding.OaepSHA256);
        try
        {
            var ciphertext = Convert.FromBase64String(envelope.Payload);
            var plaintext = new byte[ciphertext.Length];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(Convert.FromBase64String(envelope.Nonce), ciphertext, Convert.FromBase64String(envelope.Tag),
                plaintext, Binding(senderId, recipientId));
            var snapshot = LiveTaskExchange.Read(plaintext);
            if (snapshot.DeviceId != senderId) throw new InvalidDataException("ExchangeIdentityMismatch");
            return snapshot;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    static byte[] Binding(string sender, string recipient) => Encoding.UTF8.GetBytes("status-bar-v1|" + sender + "|" + recipient);
}
