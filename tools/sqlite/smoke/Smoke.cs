using System;
using System.Data.SQLite;
using System.IO;

internal static class Smoke
{
    private static int Main()
    {
        int exit = 0;
        string path = Path.Combine(Path.GetTempPath(), "posdisc-smoke-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            Console.WriteLine("process: " + (IntPtr.Size == 8 ? "x64" : "x86")
                + " | clr: " + Environment.Version + " | os: " + Environment.OSVersion);

            using (SQLiteConnection c = new SQLiteConnection("Data Source=" + path + ";Version=3;Pooling=False;"))
            {
                c.Open();
                Exec(c, "PRAGMA journal_mode=WAL;");
                Exec(c, "PRAGMA synchronous=NORMAL;");
                Exec(c, "CREATE TABLE T(A INTEGER PRIMARY KEY AUTOINCREMENT, B TEXT, C REAL);");

                using (SQLiteCommand cmd = new SQLiteCommand("INSERT INTO T(B,C) VALUES(@b,@c)", c))
                {
                    cmd.Parameters.AddWithValue("@b", "سلام دنیا");
                    cmd.Parameters.AddWithValue("@c", 5000000.0);
                    Console.WriteLine("rows inserted: " + cmd.ExecuteNonQuery());
                }

                using (SQLiteCommand cmd = new SQLiteCommand("SELECT A,B,C FROM T", c))
                using (SQLiteDataReader r = cmd.ExecuteReader())
                {
                    while (r.Read())
                        Console.WriteLine("row: id=" + r.GetInt64(0) + " text=" + r.GetString(1) + " num=" + r.GetDouble(2).ToString("0"));
                }

                using (SQLiteCommand cmd = new SQLiteCommand("SELECT COUNT(*) FROM T WHERE B = @b", c))
                {
                    cmd.Parameters.AddWithValue("@b", "سلام دنیا");
                    Console.WriteLine("count by utf8 param: " + cmd.ExecuteScalar());
                }

                Exec(c, "BEGIN IMMEDIATE;");
                Exec(c, "INSERT INTO T(B,C) VALUES('در تراکنش', 1);");
                Exec(c, "ROLLBACK;");
                using (SQLiteCommand cmd = new SQLiteCommand("SELECT COUNT(*) FROM T", c))
                    Console.WriteLine("count after rollback: " + cmd.ExecuteScalar());

                Console.WriteLine("sqlite version: " + ExecScalarString(c, "SELECT sqlite_version()"));
                Console.WriteLine("db file bytes: " + new FileInfo(path).Length);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAILED: " + ex);
            exit = 1;
        }
        finally
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                string wal = path + "-wal";
                if (File.Exists(wal)) File.Delete(wal);
                string shm = path + "-shm";
                if (File.Exists(shm)) File.Delete(shm);
            }
            catch (Exception) { }
        }
        Console.WriteLine(exit == 0 ? "SMOKE OK" : "SMOKE FAILED");
        return exit;
    }

    private static void Exec(SQLiteConnection c, string sql)
    {
        using (SQLiteCommand cmd = new SQLiteCommand(sql, c)) cmd.ExecuteNonQuery();
    }

    private static string ExecScalarString(SQLiteConnection c, string sql)
    {
        using (SQLiteCommand cmd = new SQLiteCommand(sql, c))
        {
            object o = cmd.ExecuteScalar();
            return o == null ? "" : Convert.ToString(o);
        }
    }
}
