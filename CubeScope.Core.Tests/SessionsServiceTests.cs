using System.Data;
using CubeScope.Core.Ssas;

namespace CubeScope.Core.Tests;

public class SessionsServiceTests
{
    // Column types as observed on SSAS 2022: sessions in UInt64, commands in Int64.
    private static DataTable Sessions(params (int Spid, string Id, ulong CpuMs)[] rows)
    {
        var t = new DataTable();
        t.Columns.Add("SESSION_SPID", typeof(int));
        t.Columns.Add("SESSION_ID", typeof(string));
        t.Columns.Add("SESSION_USER_NAME", typeof(string));
        t.Columns.Add("SESSION_CPU_TIME_MS", typeof(ulong));
        foreach (var (spid, id, cpu) in rows) t.Rows.Add(spid, id, "DOM\\user", cpu);
        return t;
    }

    private static DataTable Commands(params (int Spid, string Text, long ElapsedMs)[] rows)
    {
        var t = new DataTable();
        t.Columns.Add("SESSION_SPID", typeof(int));
        t.Columns.Add("COMMAND_TEXT", typeof(string));
        t.Columns.Add("COMMAND_ELAPSED_TIME_MS", typeof(long));
        foreach (var (spid, text, ms) in rows) t.Rows.Add(spid, text, ms);
        return t;
    }

    [Fact]
    public void Merge_KeepsTheLongestCommandPerSpid()
    {
        var list = SessionsService.Merge(
            Sessions((10, "A", 5)),
            Commands((10, "short", 100), (10, "long", 9000)),
            mine: null);

        var s = Assert.Single(list);
        Assert.Equal("long", s.CommandText);
        Assert.Equal(9000, s.CommandElapsedMs);
    }

    [Fact]
    public void Merge_SortsByCommandDurationThenCpu_AndSessionWithoutCommandHasNone()
    {
        var list = SessionsService.Merge(
            Sessions((1, "idle", 50), (2, "busy", 1), (3, "idle-cpu", 900)),
            Commands((2, "SELECT", 500)),
            mine: null);

        Assert.Equal([2, 3, 1], list.Select(s => s.Spid));
        Assert.Null(list[1].CommandText);
        Assert.Equal(0, list[1].CommandElapsedMs);
        Assert.Equal(900, list[1].CpuMs); // UInt64 converted, not cast
    }

    [Fact]
    public void Merge_FlagsOwnSession_CaseInsensitive()
    {
        var list = SessionsService.Merge(
            Sessions((1, "ABC-123", 0), (2, "other", 0)), Commands(), mine: "abc-123");

        Assert.True(list.Single(s => s.Spid == 1).IsMine);
        Assert.False(list.Single(s => s.Spid == 2).IsMine);
    }

    [Fact]
    public void Merge_LeavesOutTheTransientReaderSession()
    {
        // The listing goes through a throwaway connection: its session would show up as a
        // phantom entry that no longer exists by the time the user sees it.
        var list = SessionsService.Merge(
            Sessions((1, "work", 0), (2, "reader", 0)), Commands(), mine: "work", reader: "READER");

        Assert.Equal([1], list.Select(s => s.Spid));
    }

    [Fact]
    public void Merge_MissingColumns_DefaultInsteadOfThrowing()
    {
        var sessions = new DataTable();
        sessions.Columns.Add("SESSION_SPID", typeof(int));
        sessions.Rows.Add(7);

        var s = Assert.Single(SessionsService.Merge(sessions, Commands(), mine: null));
        Assert.Equal(7, s.Spid);
        Assert.Equal("", s.SessionId);
        Assert.Equal(default, s.StartTime);
    }

    [Fact]
    public void BuildCancelXmla_TargetsTheSpidAndItsCommands()
    {
        string xmla = SessionsService.BuildCancelXmla(4242);

        Assert.Contains("<Cancel xmlns=\"http://schemas.microsoft.com/analysisservices/2003/engine\">", xmla);
        Assert.Contains("<SPID>4242</SPID>", xmla);
        Assert.Contains("<CancelAssociated>1</CancelAssociated>", xmla);
    }
}
