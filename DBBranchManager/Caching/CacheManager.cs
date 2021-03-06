using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DBBranchManager.Entities.Config;
using DBBranchManager.Logging;
using DBBranchManager.Utils;
using DBBranchManager.Utils.Sql;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DBBranchManager.Caching
{
    internal class CacheManager : ICacheManager
    {
        private readonly string mPath;
        private readonly bool mAllowCompression;
        private readonly long mMaxCacheSize;
        private readonly bool mAutoGC;
        private readonly bool mDryRun;
        private readonly ILog mLog;

        public CacheManager(string path, bool allowCompression, long maxCacheSize, bool autoGC, bool dryRun, ILog log)
        {
            mPath = path;
            mAllowCompression = allowCompression;
            mMaxCacheSize = maxCacheSize;
            mAutoGC = autoGC;
            mDryRun = dryRun;
            mLog = log;
        }

        public bool TryGet(string dbName, StateHash hash, bool updateHit, out string path)
        {
            path = null;

            var root = GetCachesDir(false);
            if (!root.Exists)
                return false;

            var db = new DirectoryInfo(Path.Combine(root.FullName, dbName));
            if (!db.Exists)
                return false;

            var file = new FileInfo(Path.Combine(db.FullName, GetFileName(hash)));
            if (!file.Exists)
                return false;

            if (updateHit)
                UpdateHit(dbName, hash);

            path = file.FullName;

            return true;
        }

        public void Add(DatabaseConnectionConfig dbConfig, string dbName, StateHash hash)
        {
            var db = GetDatabaseDir(dbName, true);
            var file = Path.Combine(db.FullName, GetFileName(hash));

            if (File.Exists(file))
                return;

            if (mAutoGC)
                GarbageCollect(true);

            var exitCode = 0;
            mLog.LogFormat("Caching {0} to {1}", dbName, file);

            if (!mDryRun)
            {
                using (var process = SqlUtils.BackupDatabase(dbConfig, dbName, file, mAllowCompression))
                using (mLog.IndentScope())
                {
                    foreach (var line in process.GetOutput())
                    {
                        mLog.Log(line.Line);
                    }

                    exitCode = process.ExitCode;
                }
            }

            if (exitCode != 0)
            {
                mLog.LogFormat("WARNING: Error caching {0}. Continuing anyways.", dbName);
                File.Delete(file);
            }
            else
            {
                UpdateHit(dbName, hash);
            }
        }

        public void UpdateHits(IEnumerable<Tuple<string, StateHash>> keys)
        {
            if (mDryRun)
                return;

            var root = new DirectoryInfo(mPath);
            root.Create();

            using (var fs = AcquireHitTableFile())
            {
                var jRoot = ReadHitTableFile(fs);

                foreach (var key in keys)
                {
                    var db = jRoot[key.Item1] ?? new JObject();
                    db[GetFileName(key.Item2)] = DateTime.UtcNow.Ticks;

                    jRoot[key.Item1] = db;
                }

                WriteHitTableFile(fs, jRoot);
            }
        }

        public void GarbageCollect(bool silent)
        {
            if (!silent)
                mLog.Log("Running Cache Garbage Collection");

            using (var fs = AcquireHitTableFile())
            {
                var hitTable = ReadHitTableFile(fs);
                var stats = Stat(hitTable);

                var left = new List<Tuple<StatEntry, long>>();
                var totalLength = 0L;
                foreach (var stat in stats)
                {
                    if (stat.File != null && (stat.Hash == null || stat.LastHit == null))
                    {
                        if (!silent)
                            mLog.LogFormat("Deleting {0}", stat.File);
                        if (!mDryRun)
                            File.Delete(stat.File);
                    }
                    else if (stat.File == null && stat.Hash != null)
                    {
                        var hex = stat.Hash.ToHexString();
                        if (!silent)
                            mLog.LogFormat("Forgetting {0} -> {1}", stat.Database, hex);
                        ((JObject)hitTable[stat.Database]).Remove(hex);
                    }
                    else if (stat.File != null && stat.Hash != null && stat.LastHit != null)
                    {
                        var file = new FileInfo(stat.File);
                        left.Add(Tuple.Create(stat, file.Length));
                        totalLength += file.Length;
                    }
                }

                if (mMaxCacheSize >= 0)
                {
                    var it = left.OrderBy(x => x.Item1.LastHit).GetEnumerator();
                    while (totalLength > mMaxCacheSize && it.MoveNext())
                    {
                        var item = it.Current;
                        var stat = item.Item1;

                        if (!silent)
                            mLog.LogFormat("Erasing {0}", stat.File);
                        if (!mDryRun)
                            File.Delete(stat.File);
                        ((JObject)hitTable[stat.Database]).Remove(stat.Hash.ToHexString());

                        totalLength -= item.Item2;
                    }
                }

                WriteHitTableFile(fs, hitTable);
            }
        }

        private static JObject ReadHitTableFile(FileStream fs)
        {
            JObject jRoot;

            if (fs.Length == 0)
            {
                jRoot = new JObject();
            }
            else
            {
                var reader = new StreamReader(fs);
                var jReader = new JsonTextReader(reader);
                jRoot = JObject.Load(jReader);
            }

            return jRoot;
        }

        private void WriteHitTableFile(FileStream fs, JObject jRoot)
        {
            if (mDryRun)
                return;

            fs.Seek(0, SeekOrigin.Begin);
            fs.SetLength(0);

            var writer = new StreamWriter(fs);
            var jWriter = new JsonTextWriter(writer)
            {
                Formatting = Formatting.Indented,
                IndentChar = ' ',
                Indentation = 2
            };
            jRoot.WriteTo(jWriter);

            jWriter.Flush();
        }


        private IEnumerable<StatEntry> Stat(JObject hitTable)
        {
            var table = hitTable
                .OfType<JProperty>()
                .Select(x => new
                {
                    Database = x.Name,
                    Hashes = ((JObject)x.Value).OfType<JProperty>()
                        .Select(y => new
                        {
                            Hash = GetStateHash(y.Name),
                            LastHitTime = new DateTime((long)y.Value)
                        })
                }).SelectMany(x => x.Hashes, (d, h) => new
                {
                    d.Database,
                    h.Hash,
                    h.LastHitTime
                });
            var root = GetCachesDir(false);
            var dirs = root.Exists ? root.EnumerateDirectories() : Enumerable.Empty<DirectoryInfo>();
            var files = dirs
                .Select(x => new
                {
                    Database = x.Name,
                    Files = x.EnumerateFiles()
                }).SelectMany(x => x.Files, (d, f) => new
                {
                    d.Database,
                    Hash = GetStateHash(f.Name),
                    FilePath = f.FullName
                });

            var lookTable = table.ToLookup(x => new { x.Database, x.Hash });
            var lookFiles = files.ToLookup(x => new { x.Database, x.Hash });

            var keys = lookTable.ToHashSet(x => x.Key);
            keys.UnionWith(lookFiles.Select(x => x.Key));

            return
                from key in keys
                let tLook = lookTable[key]
                let fLook = lookFiles[key]
                from t in tLook.DefaultIfEmpty()
                from f in fLook.DefaultIfEmpty()
                select new StatEntry(key.Database, key.Hash, f == null ? null : f.FilePath, t == null ? (DateTime?)null : t.LastHitTime);
        }

        private FileStream AcquireHitTableFile()
        {
            return FileUtils.AcquireFile(Path.Combine(mPath, "hit.json"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }


        private DirectoryInfo GetCachesDir(bool create)
        {
            var dir = new DirectoryInfo(Path.Combine(mPath, "caches"));

            if (create && !mDryRun)
                dir.Create();

            return dir;
        }

        private DirectoryInfo GetDatabaseDir(string dbName, bool create)
        {
            var root = GetCachesDir(create);
            return create ? root.CreateSubdirectory(dbName) : new DirectoryInfo(Path.Combine(root.FullName, dbName));
        }

        private void UpdateHit(string dbName, StateHash hash)
        {
            if (mDryRun)
                return;

            UpdateHits(Enumerable.Repeat(Tuple.Create(dbName, hash), 1));
        }

        private static string GetFileName(StateHash hash)
        {
            return hash.ToHexString();
        }

        private static StateHash GetStateHash(string hexString)
        {
            StateHash hash;
            StateHash.TryFromHexString(hexString, out hash);
            return hash;
        }

        private class StatEntry
        {
            private readonly string mDatabase;
            private readonly StateHash mHash;
            private readonly string mFile;
            private readonly DateTime? mLastHit;

            public StatEntry(string database, StateHash hash, string file, DateTime? lastHit)
            {
                mDatabase = database;
                mHash = hash;
                mFile = file;
                mLastHit = lastHit;
            }

            public string Database
            {
                get { return mDatabase; }
            }

            public StateHash Hash
            {
                get { return mHash; }
            }

            public string File
            {
                get { return mFile; }
            }

            public DateTime? LastHit
            {
                get { return mLastHit; }
            }
        }
    }
}
