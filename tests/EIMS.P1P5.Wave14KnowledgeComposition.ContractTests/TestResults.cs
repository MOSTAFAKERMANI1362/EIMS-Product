global using static TestResults;

using EIMS.Authority.Recovery;

internal static class TestResults
{
    public static AuthorityResult Success(string correlationId, long? version) =>
        new(200, "OK", true, true, false, version, correlationId, Array.Empty<string>());
}
