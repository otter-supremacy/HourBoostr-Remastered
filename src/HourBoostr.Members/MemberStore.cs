using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace HourBoostr.Members;

internal record Game(uint AppId, string Name);
internal record MemberView(string Id, string Email, long CreatedAt, long LastSeen, bool Banned,
    bool Linked, uint? Game, bool Requested, string State, long EstimatedSeconds);
internal record Work(string Id, string Ticket, string? ProtectedSession, uint? Game);
internal sealed class MemberStore : IDisposable
{
    private readonly SqliteConnection db;
    private readonly object gate = new();

    public MemberStore(string directory)
    {
        db = new(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "members.sqlite"), Pooling = false, ForeignKeys = true }.ToString());
        db.Open();
        Execute("""
            PRAGMA journal_mode=WAL;
            PRAGMA busy_timeout=5000;
            CREATE TABLE IF NOT EXISTS members (
                id TEXT PRIMARY KEY, subject TEXT UNIQUE NOT NULL, email TEXT NOT NULL,
                created INTEGER NOT NULL, seen INTEGER NOT NULL, banned INTEGER NOT NULL DEFAULT 0,
                steam TEXT UNIQUE, secret TEXT, games TEXT NOT NULL DEFAULT '[]', game INTEGER CHECK(game > 0),
                requested INTEGER NOT NULL DEFAULT 0, state TEXT NOT NULL DEFAULT 'unlinked', ticket TEXT,
                seconds INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS sessions (
                id TEXT PRIMARY KEY, member TEXT NOT NULL REFERENCES members(id), expires INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS active_sessions ON sessions(member);
            CREATE TABLE IF NOT EXISTS password_accounts (
                email TEXT PRIMARY KEY, member TEXT UNIQUE NOT NULL REFERENCES members(id),
                password TEXT NOT NULL, recovery TEXT NOT NULL,
                failures INTEGER NOT NULL DEFAULT 0, failed_at INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS member_chat (
                member TEXT PRIMARY KEY REFERENCES members(id), settings TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS member_ai_key (
                member TEXT NOT NULL REFERENCES members(id), model TEXT NOT NULL, secret TEXT NOT NULL,
                PRIMARY KEY(member,model)
            );
            CREATE TABLE IF NOT EXISTS member_ai_usage (
                member TEXT NOT NULL REFERENCES members(id), model TEXT NOT NULL, day INTEGER NOT NULL, count INTEGER NOT NULL,
                PRIMARY KEY(member,model)
            );
            CREATE TABLE IF NOT EXISTS member_appearance (
                member TEXT PRIMARY KEY REFERENCES members(id), settings TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS member_group_activity (
                member TEXT NOT NULL REFERENCES members(id), chat_group TEXT NOT NULL,
                sender TEXT NOT NULL, day INTEGER NOT NULL, opted INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(member,chat_group,sender,day)
            );
            CREATE TABLE IF NOT EXISTS member_group_requests (
                member TEXT PRIMARY KEY REFERENCES members(id), at INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS moderation (
                sequence INTEGER PRIMARY KEY, member TEXT NOT NULL REFERENCES members(id),
                device TEXT NOT NULL, action TEXT NOT NULL, at INTEGER NOT NULL
            );
            UPDATE members SET state=CASE WHEN steam IS NULL THEN 'unlinked' ELSE 'paused' END,
                ticket=NULL, requested=0 WHERE banned=0;
            """);
    }

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private const string Columns = "id,email,created,seen,banned,steam IS NOT NULL,game,requested,state,seconds";
    private SqliteTransaction? transaction;

    private SqliteCommand Command(string sql, params object?[] values)
    {
        var command = db.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        for (var i = 0; i < values.Length; i++)
            command.Parameters.AddWithValue("$" + i, values[i] ?? DBNull.Value);
        return command;
    }

    private int Execute(string sql, params object?[] values)
    {
        using var command = Command(sql, values);
        return command.ExecuteNonQuery();
    }

    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> read, params object?[] values)
    {
        using var command = Command(sql, values);
        using var rows = command.ExecuteReader();
        List<T> result = [];
        while (rows.Read()) result.Add(read(rows));
        return result;
    }

    private static MemberView Read(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetInt64(2),
        r.GetInt64(3), r.GetBoolean(4), r.GetBoolean(5), r.IsDBNull(6) ? null : (uint)r.GetInt64(6),
        r.GetBoolean(7), r.GetString(8), r.GetInt64(9));

    public (string Member, string Session)? SignIn(string subject, string email)
    {
        lock (gate)
        {
            return Transaction<(string Member, string Session)?>(() =>
            {
                Execute("INSERT OR IGNORE INTO members(id,subject,email,created,seen) VALUES($0,$1,$2,$3,$3)",
                    Guid.NewGuid().ToString("N"), subject, email, Now);
                var id = Query("SELECT id FROM members WHERE subject=$0 AND banned=0", r => r.GetString(0), subject).SingleOrDefault();
                if (id is null) return null;
                Execute("UPDATE members SET email=$1 WHERE id=$0", id, email);
                return StartSession(id);
            });
        }
    }

    private (string Member, string Session) StartSession(string id)
    {
        var now = Now;
        var session = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        Execute("UPDATE members SET seen=$1 WHERE id=$0", id, now);
        Execute("DELETE FROM sessions WHERE expires<=$0; DELETE FROM sessions WHERE member=$1 AND id NOT IN (SELECT id FROM sessions WHERE member=$1 ORDER BY rowid DESC LIMIT 4)",
            now, id);
        Execute("INSERT INTO sessions VALUES($0,$1,$2)", session, id, now + 43200);
        return (id, session);
    }

    public (string Member, string Session)? RegisterPassword(string email, string hash, string recovery)
    {
        lock (gate) return Transaction<(string Member, string Session)?>(() =>
        {
            if (Query("SELECT 1 FROM password_accounts WHERE email=$0", r => r.GetInt32(0), email).Count != 0) return null;
            var id = Guid.NewGuid().ToString("N");
            Execute("INSERT INTO members(id,subject,email,created,seen) VALUES($0,$1,$2,$3,$3)", id, "password:" + id, email, Now);
            Execute("INSERT INTO password_accounts(email,member,password,recovery) VALUES($0,$1,$2,$3)", email, id, hash, recovery);
            return StartSession(id);
        });
    }

    private const string ActivePassword = """
        FROM password_accounts JOIN members ON members.id=password_accounts.member
        WHERE password_accounts.email=$0 AND members.banned=0 AND (failures<8 OR failed_at<=$1-900)
        """;

    public string? PasswordHash(string email)
    {
        lock (gate) return Query("SELECT password " + ActivePassword, r => r.GetString(0), email, Now).SingleOrDefault();
    }

    public (string Member, string Session)? CompletePasswordLogin(string email, string checkedHash, bool verified)
    {
        lock (gate) return Transaction<(string Member, string Session)?>(() =>
        {
            var now = Now;
            var id = Query("SELECT member " + ActivePassword + " AND password=$2", r => r.GetString(0), email, now, checkedHash).SingleOrDefault();
            if (id is null) return null;
            if (!verified)
            {
                Execute("UPDATE password_accounts SET failures=CASE WHEN failed_at<=$1-900 THEN 1 ELSE failures+1 END,failed_at=$1 WHERE email=$0", email, now);
                return null;
            }
            Execute("UPDATE password_accounts SET failures=0,failed_at=0 WHERE email=$0", email);
            return StartSession(id);
        });
    }

    public (string Member, string Session)? RecoverPassword(string email, string codeHash, string newHash, string newRecovery)
    {
        lock (gate) return Transaction<(string Member, string Session)?>(() =>
        {
            var id = Query("""
                SELECT member FROM password_accounts JOIN members ON members.id=member
                WHERE password_accounts.email=$0 AND recovery=$1 AND members.banned=0
                """, r => r.GetString(0), email, codeHash).SingleOrDefault();
            if (id is null) return null;
            Execute("UPDATE password_accounts SET password=$1,recovery=$2,failures=0,failed_at=0 WHERE member=$0", id, newHash, newRecovery);
            Execute("DELETE FROM sessions WHERE member=$0", id);
            return StartSession(id);
        });
    }

    public bool Authorize(string id, string session)
    {
        lock (gate)
        {
            var allowed = Query("SELECT 1 FROM sessions JOIN members ON members.id=member WHERE sessions.id=$0 AND member=$1 AND expires>$2 AND banned=0",
                r => r.GetInt32(0), session, id, Now).Count > 0;
            if (allowed) Execute("UPDATE members SET seen=$1 WHERE id=$0 AND seen<$1-60", id, Now);
            return allowed;
        }
    }

    public void SignOut(string session)
    {
        lock (gate) Execute("DELETE FROM sessions WHERE id=$0", session);
    }

    public MemberView? Get(string id)
    {
        lock (gate) return Query("SELECT " + Columns + " FROM members WHERE id=$0 AND banned=0", Read, id).SingleOrDefault();
    }

    public List<MemberView> List(string after)
    {
        lock (gate) return Query("SELECT " + Columns + " FROM members WHERE id>$0 ORDER BY id LIMIT 26", Read, after);
    }

    public Game[] Library(string id)
    {
        lock (gate) return Query("SELECT games FROM members WHERE id=$0 AND banned=0",
            r => JsonSerializer.Deserialize<Game[]>(r.GetString(0)) ?? [], id).SingleOrDefault() ?? [];
    }

    public MemberAppearance? Appearance(string id)
    {
        lock (gate) return Query("""
            SELECT settings FROM member_appearance JOIN members ON members.id=member WHERE member=$0 AND banned=0
            """, r => JsonSerializer.Deserialize<MemberAppearance>(r.GetString(0)), id).SingleOrDefault();
    }

    public bool SaveAppearance(string id, MemberAppearance appearance)
    {
        lock (gate) return Execute("""
            INSERT INTO member_appearance(member,settings) SELECT id,$1 FROM members WHERE id=$0 AND banned=0
            ON CONFLICT(member) DO UPDATE SET settings=excluded.settings
            """, id, JsonSerializer.Serialize(appearance)) == 1;
    }

    public MemberChatSettings Chat(string id)
    {
        lock (gate) return Query("""
            SELECT settings FROM member_chat JOIN members ON member=members.id WHERE member=$0 AND banned=0
            """, r => JsonSerializer.Deserialize<MemberChatSettings>(r.GetString(0)) ?? new(), id).SingleOrDefault() ?? new();
    }

    public bool SaveChat(string id, MemberChatSettings settings)
    {
        lock (gate) return Execute("""
            INSERT INTO member_chat(member,settings) SELECT id,$1 FROM members WHERE id=$0 AND banned=0
            ON CONFLICT(member) DO UPDATE SET settings=excluded.settings
            """, id, JsonSerializer.Serialize(settings)) == 1;
    }

    public string? AiKey(string id, string model)
    {
        lock (gate) return Query("""
            SELECT k.secret FROM member_ai_key k JOIN members m ON k.member=m.id
            WHERE m.id=$0 AND m.banned=0 AND k.model=$1
            """, r => r.GetString(0), id, model).SingleOrDefault();
    }

    public bool SaveAiKey(string id, string model, string? ciphertext)
    {
        lock (gate) return ciphertext is null
            ? Execute("DELETE FROM member_ai_key WHERE member=$0 AND model=$1", id, model) >= 0
            : Execute("""
                INSERT INTO member_ai_key(member,model,secret) SELECT id,$1,$2 FROM members WHERE id=$0 AND banned=0
                ON CONFLICT(member,model) DO UPDATE SET secret=excluded.secret
                """, id, model, ciphertext) == 1;
    }

    public bool ReserveAi(string id, string model)
    {
        lock (gate) return Execute("""
            INSERT INTO member_ai_usage(member,model,day,count)
            SELECT m.id,$1,$2,1 FROM members m JOIN member_ai_key k ON k.member=m.id AND k.model=$1
            WHERE m.id=$0 AND m.banned=0
            ON CONFLICT(member,model) DO UPDATE SET day=excluded.day,
                count=CASE WHEN day=excluded.day THEN count+1 ELSE 1 END
            WHERE day<>excluded.day OR count<500
            """, id, model, Now / 86400) == 1;
    }

    public int AiUsed(string id, string model)
    {
        lock (gate) return Query("SELECT count FROM member_ai_usage WHERE member=$0 AND model=$1 AND day=$2",
            r => r.GetInt32(0), id, model, Now / 86400).SingleOrDefault();
    }

    public bool ChatActive(Work work)
    {
        lock (gate) return Query("SELECT 1 FROM members WHERE id=$0 AND ticket=$1 AND banned=0 AND requested=1",
            r => r.GetInt32(0), work.Id, work.Ticket).Count == 1;
    }

    public bool IsChatMember(ulong id)
    {
        lock (gate) return Query("SELECT 1 FROM members WHERE steam=$0 AND requested=1 AND banned=0",
            r => r.GetInt32(0), id.ToString(System.Globalization.CultureInfo.InvariantCulture)).Count == 1;
    }

    public void ObserveRegular(Work work, string group, ulong sender, bool opted, DateTimeOffset now)
    {
        lock (gate)
        {
            if (!ChatActive(work) || !Chat(work.Id).RegularGroups.Contains(group)) return;
            var day = now.ToUnixTimeSeconds() / 86400;
            var id = sender.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Execute("DELETE FROM member_group_activity WHERE member=$0 AND day<$1", work.Id, day - 13);
            Execute("""
                INSERT INTO member_group_activity(member,chat_group,sender,day,opted)
                SELECT $0,$1,$2,$3,$4
                WHERE (SELECT count(*) FROM member_group_activity WHERE member=$0)<4096
                   OR EXISTS(SELECT 1 FROM member_group_activity WHERE member=$0 AND chat_group=$1 AND sender=$2 AND day=$3)
                ON CONFLICT DO UPDATE SET opted=max(opted,excluded.opted)
                """, work.Id, group, id, day, opted);
        }
    }

    public ChatRegular[] Regulars(string id)
    {
        lock (gate)
        {
            var selected = Chat(id).RegularGroups.ToHashSet();
            return Query("""
                SELECT sender,chat_group,count(*) FROM member_group_activity
                WHERE member=$0 AND day>=$1 GROUP BY sender,chat_group
                HAVING count(*)>=3 AND max(opted)=1 ORDER BY count(*) DESC LIMIT 128
                """, r => new ChatRegular(r.GetString(0), r.GetString(1), r.GetInt32(2)), id, Now / 86400 - 13)
                .Where(row => selected.Contains(row.GroupId)).Take(30).ToArray();
        }
    }

    public bool ReserveRegularRequest(Work work, ulong sender, IReadOnlySet<string> joined)
    {
        lock (gate) return Transaction(() =>
        {
            var id = sender.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!ChatActive(work) || !Regulars(work.Id).Any(row => row.SteamId == id && joined.Contains(row.GroupId))) return false;
            if (Execute("""
                INSERT INTO member_group_requests(member,at) VALUES($0,$1)
                ON CONFLICT(member) DO UPDATE SET at=excluded.at WHERE at<=$1-86400
                """, work.Id, Now) != 1) return false;
            Execute("DELETE FROM member_group_activity WHERE member=$0 AND sender=$1", work.Id, id);
            return true;
        });
    }

    public bool BeginLink(string id, string ticket)
    {
        lock (gate) return Execute("UPDATE members SET ticket=$1,state='pairing' WHERE id=$0 AND banned=0 AND steam IS NULL", id, ticket) == 1;
    }

    public bool CompleteLink(string id, string ticket, string steamId, string? secret, Game[] games)
    {
        lock (gate) return Execute("""
            UPDATE members SET steam=$2,secret=$3,games=$4,state='paused',ticket=NULL
            WHERE id=$0 AND banned=0 AND ticket=$1 AND NOT EXISTS(SELECT 1 FROM members WHERE steam=$2)
            """, id, ticket, steamId, secret, JsonSerializer.Serialize(games)) == 1;
    }

    public void FailLink(string id, string ticket)
    {
        lock (gate) Execute("UPDATE members SET state='pairing_failed',ticket=NULL WHERE id=$0 AND ticket=$1 AND banned=0", id, ticket);
    }

    public bool Select(string id, uint appId)
    {
        lock (gate)
        {
            if (!Library(id).Any(game => game.AppId == appId)) return false;
            return Execute("UPDATE members SET game=$1,state='paused' WHERE id=$0 AND banned=0 AND requested=0 AND steam IS NOT NULL", id, appId) == 1;
        }
    }

    public Work? Start(string id, int capacity)
    {
        lock (gate)
        {
            var ticket = Guid.NewGuid().ToString("N");
            if (Execute("""
                UPDATE members SET requested=1,state='connecting',ticket=$1
                WHERE id=$0 AND banned=0 AND steam IS NOT NULL AND game IS NOT NULL AND requested=0
                AND (SELECT count(*) FROM members WHERE requested=1) < $2
                """, id, ticket, capacity) != 1) return null;
            return Query("SELECT secret,game FROM members WHERE id=$0",
                r => new Work(id, ticket, r.IsDBNull(0) ? null : r.GetString(0), (uint)r.GetInt64(1)), id).Single();
        }
    }

    public bool WorkerState(Work work, string state, int seconds = 0)
    {
        lock (gate) return Execute("UPDATE members SET state=$2,seconds=seconds+$3 WHERE id=$0 AND ticket=$1 AND requested=1 AND banned=0",
            work.Id, work.Ticket, state, seconds) == 1;
    }

    public void WorkerEnded(Work work, string state)
    {
        lock (gate) Execute("UPDATE members SET requested=0,ticket=NULL,state=$2 WHERE id=$0 AND ticket=$1 AND banned=0",
            work.Id, work.Ticket, state);
    }

    public void Stop(string id, bool unlink)
    {
        lock (gate)
        {
            var sql = unlink
                ? "UPDATE members SET ticket=NULL,requested=0,steam=NULL,secret=NULL,games='[]',game=NULL,state='unlinked' WHERE id=$0 AND banned=0"
                : "UPDATE members SET ticket=NULL,requested=0,state='paused' WHERE id=$0 AND banned=0 AND steam IS NOT NULL";
            Transaction(() =>
            {
                if (Execute(sql, id) == 1 && unlink) ClearChat(id);
                return true;
            });
        }
    }

    public bool Moderate(string id, bool ban, string device)
    {
        lock (gate)
        {
            return Transaction(() =>
            {
            var changed = Execute("""
                UPDATE members SET banned=$1,requested=0,ticket=NULL,steam=NULL,secret=NULL,games='[]',game=NULL,state=$2
                WHERE id=$0 AND banned<>$1
                """, id, ban, ban ? "disconnecting" : "unlinked");
            if (changed == 0) return Query("SELECT 1 FROM members WHERE id=$0", r => r.GetInt32(0), id).Count > 0;
            ClearChat(id);
            Execute("DELETE FROM sessions WHERE member=$0; DELETE FROM member_ai_key WHERE member=$0; INSERT INTO moderation(member,device,action,at) VALUES($0,$1,$2,$3)",
                id, device, ban ? "ban" : "unban", Now);
            return true;
            });
        }
    }

    private void ClearChat(string id) => Execute("""
        DELETE FROM member_chat WHERE member=$0;
        DELETE FROM member_group_activity WHERE member=$0;
        DELETE FROM member_group_requests WHERE member=$0;
        """, id);

    private T Transaction<T>(Func<T> execute)
    {
        using var active = db.BeginTransaction();
        transaction = active;
        try
        {
            var result = execute();
            active.Commit();
            return result;
        }
        finally { transaction = null; }
    }

    public void ConfirmBanned(string id)
    {
        lock (gate) Execute("UPDATE members SET state='banned' WHERE id=$0 AND banned=1", id);
    }

    public void Backup(string destination)
    {
        lock (gate)
        {
            using var copy = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination, Pooling = false }.ToString());
            copy.Open();
            db.BackupDatabase(copy);
            using var check = copy.CreateCommand();
            check.CommandText = "PRAGMA integrity_check";
            if (!Equals(check.ExecuteScalar(), "ok")) throw new IOException("Member backup integrity check failed.");
        }
    }

    public void Dispose() => db.Dispose();
}
