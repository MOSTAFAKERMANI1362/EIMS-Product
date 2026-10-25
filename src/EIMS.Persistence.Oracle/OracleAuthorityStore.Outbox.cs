using System.Data.Common;
using EIMS.Outbox;
using Oracle.ManagedDataAccess.Client;

namespace EIMS.Persistence.OracleAdapter;

// S3: Oracle outbox store for the dispatcher (claim token + lease on EIMS_OUTBOX, migration V005).
// Claim = ONE UPDATE statement (no row locks are held while publishing): the earliest due message of each aggregate, only when no
// earlier message of that aggregate is still PENDING/FAILED, gets CLAIM_TOKEN + CLAIMED_UNTIL. Concurrent workers cannot claim the same
// row (the UPDATE re-checks the lease predicate after waiting for the row lock); a rare deadlock (ORA-00060) yields an empty batch.
public sealed partial class OracleAuthorityStore : IOutboxStore, IOutboxInspector
{
    private static readonly string OutboxSelect =
        "SELECT OUTBOX_SEQ, MESSAGE_ID, EVENT_NAME, AGGREGATE_ID, AGGREGATE_TYPE, AGGREGATE_VERSION, CORRELATION_ID, " + Ts("OCCURRED_AT") +
        ", PAYLOAD_JSON, ATTEMPTS, CLAIM_TOKEN FROM EIMS_OUTBOX";

    public async ValueTask<IReadOnlyList<OutboxMessage>> ClaimBatchAsync(OutboxClaimRequest request, CancellationToken cancellationToken = default)
    {
        if (request.MaxCount < 1) return Array.Empty<OutboxMessage>();
        var token = request.WorkerId + ":" + Guid.NewGuid().ToString("N");
        var types = request.Filter?.AggregateTypes;
        var typeClause = types is { Count: > 0 }
            ? " AND x.AGGREGATE_TYPE IN (" + string.Join(", ", types.Select((_, i) => ":p_t" + i)) + ")"
            : string.Empty;

        await using var conn = await OpenAsync(cancellationToken);
        using (var tx = conn.BeginTransaction())
        {
            try
            {
                await using var upd = Cmd(conn, tx,
                    "UPDATE EIMS_OUTBOX o SET o.CLAIM_TOKEN = :p_token, o.CLAIMED_UNTIL = :p_until " +
                    "WHERE o.DISPATCH_STATUS = 'PENDING' AND (o.CLAIMED_UNTIL IS NULL OR o.CLAIMED_UNTIL < :p_now) " +
                    "AND o.OUTBOX_SEQ IN (SELECT c.OUTBOX_SEQ FROM (" +
                    "SELECT x.OUTBOX_SEQ FROM EIMS_OUTBOX x WHERE x.DISPATCH_STATUS = 'PENDING' " +
                    "AND (x.NEXT_ATTEMPT_AT IS NULL OR x.NEXT_ATTEMPT_AT <= :p_now) " +
                    "AND (x.CLAIMED_UNTIL IS NULL OR x.CLAIMED_UNTIL < :p_now)" + typeClause + " " +
                    "AND NOT EXISTS (SELECT 1 FROM EIMS_OUTBOX p WHERE p.AGGREGATE_ID = x.AGGREGATE_ID AND p.OUTBOX_SEQ < x.OUTBOX_SEQ " +
                    "AND p.DISPATCH_STATUS IN ('PENDING', 'FAILED')) ORDER BY x.OUTBOX_SEQ) c WHERE ROWNUM <= :p_max)");
                P(upd, "p_token", OracleDbType.Varchar2, token);
                P(upd, "p_until", OracleDbType.TimeStampTZ, request.Now + request.Lease);
                P(upd, "p_now", OracleDbType.TimeStampTZ, request.Now);
                P(upd, "p_max", OracleDbType.Int32, request.MaxCount);
                if (types is { Count: > 0 })
                    for (var i = 0; i < types.Count; i++)
                        P(upd, "p_t" + i, OracleDbType.Varchar2, types[i]);
                await upd.ExecuteNonQueryAsync(cancellationToken);
                tx.Commit();
            }
            catch (OracleException ex) when (ex.Number == 60)
            {
                tx.Rollback();
                return Array.Empty<OutboxMessage>();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }

        await using var sel = Cmd(conn, null, OutboxSelect + " WHERE CLAIM_TOKEN = :p_token ORDER BY OUTBOX_SEQ");
        P(sel, "p_token", OracleDbType.Varchar2, token);
        await using var r = await sel.ExecuteReaderAsync(cancellationToken);
        var list = new List<OutboxMessage>();
        while (await r.ReadAsync(cancellationToken))
            list.Add(new OutboxMessage(
                Convert.ToInt64(r.GetValue(0)), r.GetString(1), r.GetString(2), r.GetString(3), Str(r, 4), Convert.ToInt64(r.GetValue(5)),
                r.GetString(6), ParseTs(r.GetString(7)), Str(r, 8), Convert.ToInt32(r.GetValue(9)), r.GetString(10)));
        return list;
    }

    public async ValueTask<bool> MarkSentAsync(OutboxMessage message, DateTimeOffset sentAt, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        await using var cmd = Cmd(conn, null,
            "UPDATE EIMS_OUTBOX SET DISPATCH_STATUS = 'SENT', SENT_AT = :p_at, ATTEMPTS = ATTEMPTS + 1, LAST_ERROR = NULL, NEXT_ATTEMPT_AT = NULL, " +
            "CLAIM_TOKEN = NULL, CLAIMED_UNTIL = NULL WHERE MESSAGE_ID = :p_id AND CLAIM_TOKEN = :p_token AND DISPATCH_STATUS = 'PENDING'");
        P(cmd, "p_at", OracleDbType.TimeStampTZ, sentAt);
        P(cmd, "p_id", OracleDbType.Varchar2, message.MessageId);
        P(cmd, "p_token", OracleDbType.Varchar2, message.ClaimToken);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async ValueTask<bool> MarkFailedAsync(
        OutboxMessage message, string error, DateTimeOffset? nextAttemptAt, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        await using var cmd = Cmd(conn, null,
            "UPDATE EIMS_OUTBOX SET DISPATCH_STATUS = :p_status, ATTEMPTS = ATTEMPTS + 1, LAST_ERROR = :p_err, NEXT_ATTEMPT_AT = :p_next, " +
            "CLAIM_TOKEN = NULL, CLAIMED_UNTIL = NULL WHERE MESSAGE_ID = :p_id AND CLAIM_TOKEN = :p_token AND DISPATCH_STATUS = 'PENDING'");
        P(cmd, "p_status", OracleDbType.Varchar2, nextAttemptAt is null ? "FAILED" : "PENDING");
        P(cmd, "p_err", OracleDbType.Varchar2, Nz(error.Length > 1000 ? error[..1000] : error));
        P(cmd, "p_next", OracleDbType.TimeStampTZ, nextAttemptAt);
        P(cmd, "p_id", OracleDbType.Varchar2, message.MessageId);
        P(cmd, "p_token", OracleDbType.Varchar2, message.ClaimToken);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async ValueTask<bool> RequeueFailedAsync(string messageId, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        await using var cmd = Cmd(conn, null,
            "UPDATE EIMS_OUTBOX SET DISPATCH_STATUS = 'PENDING', ATTEMPTS = 0, NEXT_ATTEMPT_AT = NULL, LAST_ERROR = NULL " +
            "WHERE MESSAGE_ID = :p_id AND DISPATCH_STATUS = 'FAILED'");
        P(cmd, "p_id", OracleDbType.Varchar2, messageId);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async ValueTask<OutboxRow?> GetOutboxRowAsync(string messageId, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        await using var cmd = Cmd(conn, null,
            "SELECT DISPATCH_STATUS, ATTEMPTS, " + Ts("NEXT_ATTEMPT_AT") + ", " + Ts("SENT_AT") + ", LAST_ERROR, CLAIM_TOKEN " +
            "FROM EIMS_OUTBOX WHERE MESSAGE_ID = :p_id");
        P(cmd, "p_id", OracleDbType.Varchar2, messageId);
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await r.ReadAsync(cancellationToken)) return null;
        var status = r.GetString(0) switch { "SENT" => OutboxStatus.Sent, "FAILED" => OutboxStatus.Failed, _ => OutboxStatus.Pending };
        return new OutboxRow(messageId, status, Convert.ToInt32(r.GetValue(1)),
            r.IsDBNull(2) ? null : ParseTs(r.GetString(2)), r.IsDBNull(3) ? null : ParseTs(r.GetString(3)), Str(r, 4), Str(r, 5));
    }

    /// <summary>Test/utility: inserts a PENDING outbox row directly (production rows are written by the atomic commit paths).</summary>
    public async Task EnqueueOutboxMessageAsync(
        string messageId, string eventName, string aggregateId, string? aggregateType, long aggregateVersion, string correlationId,
        DateTimeOffset occurredAt, string? payloadJson, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        await using var cmd = Cmd(conn, null,
            "INSERT INTO EIMS_OUTBOX (MESSAGE_ID, EVENT_NAME, AGGREGATE_ID, AGGREGATE_TYPE, AGGREGATE_VERSION, CORRELATION_ID, OCCURRED_AT, PAYLOAD_JSON) " +
            "VALUES (:p_mid, :p_evt, :p_id, :p_type, :p_ver, :p_corr, :p_at, :p_payload)");
        P(cmd, "p_mid", OracleDbType.Varchar2, messageId);
        P(cmd, "p_evt", OracleDbType.Varchar2, eventName);
        P(cmd, "p_id", OracleDbType.Varchar2, aggregateId);
        P(cmd, "p_type", OracleDbType.Varchar2, Nz(aggregateType));
        P(cmd, "p_ver", OracleDbType.Int64, aggregateVersion);
        P(cmd, "p_corr", OracleDbType.Varchar2, correlationId);
        P(cmd, "p_at", OracleDbType.TimeStampTZ, occurredAt);
        P(cmd, "p_payload", OracleDbType.Clob, payloadJson);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
