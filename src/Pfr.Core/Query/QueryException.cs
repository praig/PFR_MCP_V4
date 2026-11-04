namespace Pfr.Core.Query;

/// <summary>
/// A query that can't be run or failed: not a read-only statement, a mistake in the SQL, or too slow.
/// The message is written for the engineer (no SQLite internals).
/// </summary>
public sealed class QueryException(string message, Exception? innerException = null) : Exception(message, innerException);
