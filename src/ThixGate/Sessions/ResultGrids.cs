using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Matgate.Services.Database;

namespace ThixGate.Sessions;

/// <summary>The result of a statement as a grid (Fluent's DataGrid, columns from the result, cells as text, NULL marked).</summary>
internal static class ResultGrids
{
    public static DataGrid For(DbResult result, double maxHeight = double.PositiveInfinity)
    {
        var grid = new DataGrid
        {
            IsReadOnly = true,
            CanUserSortColumns = false,
            CanUserReorderColumns = true,
            CanUserResizeColumns = true,
            GridLinesVisibility = DataGridGridLinesVisibility.All,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            SelectionMode = DataGridSelectionMode.Extended,
            MaxHeight = maxHeight,
            MinHeight = 80,
            ItemsSource = result.Rows.Select(row => new ResultRow(row)).ToList(),
        };
        for (var index = 0; index < result.Columns.Count; index++)
        {
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = result.Columns[index],
                Binding = new Binding($"{nameof(ResultRow.Shown)}[{index}]") { Mode = BindingMode.OneWay },
                MaxWidth = 480,
            });
        }

        return grid;
    }

    /// <summary>The result as a tab separated table (with the column names): pasted into a spreadsheet it keeps its columns.</summary>
    public static string AsTable(DbResult result)
    {
        var text = new StringBuilder();
        text.AppendLine(string.Join('\t', result.Columns));
        foreach (var row in result.Rows)
        {
            text.AppendLine(string.Join('\t', row.Select(value => (value ?? "NULL").Replace('\t', ' ').Replace("\r", "", StringComparison.Ordinal).Replace('\n', ' '))));
        }

        return text.ToString();
    }

    /// <summary>A row as the grid shows it: NULL in words.</summary>
    public sealed class ResultRow(string?[] values)
    {
        public string?[] Values { get; } = values;

        public string[] Shown { get; } = [.. values.Select(value => value ?? "NULL")];
    }
}
