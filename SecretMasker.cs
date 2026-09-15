using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace RunCommandsService
{
    public static class SecretMasker
    {
        private static readonly HashSet<string> DefaultSecretPlaceholders = new(StringComparer.OrdinalIgnoreCase)
        {
            "CHANGE-ME",
            "CHANGE_ME",
            "put-a-strong-random-key-here",
            "admin",
            "password",
            "123456",
            "12345678",
            "<token-if-needed>",
            "Bearer <token-if-needed>",
            "user@example.com"
        };

        /// <summary>
        /// Check if a given secret is a known default or placeholder value that should be changed in production.
        /// </summary>
        public static bool IsDefaultSecret(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return true;
            return DefaultSecretPlaceholders.Contains(value.Trim());
        }

        /// <summary>
        /// Masks a secret string (e.g. password or API key) for safe logging and API display.
        /// </summary>
        public static string Mask(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "(none)";
            if (value.Length <= 4) return "***";
            return value.Substring(0, 2) + "***" + value.Substring(value.Length - 2);
        }

        /// <summary>
        /// Masks a Webhook URL by obscuring sensitive tokens or path segments.
        /// </summary>
        public static string MaskUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "(none)";
            try
            {
                var uri = new Uri(url);
                var builder = new UriBuilder(uri);
                if (!string.IsNullOrEmpty(uri.Query))
                {
                    builder.Query = "redacted=true";
                }
                var pathSegments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (pathSegments.Length > 1)
                {
                    // Mask last path segment (often contains webhook token)
                    pathSegments[pathSegments.Length - 1] = "***";
                    builder.Path = string.Join("/", pathSegments);
                }
                return builder.ToString();
            }
            catch
            {
                return "***MASKED_URL***";
            }
        }

        /// <summary>
        /// Compares two secrets in fixed-time to prevent timing side-channel attacks.
        /// </summary>
        public static bool FixedTimeEquals(string? a, string? b)
        {
            if (a == null || b == null) return false;
            var aBytes = Encoding.UTF8.GetBytes(a);
            var bBytes = Encoding.UTF8.GetBytes(b);
            return CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
        }
    }
}
