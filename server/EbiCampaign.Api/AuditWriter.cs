using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace EbiCampaign.Api;

/// <summary>
/// نویسندهٔ واحد رکوردهای حسابرسی. همهٔ سرویس‌ها (دوره‌ها، تصمیم‌ها، قرعه‌کشی) از همین مسیر
/// استفاده می‌کنند تا Actor/Action/Entity یکدست باشد.
/// </summary>
public static class AuditWriter
{
    public static async Task WriteAsync(SqlConnection cn, SqlTransaction? tx, string actor, string action,
        string entity, string? entityId, object details, CancellationToken ct = default)
    {
        using SqlCommand cmd = new(@"
INSERT INTO dbo.AuditLog(At, Actor, Action, Entity, EntityId, Details)
VALUES(SYSUTCDATETIME(), @actor, @action, @entity, @id, @details);", cn, tx);
        cmd.Parameters.AddWithValue("@actor", actor);
        cmd.Parameters.AddWithValue("@action", action);
        cmd.Parameters.AddWithValue("@entity", entity);
        cmd.Parameters.AddWithValue("@id", (object?)entityId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@details", JsonSerializer.Serialize(details, CampaignService.Json));
        await cmd.ExecuteNonQueryAsync(ct);
    }
}