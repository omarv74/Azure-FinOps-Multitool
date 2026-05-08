using System.Collections.Generic;

namespace FinOpsMultitool.Helpers
{
    /// <summary>
    /// Maps ISO 4217 currency codes to display symbols.
    /// </summary>
    public static class CurrencyHelper
    {
        private static readonly Dictionary<string, string> _symbols = new(System.StringComparer.OrdinalIgnoreCase)
        {
            ["USD"] = "$",
            ["EUR"] = "€",
            ["GBP"] = "£",
            ["JPY"] = "¥",
            ["AUD"] = "A$",
            ["CAD"] = "C$",
            ["CHF"] = "Fr",
            ["CNY"] = "¥",
            ["SEK"] = "kr",
            ["NOK"] = "kr",
            ["DKK"] = "kr",
            ["INR"] = "₹",
            ["BRL"] = "R$",
            ["MXN"] = "$",
            ["SGD"] = "S$",
            ["HKD"] = "HK$",
            ["KRW"] = "₩",
            ["TRY"] = "₺",
            ["ZAR"] = "R",
            ["NZD"] = "NZ$",
            ["AED"] = "د.إ",
            ["SAR"] = "﷼",
            ["ILS"] = "₪",
            ["PLN"] = "zł",
            ["CZK"] = "Kč",
            ["HUF"] = "Ft",
            ["RON"] = "lei",
            ["BGN"] = "лв",
            ["HRK"] = "kn",
            ["RUB"] = "₽",
            ["UAH"] = "₴",
        };

        /// <summary>Returns the currency symbol for the given currency code, or the code itself if unknown.</summary>
        public static string GetSymbol(string? currencyCode)
        {
            if (string.IsNullOrWhiteSpace(currencyCode))
                return "$";
            return _symbols.TryGetValue(currencyCode, out var sym) ? sym : currencyCode;
        }

        /// <summary>Formats a monetary value with the appropriate symbol.</summary>
        public static string Format(double amount, string? currencyCode, int decimals = 2)
        {
            string sym = GetSymbol(currencyCode);
            string fmt = "N" + decimals;
            return sym + amount.ToString(fmt);
        }
    }
}
