using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scribe.Core.Security;

/// <summary>
/// JSON converter that keeps a string encrypted at rest with Windows DPAPI (current-user scope) while
/// exposing the plaintext in memory. Used for the optional Azure API key so the settings store never
/// holds it in the clear. Decryption failures (e.g. a settings file copied from another user or
/// machine) return <see langword="null"/> instead of throwing, so a stale value simply prompts
/// re-entry rather than bricking settings load.
/// </summary>
public sealed class DpapiProtectedStringConverter : JsonConverter<string?>
{
    // Extra entropy ties the ciphertext to this specific use, so a blob can't be unprotected by
    // another app running under the same user account.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Scribe.AzureApiKey.v1");

    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        var protectedValue = reader.GetString();
        if (string.IsNullOrEmpty(protectedValue))
        {
            return null;
        }

        try
        {
            var cipher = Convert.FromBase64String(protectedValue);
            var plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (string.IsNullOrEmpty(value))
        {
            writer.WriteNullValue();
            return;
        }

        var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
        writer.WriteStringValue(Convert.ToBase64String(cipher));
    }
}
