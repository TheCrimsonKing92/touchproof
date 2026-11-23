using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

// Fixture-only SQLite instrumentation. ROW counts results crossing the SQLite
// API boundary, and STMT separates top-level executions from trigger subprograms.
internal sealed class BoardFillSnapshotSqlTrace(Action? snapshotEstablished = null)
{
    private const uint Statement = 1;
    private const uint Row = 4;
    private const uint Close = 8;
    private static readonly Callback Trace = Observe;
    private readonly Action? _snapshotEstablished = snapshotEstablished;
    internal int Statements { get; private set; }
    internal int ImmediateTransactions { get; private set; }
    internal int TriggerSteps { get; private set; }
    internal int Rows { get; private set; }
    internal Exception? Failure { get; private set; }

    internal void Attach(string path, SqliteConnection connection)
    {
        var state = GCHandle.Alloc(this);
        if (TraceV2(connection.Handle!, Statement | Row | Close, Trace, GCHandle.ToIntPtr(state)) != 0)
        {
            state.Free();
            throw new InvalidOperationException($"Could not attach SQLite measurement to {path}");
        }
    }

    private static int Observe(uint kind, nint context, nint statement, nint argument)
    {
        var state = GCHandle.FromIntPtr(context);
        if (kind == Close)
        {
            state.Free();
            return 0;
        }

        var trace = (BoardFillSnapshotSqlTrace)state.Target!;
        try
        {
            if (kind == Statement)
            {
                var sql = Marshal.PtrToStringUTF8(argument);
                if (sql?.StartsWith("--", StringComparison.Ordinal) == true)
                    trace.TriggerSteps++;
                else
                {
                    trace.Statements++;
                    if (sql == "BEGIN IMMEDIATE")
                        trace.ImmediateTransactions++;
                }
            }
            else if (kind == Row)
            {
                trace.Rows++;
                if (trace._snapshotEstablished is not null &&
                    Marshal.PtrToStringUTF8(Sql(statement))?.StartsWith("SELECT store_id, revision", StringComparison.Ordinal) == true)
                    trace._snapshotEstablished();
            }
        }
        catch (Exception exception)
        {
            trace.Failure = exception;
        }
        return 0;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Callback(uint kind, nint context, nint statement, nint argument);

    [DllImport("e_sqlite3", EntryPoint = "sqlite3_trace_v2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int TraceV2(SQLitePCL.sqlite3 database, uint mask, Callback callback, nint context);

    [DllImport("e_sqlite3", EntryPoint = "sqlite3_sql", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint Sql(nint statement);
}
