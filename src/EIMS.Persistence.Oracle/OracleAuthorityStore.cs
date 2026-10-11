using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;
using EIMS.Authority.Recovery;
using EIMS.Persistence.Recovery;
using Oracle.ManagedDataAccess.Client;

namespace EIMS.Persistence.OracleAdapter;

/// <summary>
/// S2a+S2b: Oracle implementation of the kernel commit path (aggregate state + decisions + evaluation plan/assignments + audit + outbox + idempotency)
/// in ONE database transaction. Evaluation COMPLETION and G04 assessment persistence are bound in S2c and fail closed until then (see OracleAuthorityStore.Evaluation.cs).
/// The connection string is supplied by the caller (environment/secret store); it never lives in the repository.
/// </summary>
public sealed partial class OracleAuthorityStore : IEvaluationWorkflowStore, IFaultInjectablePersistence
{
    private static readonly Regex SchemaPattern = new("^[A-Za-z][A-Za-z0-9_]{0,29}$", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly string _connectionString;
    private readonly string _schema;

    public OracleAuthorityStore(string connectionString, string schema = "EIMS_OWNER")
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("A connection string is required.", nameof(connectionString));
        if (!SchemaPattern.IsMatch(schema))
            throw new ArgumentException("Invalid schema name.", nameof(schema));
        _connectionString = connectionString;
        _schema = schema;
    }

    public PersistenceFaultPoint FaultPoint { get; set; }

    public async ValueTask<AggregateSnapshot?> GetAggregateAsync(string aggregateId, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        return await ReadAggregateAsync(conn, null, aggregateId, cancellationToken);
    }

    public async ValueTask<IdempotencyRecord?> GetIdempotencyAsync(
        string commandName, string aggregateId, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        return await ReadIdempotencyAsync(conn, null, commandName, aggregateId, idempotencyKey, cancellationToken);
    }

    /// <summary>Test/seed utility: inserts an aggregate (version as given). Not part of the production command path.</summary>
    public async Task SeedAggregateAsync(AggregateSnapshot a, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        await using var cmd = Cmd(conn, null,
            "INSERT INTO EIMS_AGGREGATE (AGGREGATE_ID, AGGREGATE_TYPE, AGG_STATE, ENTITY_VERSION, OWNER_PERSON_ID, OWNER_ROLE, SCOPE_CODE, WORK_ROUTING_ROLE, STATE_MUTATION_VERSION, RULE_FACTS_JSON) " +
            "VALUES (:p_id, :p_type, :p_state, :p_ver, :p_owner, :p_role, :p_scope, :p_route, :p_smv, :p_facts)");
        BindAggregate(cmd, a);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Evidence counts for one aggregate (audit, outbox, decisions, idempotency).</summary>
    public async Task<(int Audit, int Outbox, int Decisions, int Idempotency)> CountEvidenceAsync(
        string aggregateId, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        var audit = await CountAsync(conn, "EIMS_AUDIT_LOG", aggregateId, cancellationToken);
        var outbox = await CountAsync(conn, "EIMS_OUTBOX", aggregateId, cancellationToken);
        var decisions = await CountAsync(conn, "EIMS_DOMAIN_DECISION", aggregateId, cancellationToken);
        var idem = await CountAsync(conn, "EIMS_IDEMPOTENCY", aggregateId, cancellationToken);
        return (audit, outbox, decisions, idem);
    }

    public async ValueTask<AuthorityResult> CommitAsync(
        MutationRequest request, MutationCommit commit, CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var conn = await OpenAsync(cancellationToken);
            using var tx = conn.BeginTransaction();
            var corr = request.Command.CorrelationId;

            var prior = await ReadIdempotencyAsync(conn, tx, request.Command.CommandName,
                request.Command.AggregateId, request.Command.IdempotencyKey, cancellationToken);
            if (prior is not null)
            {
                if (!string.Equals(prior.Fingerprint, request.IdempotencyFingerprint, StringComparison.Ordinal))
                    return AuthorityResult.Deny(409, "P2_IDEMPOTENCY_CONFLICT", corr,
                        "The same idempotency key is already committed with a different fingerprint.");
                return prior.Result with { IdempotentReplay = true, StateMutated = false, CorrelationId = corr };
            }

            var current = await ReadAggregateAsync(conn, tx, request.Before.AggregateId, cancellationToken);
            if (current is null)
                return AuthorityResult.Deny(404, "P2_AGGREGATE_NOT_FOUND", corr);
            if (request.Command.ExpectedVersion != request.Before.Version)
                return AuthorityResult.Deny(409, "P2_REQUEST_VERSION_MISMATCH", corr);
            if (current.Version != request.Before.Version)
                return AuthorityResult.Deny(409, "P2_VERSION_CONFLICT", corr,
                    $"Expected {request.Before.Version}; current {current.Version}.");

            var contractError = TransactionalAuthorityStore.ValidateCommitShape(request, commit);
            if (contractError is not null)
                return AuthorityResult.Deny(500, contractError, corr);

            var decisions = (commit.Decisions ?? Array.Empty<DomainDecisionEnvelope>()).ToArray();
            if (decisions.GroupBy(x => x.DecisionId, StringComparer.Ordinal).Any(g => g.Count() > 1))
                return AuthorityResult.Deny(409, "P2_DUPLICATE_DECISION_ID", corr);

            var plan = commit.EvaluationPlan;
            var assignments = (commit.EvaluationAssignments ?? Array.Empty<EvaluationAssignmentEnvelope>()).ToArray();
            if (assignments.GroupBy(x => x.AssignmentId, StringComparer.Ordinal).Any(g => g.Count() > 1))
                return AuthorityResult.Deny(409, "P2_DUPLICATE_EVALUATION_ASSIGNMENT_ID", corr);

            var result = new AuthorityResult(
                200, "P2_ATOMIC_COMMIT", Allowed: true, StateMutated: true, IdempotentReplay: false,
                NewVersion: commit.After.Version, CorrelationId: corr,
                EmittedEvents: Array.AsReadOnly(new[] { commit.Outbox.EventName }));

            try
            {
                await UpdateAggregateAsync(conn, tx, commit.After, request.Before.Version, cancellationToken);
                ThrowIf(PersistenceFaultPoint.AfterStateStaged);

                foreach (var d in decisions)
                    await InsertDecisionAsync(conn, tx, commit.After, d, cancellationToken);
                ThrowIf(PersistenceFaultPoint.AfterDecisionStaged);

                if (plan is not null)
                    await InsertPlanAsync(conn, tx, plan, cancellationToken);
                ThrowIf(PersistenceFaultPoint.AfterEvaluationPlanStaged);

                foreach (var a in assignments)
                    await InsertAssignmentAsync(conn, tx, a, cancellationToken);
                ThrowIf(PersistenceFaultPoint.AfterEvaluationAssignmentsStaged);

                await InsertAuditAsync(conn, tx, commit.After, commit.Audit, cancellationToken);
                ThrowIf(PersistenceFaultPoint.AfterAuditStaged);

                await InsertOutboxAsync(conn, tx, commit.After, commit.Outbox, cancellationToken);
                ThrowIf(PersistenceFaultPoint.AfterOutboxStaged);

                await InsertIdempotencyAsync(conn, tx, request, result, cancellationToken);
                ThrowIf(PersistenceFaultPoint.AfterIdempotencyStaged);
                ThrowIf(PersistenceFaultPoint.BeforeCommitPublish);

                tx.Commit();
                return result;
            }
            catch (OracleException ex) when (ex.Number == 1)
            {
                tx.Rollback();
                if (Has(ex, "PK_EIMS_IDEMPOTENCY") && attempt == 0)
                    continue; // concurrent same-key commit won the race: re-read it as replay/conflict
                if (Has(ex, "PK_EIMS_DECISION")) return AuthorityResult.Deny(409, "P2_DUPLICATE_DECISION_ID", corr);
                if (Has(ex, "PK_EVALUATION_PLAN") || Has(ex, "UQ_EVAL_PLAN_IDEA_VERSION"))
                    return AuthorityResult.Deny(409, "P2_DUPLICATE_EVALUATION_PLAN", corr);
                if (Has(ex, "PK_EVALUATION_ASSIGNMENT"))
                    return AuthorityResult.Deny(409, "P2_DUPLICATE_EVALUATION_ASSIGNMENT_ID", corr);
                if (Has(ex, "UQ_EIMS_AUDIT_ID")) return AuthorityResult.Deny(409, "P2_DUPLICATE_AUDIT_ID", corr);
                if (Has(ex, "UQ_EIMS_OUTBOX_MSG")) return AuthorityResult.Deny(409, "P2_DUPLICATE_OUTBOX_ID", corr);
                throw;
            }
            catch (VersionRaceException)
            {
                tx.Rollback();
                return AuthorityResult.Deny(409, "P2_VERSION_CONFLICT", corr, "Concurrent update detected.");
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }

        throw new PersistenceAtomicityException("Idempotency race could not be resolved.");
    }

    // ---------------------------------------------------------------- SQL helpers

    private sealed class VersionRaceException : Exception;

    private static bool Has(OracleException ex, string constraint) =>
        ex.Message.Contains(constraint, StringComparison.OrdinalIgnoreCase);

    private void ThrowIf(PersistenceFaultPoint point)
    {
        if (FaultPoint == point)
            throw new PersistenceAtomicityException($"Injected persistence failure at {point}.");
    }

    private async Task<OracleConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new OracleConnection(_connectionString);
        try
        {
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "ALTER SESSION SET CURRENT_SCHEMA = " + _schema;
            await cmd.ExecuteNonQueryAsync(ct);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    private static OracleCommand Cmd(OracleConnection conn, OracleTransaction? tx, string sql)
    {
        var c = conn.CreateCommand();
        c.BindByName = true;
        c.CommandText = sql;
        c.InitialLOBFetchSize = -1;
        if (tx is not null) c.Transaction = tx;
        return c;
    }

    private static void P(OracleCommand c, string name, OracleDbType type, object? value)
    {
        var p = c.Parameters.Add(name, type);
        p.Value = value ?? DBNull.Value;
    }

    private static string? Nz(string? s) => string.IsNullOrEmpty(s) ? null : s; // Oracle: '' is NULL
    private static string? ToJson(IReadOnlyDictionary<string, string>? d) => d is null ? null : JsonSerializer.Serialize(d);

    private static IReadOnlyDictionary<string, string>? FromJson(string? j) =>
        string.IsNullOrWhiteSpace(j) ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(j);

    private static string? Str(DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static void BindAggregate(OracleCommand c, AggregateSnapshot a)
    {
        P(c, "p_id", OracleDbType.Varchar2, a.AggregateId);
        P(c, "p_type", OracleDbType.Varchar2, a.AggregateType);
        P(c, "p_state", OracleDbType.Varchar2, a.State);
        P(c, "p_ver", OracleDbType.Int64, a.Version);
        P(c, "p_owner", OracleDbType.Varchar2, Nz(a.OwnerPersonId));
        P(c, "p_role", OracleDbType.Varchar2, Nz(a.OwnerRole));
        P(c, "p_scope", OracleDbType.Varchar2, Nz(a.Scope));
        P(c, "p_route", OracleDbType.Varchar2, Nz(a.WorkRoutingRole));
        P(c, "p_smv", OracleDbType.Int64, a.StateMutationVersion);
        P(c, "p_facts", OracleDbType.Clob, ToJson(a.RuleFacts));
    }

    private static async Task<int> CountAsync(OracleConnection conn, string table, string aggregateId, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, null, "SELECT COUNT(*) FROM " + table + " WHERE AGGREGATE_ID = :p_id");
        P(cmd, "p_id", OracleDbType.Varchar2, aggregateId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task<AggregateSnapshot?> ReadAggregateAsync(
        OracleConnection conn, OracleTransaction? tx, string id, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, tx,
            "SELECT AGGREGATE_TYPE, AGG_STATE, ENTITY_VERSION, OWNER_PERSON_ID, OWNER_ROLE, SCOPE_CODE, RULE_FACTS_JSON, WORK_ROUTING_ROLE, STATE_MUTATION_VERSION " +
            "FROM EIMS_AGGREGATE WHERE AGGREGATE_ID = :p_id");
        P(cmd, "p_id", OracleDbType.Varchar2, id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new AggregateSnapshot(
            id, r.GetString(0), r.GetString(1), Convert.ToInt64(r.GetValue(2)),
            Str(r, 3), Str(r, 4), Str(r, 5), FromJson(Str(r, 6)), Str(r, 7),
            r.IsDBNull(8) ? null : Convert.ToInt64(r.GetValue(8)));
    }

    private static async Task<IdempotencyRecord?> ReadIdempotencyAsync(
        OracleConnection conn, OracleTransaction? tx, string command, string aggregateId, string key, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, tx,
            "SELECT FINGERPRINT, RESULT_JSON FROM EIMS_IDEMPOTENCY " +
            "WHERE COMMAND_NAME = :p_cmd AND AGGREGATE_ID = :p_id AND IDEMPOTENCY_KEY = :p_key");
        P(cmd, "p_cmd", OracleDbType.Varchar2, command);
        P(cmd, "p_id", OracleDbType.Varchar2, aggregateId);
        P(cmd, "p_key", OracleDbType.Varchar2, key);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        var result = JsonSerializer.Deserialize<AuthorityResult>(r.GetString(1), Json)
            ?? throw new InvalidOperationException("Stored idempotency result is empty.");
        return new IdempotencyRecord(command, aggregateId, key, r.GetString(0), result);
    }

    private static async Task UpdateAggregateAsync(
        OracleConnection conn, OracleTransaction tx, AggregateSnapshot a, long expectedVersion, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, tx,
            "UPDATE EIMS_AGGREGATE SET AGG_STATE = :p_state, ENTITY_VERSION = :p_ver, OWNER_PERSON_ID = :p_owner, OWNER_ROLE = :p_role, " +
            "SCOPE_CODE = :p_scope, WORK_ROUTING_ROLE = :p_route, STATE_MUTATION_VERSION = :p_smv, RULE_FACTS_JSON = :p_facts, UPDATED_AT = SYSTIMESTAMP " +
            "WHERE AGGREGATE_ID = :p_id AND ENTITY_VERSION = :p_before");
        P(cmd, "p_state", OracleDbType.Varchar2, a.State);
        P(cmd, "p_ver", OracleDbType.Int64, a.Version);
        P(cmd, "p_owner", OracleDbType.Varchar2, Nz(a.OwnerPersonId));
        P(cmd, "p_role", OracleDbType.Varchar2, Nz(a.OwnerRole));
        P(cmd, "p_scope", OracleDbType.Varchar2, Nz(a.Scope));
        P(cmd, "p_route", OracleDbType.Varchar2, Nz(a.WorkRoutingRole));
        P(cmd, "p_smv", OracleDbType.Int64, a.StateMutationVersion);
        P(cmd, "p_facts", OracleDbType.Clob, ToJson(a.RuleFacts));
        P(cmd, "p_id", OracleDbType.Varchar2, a.AggregateId);
        P(cmd, "p_before", OracleDbType.Int64, expectedVersion);
        if (await cmd.ExecuteNonQueryAsync(ct) != 1) throw new VersionRaceException();
    }

    private static async Task InsertDecisionAsync(
        OracleConnection conn, OracleTransaction tx, AggregateSnapshot a, DomainDecisionEnvelope d, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, tx,
            "INSERT INTO EIMS_DOMAIN_DECISION (DECISION_ID, DECISION_TYPE, OUTCOME, AGGREGATE_ID, AGGREGATE_TYPE, ENTITY_VERSION, PERSON_ID, ASSIGNMENT_ID, DECIDED_AT, CORRELATION_ID, NOTE, FACTS_JSON) " +
            "VALUES (:p_did, :p_dtype, :p_out, :p_id, :p_atype, :p_ver, :p_person, :p_asg, :p_at, :p_corr, :p_note, :p_facts)");
        P(cmd, "p_did", OracleDbType.Varchar2, d.DecisionId);
        P(cmd, "p_dtype", OracleDbType.Varchar2, d.DecisionType);
        P(cmd, "p_out", OracleDbType.Varchar2, d.Outcome);
        P(cmd, "p_id", OracleDbType.Varchar2, d.AggregateId);
        P(cmd, "p_atype", OracleDbType.Varchar2, a.AggregateType);
        P(cmd, "p_ver", OracleDbType.Int64, d.EntityVersion);
        P(cmd, "p_person", OracleDbType.Varchar2, d.PersonId);
        P(cmd, "p_asg", OracleDbType.Varchar2, d.AssignmentId);
        P(cmd, "p_at", OracleDbType.TimeStampTZ, d.Timestamp);
        P(cmd, "p_corr", OracleDbType.Varchar2, d.CorrelationId);
        P(cmd, "p_note", OracleDbType.Varchar2, Nz(d.Note));
        P(cmd, "p_facts", OracleDbType.Clob, ToJson(d.Facts));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertAuditAsync(
        OracleConnection conn, OracleTransaction tx, AggregateSnapshot a, AuditEnvelope e, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, tx,
            "INSERT INTO EIMS_AUDIT_LOG (AUDIT_ID, PERSON_ID, NETWORK_IDENTITY, IDENTITY_SOURCE, ROLES_JSON, ASSIGNMENT_ID, AGGREGATE_ID, AGGREGATE_TYPE, ENTITY_VERSION, RULE_SET, OCCURRED_AT, CORRELATION_ID, COMMAND_NAME) " +
            "VALUES (:p_aid, :p_person, :p_net, :p_src, :p_roles, :p_asg, :p_id, :p_atype, :p_ver, :p_rule, :p_at, :p_corr, :p_cmd)");
        P(cmd, "p_aid", OracleDbType.Varchar2, e.AuditId);
        P(cmd, "p_person", OracleDbType.Varchar2, e.PersonId);
        P(cmd, "p_net", OracleDbType.Varchar2, e.NetworkIdentity);
        P(cmd, "p_src", OracleDbType.Varchar2, e.IdentitySource);
        P(cmd, "p_roles", OracleDbType.Clob, JsonSerializer.Serialize(e.Roles));
        P(cmd, "p_asg", OracleDbType.Varchar2, Nz(e.Assignment));
        P(cmd, "p_id", OracleDbType.Varchar2, e.AggregateId);
        P(cmd, "p_atype", OracleDbType.Varchar2, a.AggregateType);
        P(cmd, "p_ver", OracleDbType.Int64, e.EntityVersion);
        P(cmd, "p_rule", OracleDbType.Varchar2, Nz(e.RuleSet));
        P(cmd, "p_at", OracleDbType.TimeStampTZ, e.Timestamp);
        P(cmd, "p_corr", OracleDbType.Varchar2, e.CorrelationId);
        P(cmd, "p_cmd", OracleDbType.Varchar2, e.CommandName);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertOutboxAsync(
        OracleConnection conn, OracleTransaction tx, AggregateSnapshot a, OutboxEnvelope o, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, tx,
            "INSERT INTO EIMS_OUTBOX (MESSAGE_ID, EVENT_NAME, AGGREGATE_ID, AGGREGATE_TYPE, AGGREGATE_VERSION, CORRELATION_ID, OCCURRED_AT, PAYLOAD_JSON) " +
            "VALUES (:p_mid, :p_evt, :p_id, :p_atype, :p_ver, :p_corr, :p_at, :p_payload)");
        P(cmd, "p_mid", OracleDbType.Varchar2, o.MessageId);
        P(cmd, "p_evt", OracleDbType.Varchar2, o.EventName);
        P(cmd, "p_id", OracleDbType.Varchar2, o.AggregateId);
        P(cmd, "p_atype", OracleDbType.Varchar2, a.AggregateType);
        P(cmd, "p_ver", OracleDbType.Int64, o.AggregateVersion);
        P(cmd, "p_corr", OracleDbType.Varchar2, o.CorrelationId);
        P(cmd, "p_at", OracleDbType.TimeStampTZ, o.OccurredAt);
        P(cmd, "p_payload", OracleDbType.Clob, ToJson(o.Payload));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertIdempotencyAsync(
        OracleConnection conn, OracleTransaction tx, MutationRequest request, AuthorityResult result, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, tx,
            "INSERT INTO EIMS_IDEMPOTENCY (COMMAND_NAME, AGGREGATE_ID, IDEMPOTENCY_KEY, FINGERPRINT, RESULT_JSON) " +
            "VALUES (:p_cmd, :p_id, :p_key, :p_fp, :p_res)");
        P(cmd, "p_cmd", OracleDbType.Varchar2, request.Command.CommandName);
        P(cmd, "p_id", OracleDbType.Varchar2, request.Command.AggregateId);
        P(cmd, "p_key", OracleDbType.Varchar2, request.Command.IdempotencyKey);
        P(cmd, "p_fp", OracleDbType.Varchar2, request.IdempotencyFingerprint);
        P(cmd, "p_res", OracleDbType.Clob, JsonSerializer.Serialize(result));
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
