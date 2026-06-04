using System.Security.Cryptography;
using System.Text;

namespace SpiceClient.Crypto;

/// <summary>
/// SPICE ticket (password) encryption. The server sends its RSA public key as a
/// DER-encoded SubjectPublicKeyInfo block in the link reply; the client returns
/// the password (NUL-terminated) encrypted with RSA OAEP-SHA1.
///
/// spice-html5 hand-rolls OAEP padding (ticket.js); .NET provides it natively, so
/// this is just ImportSubjectPublicKeyInfo + Encrypt(OaepSHA1).
/// </summary>
public static class SpiceTicket
{
    /// <param name="pubKeyDer">The 162-byte SubjectPublicKeyInfo block from the link reply.</param>
    /// <param name="password">The SPICE password (usually empty).</param>
    /// <returns>The RSA ciphertext (128 bytes for a 1024-bit key).</returns>
    public static byte[] EncryptPassword(byte[] pubKeyDer, string password)
    {
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(pubKeyDer, out _);

        var pw = Encoding.UTF8.GetBytes(password ?? string.Empty);
        var plain = new byte[pw.Length + 1]; // trailing NUL terminator (already zero)
        Array.Copy(pw, plain, pw.Length);

        return rsa.Encrypt(plain, RSAEncryptionPadding.OaepSHA1);
    }
}
