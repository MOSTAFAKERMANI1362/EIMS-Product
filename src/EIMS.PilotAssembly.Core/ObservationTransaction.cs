namespace EIMS.PilotAssembly.Core;

public enum ObservationTransactionState
{
    Active,
    Committed,
    RolledBack
}

public interface IObservationTransaction
{
    T Execute<T>(Func<T> operation);
}

public sealed class ObservationTransaction : IObservationTransaction
{
    public T Execute<T>(Func<T> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        try
        {
            return operation();
        }
        catch
        {
            throw;
        }
    }
}
