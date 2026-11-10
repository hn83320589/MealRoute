namespace MealRoute.Core.Reports;

/// <summary>
/// Spreadsheet-safe CSV text cells. Text a spreadsheet would evaluate as a formula (starting with
/// = + - @, tab or CR), or that already starts with the escape apostrophe, is written with a
/// leading apostrophe; <see cref="Unescape"/> removes it on import so round trips stay lossless.
/// Use only for free-text columns, never for numbers.
/// </summary>
public static class CsvText
{
    private const string FormulaTriggers = "=+-@\t\r'";

    public static string Escape(string text) =>
        text.Length > 0 && FormulaTriggers.Contains(text[0], StringComparison.Ordinal) ? "'" + text : text;

    public static string Unescape(string cell) =>
        cell.Length > 1 && cell[0] == '\'' && FormulaTriggers.Contains(cell[1], StringComparison.Ordinal) ? cell[1..] : cell;
}
