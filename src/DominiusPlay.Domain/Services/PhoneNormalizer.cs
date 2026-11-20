using System.Text;

namespace DominiusPlay.Domain.Services;

public readonly record struct PhoneResult(bool IsValid, string Normalized, string? Error);

/// <summary>
/// Normaliza telefones para o formato usado pelo WhatsApp/Evolution API: apenas dígitos, com DDI.
/// Foco no Brasil (DDI 55): valida DDD, adiciona DDI quando ausente e acrescenta o nono dígito
/// em celulares antigos de 8 dígitos. Números internacionais (+DDI ≠ 55) são aceitos se tiverem 8–15 dígitos.
/// </summary>
public static class PhoneNormalizer
{
    private static readonly HashSet<int> ValidDdds = new()
    {
        11,12,13,14,15,16,17,18,19, 21,22,24, 27,28, 31,32,33,34,35,37,38,
        41,42,43,44,45,46, 47,48,49, 51,53,54,55, 61, 62,64, 63, 65,66, 67, 68, 69,
        71,73,74,75,77, 79, 81,87, 82, 83, 84, 85,88, 86,89, 91,93,94, 92,97, 95, 96, 98,99,
    };

    public static PhoneResult Normalize(string? raw, string defaultCountry = "55")
    {
        if (string.IsNullOrWhiteSpace(raw)) return new(false, "", "Telefone vazio");
        var trimmed = raw.Trim();
        // remove sufixo de JID do WhatsApp
        var at = trimmed.IndexOf('@');
        if (at > 0) trimmed = trimmed[..at];
        var hasPlus = trimmed.StartsWith('+');
        var sb = new StringBuilder();
        foreach (var c in trimmed) if (char.IsAsciiDigit(c)) sb.Append(c);
        var digits = sb.ToString();
        if (digits.StartsWith("00")) { digits = digits[2..]; hasPlus = true; }
        if (digits.Length == 0) return new(false, "", "Telefone sem dígitos");

        // Número internacional explícito (não Brasil)
        if (hasPlus && !digits.StartsWith("55"))
            return digits.Length is >= 8 and <= 15 ? new(true, digits, null) : new(false, digits, "Número internacional com tamanho inválido");

        // Remove zero de discagem (0XX) e código de operadora (0 + 2 dígitos operadora + DDD) — ex.: 0 21 11 9xxxx
        if (!hasPlus && digits.StartsWith('0'))
        {
            digits = digits.TrimStart('0');
            if (digits.Length is 12 or 13 && !digits.StartsWith("55")) digits = digits[2..]; // operadora
        }

        string national;
        if (digits.StartsWith(defaultCountry) && digits.Length is 12 or 13) national = digits[2..];
        else if (digits.Length is 10 or 11) national = digits;
        else return new(false, digits, $"Quantidade de dígitos inválida ({digits.Length}); informe DDD + número");

        var ddd = int.Parse(national[..2]);
        if (!ValidDdds.Contains(ddd)) return new(false, digits, $"DDD inválido: {ddd}");
        var number = national[2..];

        if (number.Length == 8)
        {
            // Celular antigo sem o nono dígito (começa com 6–9) → acrescenta 9. Fixo (2–5) é mantido.
            if (number[0] is >= '6' and <= '9') number = "9" + number;
            else if (number[0] is < '2' or > '5') return new(false, digits, "Número fixo inválido");
        }
        else if (number.Length == 9 && number[0] != '9')
            return new(false, digits, "Celular com 9 dígitos deve começar com 9");

        return new(true, defaultCountry + national[..2] + number, null);
    }

    public static string Format(string normalized)
    {
        if (normalized.Length == 13 && normalized.StartsWith("55"))
            return $"+55 ({normalized[2..4]}) {normalized[4..9]}-{normalized[9..]}";
        if (normalized.Length == 12 && normalized.StartsWith("55"))
            return $"+55 ({normalized[2..4]}) {normalized[4..8]}-{normalized[8..]}";
        return "+" + normalized;
    }
}
