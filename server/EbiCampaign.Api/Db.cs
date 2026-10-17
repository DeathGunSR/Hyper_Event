using Microsoft.Data.SqlClient;

namespace EbiCampaign.Api;

public sealed class Db
{
    private readonly string _connectionString;

    public Db(string connectionString)
    {
        _connectionString = connectionString;
    }

    public string ConnectionString => _connectionString;

    public async Task<SqlConnection> OpenAsync(CancellationToken ct = default)
    {
        SqlConnection cn = new(_connectionString);
        await cn.OpenAsync(ct);
        return cn;
    }

    /// <summary>ساخت دیتابیس کمپین در صورت نبودن (فقط برای توسعه/آزمون).</summary>
    public static async Task EnsureDatabaseAsync(string connectionString, CancellationToken ct = default)
    {
        SqlConnectionStringBuilder b = new(connectionString);
        string dbName = b.InitialCatalog;
        if (string.IsNullOrWhiteSpace(dbName))
            throw new InvalidOperationException("نام دیتابیس در رشتهٔ اتصال مشخص نیست.");

        b.InitialCatalog = "master";
        await using SqlConnection cn = new(b.ConnectionString);
        await cn.OpenAsync(ct);
        await using SqlCommand cmd = new(@"
IF DB_ID(@name) IS NULL
BEGIN
    DECLARE @sql NVARCHAR(MAX) = N'CREATE DATABASE ' + QUOTENAME(@name);
    EXEC(@sql);
END;", cn);
        cmd.Parameters.AddWithValue("@name", dbName);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>اجرای مهاجرت‌های نسخه‌دار از پوشهٔ migrations کنار فایل اجرایی.</summary>
    public static async Task MigrateAsync(string connectionString, CancellationToken ct = default)
    {
        using SqlConnection cn = new(connectionString);
        await cn.OpenAsync(ct);

        using (SqlCommand ensure = new(@"
IF OBJECT_ID(N'dbo.SchemaVersions', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SchemaVersions(
        Version   INT          NOT NULL PRIMARY KEY,
        AppliedAt DATETIME2(0) NOT NULL
    );
END;", cn))
        {
            await ensure.ExecuteNonQueryAsync(ct);
        }

        HashSet<int> applied = new();
        using (SqlCommand read = new("SELECT Version FROM dbo.SchemaVersions", cn))
        using (SqlDataReader r = await read.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct)) applied.Add(r.GetInt32(0));
        }

        // مهاجرت‌ها به‌صورت EmbeddedResource داخل اسمبلی هستند تا استقرار تک‌فایلی هم کار کند.
        var resources = typeof(Db).Assembly.GetManifestResourceNames()
            .Where(n => n.Contains(".migrations.", StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        if (resources.Count == 0)
            throw new InvalidOperationException("هیچ مهاجرتی داخل اسمبلی پیدا نشد.");

        foreach (string resource in resources)
        {
            int marker = resource.IndexOf(".migrations.", StringComparison.Ordinal);
            string name = resource[(marker + ".migrations.".Length)..];
            int version = ParseVersion(name);
            if (version <= 0 || applied.Contains(version)) continue;

            string sql;
            using (Stream s = typeof(Db).Assembly.GetManifestResourceStream(resource)!
                        ?? throw new InvalidOperationException("منبع مهاجرت باز نشد: " + resource))
            using (StreamReader sr = new(s, System.Text.Encoding.UTF8))
            {
                sql = await sr.ReadToEndAsync(ct);
            }
            using SqlTransaction tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);
            try
            {
                using (SqlCommand cmd = new(sql, cn, tx))
                {
                    cmd.CommandTimeout = 120;
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                using (SqlCommand mark = new(
                    "INSERT INTO dbo.SchemaVersions(Version, AppliedAt) VALUES(@v, SYSUTCDATETIME())", cn, tx))
                {
                    mark.Parameters.AddWithValue("@v", version);
                    await mark.ExecuteNonQueryAsync(ct);
                }
                await tx.CommitAsync(ct);
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        }
    }

    private static int ParseVersion(string fileName)
    {
        int cut = fileName.IndexOf('_');
        if (cut <= 0) cut = fileName.IndexOf('.');
        string digits = cut > 0 ? fileName[..cut] : fileName;
        return int.TryParse(digits, out int v) ? v : 0;
    }
}
