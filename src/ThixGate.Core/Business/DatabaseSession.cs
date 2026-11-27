using Matgate.Services.Database;
using ThixGate.Core.Infrastructure;
using ThixGate.Core.Models;

namespace ThixGate.Core.Business;

/// <summary>
/// A database connection (SQL Server, MySQL/MariaDB, PostgreSQL) as the database tab uses it: Mat.Remote's <see cref="DatabaseGateway"/> -
/// the very code of Matgate's database browser (catalog, queries with several results, cancel, rows page by page, row edits in one
/// transaction, the table designer, plans, export) - with ThixGate's login (vault or asked) and, über Matgate, its relay.
/// </summary>
public sealed class DatabaseSession
{
    private const int Attempts = 3;
    private static readonly DatabaseGateway Gateway = new();

    private readonly string _user;
    private readonly string _password;

    private DatabaseSession(RemoteConnection connection, DatabaseTarget target, string user, string password)
    {
        Connection = connection;
        Target = target;
        _user = user;
        _password = password;
    }

    public RemoteConnection Connection { get; }

    public DatabaseTarget Target { get; }

    public DatabaseEngine Engine => Target.Protocol;

    /// <summary>The rows "Oberste … Zeilen auswählen" reads (as Management Studio's "Select Top Rows").</summary>
    public const int TopRows = 100;

    /// <summary>
    /// The statement of "Oberste 100 Zeilen auswählen", laid out as Management Studio writes it: the columns one a line (every column when
    /// none are known), SQL Server with TOP, MySQL and PostgreSQL with LIMIT; without <paramref name="count"/> every row ("Skript als SELECT").
    /// </summary>
    public static string SelectTop(DatabaseEngine engine, string schema, string table, IReadOnlyList<string> columns, int? count = TopRows)
    {
        var dialect = DatabaseGateway.DialectOf(engine);
        var list = columns.Count == 0 ? "*" : string.Join("\n      ,", columns.Select(dialect.Quote));
        var from = dialect.Table(schema, table);
        return count is null ? $"SELECT {list}\n  FROM {from}"
            : engine == DatabaseEngine.MsSql ? $"SELECT TOP ({count}) {list}\n  FROM {from}"
            : $"SELECT {list}\n  FROM {from}\n LIMIT {count}";
    }

    /// <summary>The most rows a query or a page shows (more: export).</summary>
    public static int MaxRows => DatabaseGateway.MaxRows;

    /// <summary>Signs in (asking when the vault has no password or it is refused) and reads the databases once.</summary>
    public static async Task<DatabaseSession> OpenAsync(RemoteConnection connection, AskSignIn ask, CancellationToken cancellationToken)
    {
        var login = SessionTargets.LoginOf(connection);
        var (user, password) = (login.UserName, login.Password);
        var canRemember = login.CredentialId is not null && !AccessPolicy.ArePasswordsDisabled;
        var target = await SessionTargets.DatabaseTargetAsync(connection, cancellationToken);
        LoginAnswer? typed = null;
        var refused = false;
        for (var attempt = 0; ; attempt++)
        {
            if (refused || user.Length == 0 || password.Length == 0)
            {
                typed = await ask(user, refused, canRemember) ?? throw new OperationCanceledException(cancellationToken);
                (user, password) = (typed.UserName.Trim(), typed.Password);
            }

            var session = new DatabaseSession(connection, target, user, password);
            try
            {
                await Gateway.DatabasesAsync(target, user, password, cancellationToken);
                if (typed is { Remember: true, Password.Length: > 0 } && login.CredentialId is { } credentialId)
                {
                    CredentialService.RememberPassword(credentialId, typed.Password);
                }

                LibraryService.MarkUsed(connection.Id);
                return session;
            }
            catch (DatabaseException exception) when (IsSignInRefused(exception) && attempt + 1 < Attempts)
            {
                Log.Info($"Database {connection.Id}: sign-in refused");
                refused = true;
                password = "";
            }
            catch (DatabaseException exception)
            {
                throw new BusinessException(exception.Message);
            }
        }
    }

    private static bool IsSignInRefused(DatabaseException exception)
    {
        var text = (exception.Message + " " + exception.InnerException?.Message).ToLowerInvariant();
        return text.Contains("login failed", StringComparison.Ordinal) || text.Contains("access denied", StringComparison.Ordinal)
               || text.Contains("password authentication failed", StringComparison.Ordinal) || text.Contains("anmeldung", StringComparison.Ordinal)
               || text.Contains("28p01", StringComparison.Ordinal) || text.Contains("18456", StringComparison.Ordinal);
    }

    // ================================================================= the gateway with this login

    public Task<List<string>> DatabasesAsync(CancellationToken cancellationToken) => Run(() => Gateway.DatabasesAsync(Target, _user, _password, cancellationToken));

    public Task<List<DbObject>> ObjectsAsync(string database, CancellationToken cancellationToken) =>
        Run(() => Gateway.ObjectsAsync(Target, _user, _password, database, cancellationToken));

    public Task<List<DbRoutine>> RoutinesAsync(string database, CancellationToken cancellationToken) =>
        Run(() => Gateway.RoutinesAsync(Target, _user, _password, database, cancellationToken));

    public Task<DbTableInfo> TableAsync(string database, string schema, string table, CancellationToken cancellationToken) =>
        Run(() => Gateway.TableAsync(Target, _user, _password, database, schema, table, cancellationToken));

    public Task<string> ScriptAsync(string database, string kind, string schema, string name, CancellationToken cancellationToken) =>
        Run(() => Gateway.ScriptAsync(Target, _user, _password, database, kind, schema, name, cancellationToken));

    public Task<DbResult> RowsAsync(string database, string schema, string table, int offset, int limit, string? orderColumn, bool descending, CancellationToken cancellationToken) =>
        Run(() => Gateway.RowsAsync(Target, _user, _password, database, schema, table, offset, limit, orderColumn, descending, cancellationToken));

    /// <summary>Runs a batch (several statements, several results); cancelled with the token.</summary>
    public Task<DbBatch> BatchAsync(string? database, string sql, CancellationToken cancellationToken) =>
        Run(() => Gateway.BatchAsync(Target, _user, _password, Empty(database), sql, readOnly: false, german: true, cancellationToken));

    public Task<DbPlan> PlanAsync(string? database, string sql, bool analyze, CancellationToken cancellationToken) =>
        Run(() => Gateway.PlanAsync(Target, _user, _password, Empty(database), sql, analyze && Engine == DatabaseEngine.Postgres, readOnly: false, german: true, cancellationToken));

    /// <summary>Row edits of a table (update, insert, delete by primary key): the statements to show first.</summary>
    public List<RowStatement> RowStatements(string schema, string table, DbTableInfo info, IReadOnlyList<RowChange> changes) =>
        Checked(() => DatabaseGateway.RowStatements(Engine, schema, table, info, changes, german: true));

    /// <summary>Applies row edits in one transaction (undone unless each hits exactly one row).</summary>
    public Task<int> ApplyRowsAsync(string? database, DbTableInfo info, IReadOnlyList<RowStatement> statements, CancellationToken cancellationToken) =>
        Run(() => Gateway.ApplyRowsAsync(Target, _user, _password, Empty(database), info, statements, german: true, cancellationToken));

    /// <summary>The statements of the table designer (new table or changes of one).</summary>
    public List<string> DesignStatements(string schema, string? originalName, DbTableInfo? original, TableDesign design) =>
        Checked(() => TableDesigner.Statements(Engine, schema, originalName, original, design, german: true));

    private static T Checked<T>(Func<T> make)
    {
        try
        {
            return make();
        }
        catch (DatabaseException exception)
        {
            throw new BusinessException(exception.Message);
        }
    }

    public Task<int> ApplyAsync(string? database, IReadOnlyList<string> statements, CancellationToken cancellationToken) =>
        Run(() => Gateway.ApplyAsync(Target, _user, _password, Empty(database), statements, german: true, cancellationToken));

    /// <summary>A query's result into a CSV or JSON file of this computer (up to a million rows).</summary>
    public async Task<long> ExportAsync(string? database, string sql, string localFile, bool json, char separator, CancellationToken cancellationToken)
    {
        long rows = 0;
        await using var file = File.Create(localFile);
        await using var writer = new StreamWriter(file, new System.Text.UTF8Encoding(true));
        IReadOnlyList<string> names = [];
        var first = true;
        await Run(async () =>
        {
            await Gateway.ExportAsync(Target, _user, _password, Empty(database), sql, readOnly: false, german: true, 1_000_000,
                async columns =>
                {
                    names = columns;
                    if (json)
                    {
                        await writer.WriteAsync("[");
                    }
                    else
                    {
                        await writer.WriteLineAsync(string.Join(separator, columns.Select(name => Csv(name, separator))));
                    }
                },
                async row =>
                {
                    rows++;
                    if (json)
                    {
                        var item = new System.Text.Json.Nodes.JsonObject();
                        for (var i = 0; i < names.Count; i++)
                        {
                            item[names[i]] = row[i];
                        }

                        await writer.WriteAsync((first ? "" : ",") + Environment.NewLine + "  " + item.ToJsonString());
                        first = false;
                    }
                    else
                    {
                        await writer.WriteLineAsync(string.Join(separator, row.Select(value => Csv(value, separator))));
                    }
                },
                cancellationToken);
            if (json)
            {
                await writer.WriteAsync(Environment.NewLine + "]" + Environment.NewLine);
            }

            return 0;
        });
        return rows;
    }

    private static string Csv(string? value, char separator) =>
        value is null ? "" : value.IndexOfAny([separator, '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;

    private static string? Empty(string? database) => string.IsNullOrWhiteSpace(database) ? null : database;

    private async Task<T> Run<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (DatabaseException exception)
        {
            throw new BusinessException(exception.Message);
        }
        catch (Exception exception) when (exception is System.Data.Common.DbException or InvalidOperationException or IOException or System.Net.Sockets.SocketException)
        {
            Log.Warning($"Database {Connection.Id}: {exception.GetType().Name}: {exception.Message}");
            throw new BusinessException(exception.Message);
        }
    }
}
