using System.Text.RegularExpressions;

namespace HyperMemory.Core;

public static partial class SensitiveDataRedactor
{
    public static (string Value, int Redactions) Redact(string value)
    {
        if (string.IsNullOrEmpty(value)) return (value, 0);
        var redacted = value;
        var count = 0;
        (redacted, count) = Replace(PrivateKeyRegex(), redacted, "[REDACTED PRIVATE KEY]", count);
        (redacted, count) = Replace(BearerRegex(), redacted, "$1[REDACTED]", count);
        (redacted, count) = Replace(KnownTokenRegex(), redacted, "[REDACTED TOKEN]", count);
        (redacted, count) = Replace(JwtRegex(), redacted, "[REDACTED JWT]", count);
        var assigned = SecretAssignmentRegex().Replace(redacted, match =>
        {
            count++;
            return match.Groups[1].Value + match.Groups[2].Value + "[REDACTED]";
        });
        redacted = assigned;
        (redacted, count) = Replace(UriCredentialsRegex(), redacted, "$1[REDACTED]@", count);
        redacted = PaymentCardCandidateRegex().Replace(redacted, match =>
        {
            var digits = NonDigitRegex().Replace(match.Value, string.Empty);
            if (!IsPaymentCard(digits)) return match.Value;
            count++;
            return "[REDACTED PAYMENT CARD]";
        });
        return (redacted, count);
    }

    private static (string Value, int Count) Replace(Regex pattern, string value, string replacement, int count)
    {
        var result = pattern.Replace(value, replacement);
        return (result, count + pattern.Count(value));
    }

    private static bool IsPaymentCard(string digits)
    {
        if (digits.Length is < 13 or > 19) return false;
        var sum = 0;
        var parity = digits.Length % 2;
        for (var index = 0; index < digits.Length; index++)
        {
            var number = digits[index] - '0';
            if (index % 2 == parity)
            {
                number *= 2;
                if (number > 9) number -= 9;
            }
            sum += number;
        }
        return sum % 10 == 0;
    }

    [GeneratedRegex("(?is)-----BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----.*?-----END (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----", RegexOptions.CultureInvariant)]
    private static partial Regex PrivateKeyRegex();
    [GeneratedRegex("(?i)(\\bAuthorization\\s*:\\s*Bearer\\s+)[^\\s,;]+", RegexOptions.CultureInvariant)]
    private static partial Regex BearerRegex();
    [GeneratedRegex("(?i)\\b(?:sk-[A-Za-z0-9_-]{16,}|ghp_[A-Za-z0-9]{16,}|github_pat_[A-Za-z0-9_]{16,}|xox[baprs]-[A-Za-z0-9-]{10,})\\b", RegexOptions.CultureInvariant)]
    private static partial Regex KnownTokenRegex();
    [GeneratedRegex("\\beyJ[A-Za-z0-9_-]{8,}\\.[A-Za-z0-9_-]{8,}\\.[A-Za-z0-9_-]{8,}\\b", RegexOptions.CultureInvariant)]
    private static partial Regex JwtRegex();
    [GeneratedRegex("(?i)\\b(password|passwd|contrase(?:ñ|n)a|api[ _-]?key|access[ _-]?token|auth[ _-]?token|secret)\\b(\\s*[:=]\\s*)([^\\s,;]+)", RegexOptions.CultureInvariant)]
    private static partial Regex SecretAssignmentRegex();
    [GeneratedRegex("(?i)(https?://)([^/@\\s:]+):([^/@\\s]+)@", RegexOptions.CultureInvariant)]
    private static partial Regex UriCredentialsRegex();
    [GeneratedRegex("(?<!\\d)(?:\\d[ -]?){12,18}\\d(?!\\d)", RegexOptions.CultureInvariant)]
    private static partial Regex PaymentCardCandidateRegex();
    [GeneratedRegex("\\D", RegexOptions.CultureInvariant)]
    private static partial Regex NonDigitRegex();
}
