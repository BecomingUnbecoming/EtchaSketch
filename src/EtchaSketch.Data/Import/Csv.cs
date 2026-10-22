using System.Text;

namespace EtchaSketch.Data.Import;

/// <summary>Reads comma- or tab-separated text, with quoted fields that may hold separators, quotes and line breaks.</summary>
public static class Csv
{
    /// <summary>
    /// The rows of a sheet, the header first. The separator is a tab if the first line has one
    /// before any comma, as Homebox decides; otherwise a comma.
    /// </summary>
    public static List<string[]> Read(string text)
    {
        text = text.TrimStart('﻿');
        var firstLine = text.Split('\n', 2)[0];
        var tab = firstLine.IndexOf('\t');
        var comma = firstLine.IndexOf(',');
        var separator = tab >= 0 && (comma < 0 || tab < comma) ? '\t' : ',';

        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == '"' && field.Length == 0)
            {
                quoted = true;
            }
            else if (c == separator)
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (c is '\n' or '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                row.Add(field.ToString());
                field.Clear();
                AddRow(rows, row);
                row = [];
            }
            else
            {
                field.Append(c);
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            AddRow(rows, row);
        }

        return rows;
    }

    // Blank lines are skipped.
    private static void AddRow(List<string[]> rows, List<string> row)
    {
        if (row.Count > 1 || row[0].Length > 0)
        {
            rows.Add([.. row]);
        }
    }
}

/// <summary>One row of a sheet, read by column name (ignoring capitals), with the first name that exists winning.</summary>
public sealed class CsvRow(IReadOnlyDictionary<string, int> columns, string[] cells, int number)
{
    /// <summary>The row's line in the file, counting the header as 1, for problem messages.</summary>
    public int Number { get; } = number;

    public string? this[params string[] names]
    {
        get
        {
            foreach (var name in names)
            {
                if (columns.TryGetValue(name, out var index) && index < cells.Length && cells[index].Trim() is { Length: > 0 } value)
                {
                    return value;
                }
            }

            return null;
        }
    }

    public static (IReadOnlyDictionary<string, int> Columns, List<CsvRow> Rows) Sheet(string text)
    {
        var rows = Csv.Read(text);
        if (rows.Count == 0)
        {
            throw new FormatException("The file is empty.");
        }

        var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < rows[0].Length; i++)
        {
            columns.TryAdd(rows[0][i].Trim(), i);
        }

        return (columns, rows.Skip(1).Select((cells, i) => new CsvRow(columns, cells, i + 2)).ToList());
    }
}
