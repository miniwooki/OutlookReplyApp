using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;
using TechSupportReply.Rag.Embedding;
using TechSupportReply.Rag.Search;

namespace TechSupportReply.Rag.Store
{
    /// <summary>
    /// 제품 1개의 색인(SQLite 파일 1개). 청크 본문, 벡터 BLOB, FTS5 용어 색인을 담는다.
    /// 파일 복사·교체 시 잠금이 남지 않도록 연결 풀링을 끈다.
    /// </summary>
    public sealed class SqliteIndexStore : IDisposable
    {
        private const string Schema = @"
CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT);
CREATE TABLE IF NOT EXISTS files(id INTEGER PRIMARY KEY, path TEXT NOT NULL UNIQUE COLLATE NOCASE, size INTEGER NOT NULL, mtime_ticks INTEGER NOT NULL, hash TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS chunks(id INTEGER PRIMARY KEY, file_id INTEGER NOT NULL, doc_type INTEGER NOT NULL, title TEXT, page INTEGER, text TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS ix_chunks_file ON chunks(file_id);
CREATE TABLE IF NOT EXISTS vectors(chunk_id INTEGER PRIMARY KEY, blob BLOB NOT NULL);
CREATE VIRTUAL TABLE IF NOT EXISTS chunks_fts USING fts5(terms, tokenize = ""unicode61 remove_diacritics 0 tokenchars '_'"");";

        private readonly SqliteConnection _connection;
        private SqliteTransaction _tx;

        private SqliteIndexStore(string path, bool readOnly)
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            };
            _connection = new SqliteConnection(builder.ToString());
            _connection.Open();
        }

        /// <summary>색인 파일을 읽기/쓰기로 연다(없으면 만든다).</summary>
        public static SqliteIndexStore Create(string path)
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)));
            var store = new SqliteIndexStore(path, false);
            store.Execute(Schema);
            return store;
        }

        public static SqliteIndexStore OpenReadOnly(string path) => new SqliteIndexStore(path, true);

        public int ChunkCount => Convert.ToInt32(Scalar("SELECT COUNT(*) FROM chunks"));

        public string GetMeta(string key) => Scalar("SELECT value FROM meta WHERE key = $k", ("$k", key)) as string;

        public void SetMeta(string key, string value) =>
            Execute("INSERT INTO meta(key, value) VALUES($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value", ("$k", key), ("$v", value));

        public IReadOnlyList<IndexedFile> GetFiles()
        {
            var files = new List<IndexedFile>();
            using (var cmd = Command("SELECT id, path, size, mtime_ticks, hash FROM files ORDER BY path"))
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    files.Add(new IndexedFile
                    {
                        Id = r.GetInt64(0),
                        RelativePath = r.GetString(1),
                        Size = r.GetInt64(2),
                        MtimeUtc = new DateTime(r.GetInt64(3), DateTimeKind.Utc),
                        Hash = r.GetString(4),
                    });
                }
            }
            return files;
        }

        public void UpsertFile(string relativePath, long size, DateTime mtimeUtc, string hash, IReadOnlyList<NewChunk> chunks)
        {
            using (var tx = Begin())
            {
                var existing = Scalar("SELECT id FROM files WHERE path = $p", ("$p", relativePath));
                long fileId;
                if (existing != null)
                {
                    fileId = Convert.ToInt64(existing);
                    DeleteChunksOf(fileId);
                    Execute("UPDATE files SET size = $s, mtime_ticks = $m, hash = $h WHERE id = $id",
                        ("$s", size), ("$m", mtimeUtc.ToUniversalTime().Ticks), ("$h", hash), ("$id", fileId));
                }
                else
                {
                    fileId = Convert.ToInt64(Scalar(
                        "INSERT INTO files(path, size, mtime_ticks, hash) VALUES($p, $s, $m, $h); SELECT last_insert_rowid();",
                        ("$p", relativePath), ("$s", size), ("$m", mtimeUtc.ToUniversalTime().Ticks), ("$h", hash)));
                }

                foreach (var c in chunks)
                {
                    var chunkId = Convert.ToInt64(Scalar(
                        "INSERT INTO chunks(file_id, doc_type, title, page, text) VALUES($f, $t, $ti, $pg, $tx); SELECT last_insert_rowid();",
                        ("$f", fileId), ("$t", (int)c.DocType), ("$ti", c.Title ?? ""), ("$pg", (object)c.Page ?? DBNull.Value), ("$tx", c.Text ?? "")));
                    if (c.Vector != null)
                        Execute("INSERT INTO vectors(chunk_id, blob) VALUES($id, $b)", ("$id", chunkId), ("$b", VectorMath.ToBytes(c.Vector)));
                    Execute("INSERT INTO chunks_fts(rowid, terms) VALUES($id, $terms)",
                        ("$id", chunkId), ("$terms", SearchTextNormalizer.ToIndexText((c.Title ?? "") + "\n" + (c.Text ?? ""))));
                }
                tx.Commit();
                _tx = null;
            }
        }

        public void TouchFile(string relativePath, long size, DateTime mtimeUtc) =>
            Execute("UPDATE files SET size = $s, mtime_ticks = $m WHERE path = $p",
                ("$s", size), ("$m", mtimeUtc.ToUniversalTime().Ticks), ("$p", relativePath));

        public void RemoveFile(string relativePath)
        {
            using (var tx = Begin())
            {
                var id = Scalar("SELECT id FROM files WHERE path = $p", ("$p", relativePath));
                if (id != null)
                {
                    DeleteChunksOf(Convert.ToInt64(id));
                    Execute("DELETE FROM files WHERE id = $id", ("$id", Convert.ToInt64(id)));
                }
                tx.Commit();
                _tx = null;
            }
        }

        public IReadOnlyList<StoredChunk> SearchKeyword(string text, int topK, DocType? type = null)
        {
            var match = SearchTextNormalizer.ToMatchQuery(text);
            if (match == null || topK <= 0) return new List<StoredChunk>();
            var sql = @"SELECT c.id, f.path, c.doc_type, c.title, c.page, c.text, -bm25(chunks_fts) AS score
FROM chunks_fts JOIN chunks c ON c.id = chunks_fts.rowid JOIN files f ON f.id = c.file_id
WHERE chunks_fts MATCH $q" + (type.HasValue ? " AND c.doc_type = $t" : "") + @"
ORDER BY bm25(chunks_fts) LIMIT $k";
            using (var cmd = Command(sql, ("$q", match), ("$k", topK)))
            {
                if (type.HasValue) cmd.Parameters.AddWithValue("$t", (int)type.Value);
                return ReadChunks(cmd, true);
            }
        }

        public IReadOnlyList<KeyValuePair<long, float[]>> LoadVectors(DocType? type = null)
        {
            var sql = "SELECT v.chunk_id, v.blob FROM vectors v JOIN chunks c ON c.id = v.chunk_id"
                      + (type.HasValue ? " WHERE c.doc_type = $t" : "") + " ORDER BY v.chunk_id";
            var result = new List<KeyValuePair<long, float[]>>();
            using (var cmd = Command(sql))
            {
                if (type.HasValue) cmd.Parameters.AddWithValue("$t", (int)type.Value);
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                        result.Add(new KeyValuePair<long, float[]>(r.GetInt64(0), VectorMath.FromBytes((byte[])r.GetValue(1))));
            }
            return result;
        }

        /// <summary>id 순서대로 청크를 반환한다(없는 id는 건너뜀).</summary>
        public IReadOnlyList<StoredChunk> GetChunks(IEnumerable<long> ids)
        {
            var list = ids.ToList();
            if (list.Count == 0) return new List<StoredChunk>();
            var sql = "SELECT c.id, f.path, c.doc_type, c.title, c.page, c.text FROM chunks c JOIN files f ON f.id = c.file_id WHERE c.id IN ("
                      + string.Join(",", list) + ")";
            using (var cmd = Command(sql))
            {
                var byId = ReadChunks(cmd, false).ToDictionary(c => c.Id);
                return list.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
            }
        }

        public void Dispose() => _connection.Dispose();

        private SqliteTransaction Begin() => _tx = _connection.BeginTransaction();

        private void DeleteChunksOf(long fileId)
        {
            Execute("DELETE FROM chunks_fts WHERE rowid IN (SELECT id FROM chunks WHERE file_id = $f)", ("$f", fileId));
            Execute("DELETE FROM vectors WHERE chunk_id IN (SELECT id FROM chunks WHERE file_id = $f)", ("$f", fileId));
            Execute("DELETE FROM chunks WHERE file_id = $f", ("$f", fileId));
        }

        private static List<StoredChunk> ReadChunks(SqliteCommand cmd, bool hasScore)
        {
            var result = new List<StoredChunk>();
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    result.Add(new StoredChunk
                    {
                        Id = r.GetInt64(0),
                        RelativePath = r.GetString(1),
                        DocType = (DocType)r.GetInt32(2),
                        Title = r.IsDBNull(3) ? "" : r.GetString(3),
                        Page = r.IsDBNull(4) ? (int?)null : r.GetInt32(4),
                        Text = r.GetString(5),
                        Score = hasScore ? r.GetDouble(6) : 0,
                    });
                }
            }
            return result;
        }

        private SqliteCommand Command(string sql, params (string Name, object Value)[] parameters)
        {
            var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.Transaction = _tx;
            foreach (var p in parameters) cmd.Parameters.AddWithValue(p.Name, p.Value ?? DBNull.Value);
            return cmd;
        }

        private void Execute(string sql, params (string Name, object Value)[] parameters)
        {
            using (var cmd = Command(sql, parameters)) cmd.ExecuteNonQuery();
        }

        private object Scalar(string sql, params (string Name, object Value)[] parameters)
        {
            using (var cmd = Command(sql, parameters))
            {
                var value = cmd.ExecuteScalar();
                return value is DBNull ? null : value;
            }
        }
    }
}
