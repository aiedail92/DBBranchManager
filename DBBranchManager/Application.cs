using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using DBBranchManager.Caching;
using DBBranchManager.Constants;
using DBBranchManager.Entities;
using DBBranchManager.Entities.Config;
using DBBranchManager.Exceptions;
using DBBranchManager.Logging;
using DBBranchManager.Tasks;
using DBBranchManager.Utils;

namespace DBBranchManager
{
    internal class Application
    {
        private readonly CommandLineArguments mCommandLine;
        private readonly string mProjectRoot;
        private readonly UserConfig mUserConfig;
        private readonly ProjectConfig mProjectConfig;


        public Application(string[] args)
        {
            mCommandLine = CommandLineArguments.Parse(args);
            mProjectRoot = DiscoverProjectRoot();
            mProjectConfig = ProjectConfig.LoadFromJson(Path.Combine(mProjectRoot, FileConstants.ProjectFileName));
            mUserConfig = UserConfig.LoadFromJson(Path.Combine(mProjectRoot, FileConstants.UserFileName));
        }

        public int Run()
        {
            switch (mCommandLine.Command)
            {
                case CommandConstants.Help:
                    return RunHelp();

                case CommandConstants.Deploy:
                    return RunDeploy();

                default:
                    throw new SoftFailureException(string.Format("Unknown command: {0}", mCommandLine.Command));
            }
        }

        private int RunHelp()
        {
            return 0;
        }

        private int RunDeploy()
        {
            Beep("start");

            var context = CreateRunContext();
            var plan = BuildActionPlan(context);

            var root = new ExecutionNode("Begin deploy", "Deploy completed");
            root.AddChild(BuildRestoreDatabasesNode(plan.Databases, context));

            foreach (var release in plan.Releases)
            {
                var releaseNode = BuildReleaseNode(release, context);
                root.AddChild(releaseNode);
            }

            var hash = StateHash.Empty;
            try
            {
                var cacheManager = context.UseCache ? (ICacheManager)new CacheManager(context.UserConfig.Cache.RootPath, true, context.Log) : new NullCacheManager();

                StateHash startingHash = null;
                if (context.CommandLine.Resume)
                {
                    startingHash = LoadResumeHash(context);
                }

                var improved = root.Calculate(context, hash, startingHash, cacheManager);
                if (improved.Item3)
                    root = improved.Item1;

                root.Run(context, startingHash ?? hash, cacheManager);

                CleanResumeHash(context);
            }
            catch (SoftFailureException ex)
            {
                context.Log.LogFormat("Blocking error detected: {0}", ex.Message);

                Beep("error");
                return 1;
            }

            Beep("success");
            return 0;
        }

        private static StateHash LoadResumeHash(RunContext context)
        {
            var file = GetResumeHashFile(context);
            if (!file.Exists)
                throw new SoftFailureException("Cannot find resume hash");

            using (var fs = file.OpenRead())
            using (var r = new StreamReader(fs))
            {
                var line = r.ReadLine();
                try
                {
                    return StateHash.FromHexString(line);
                }
                catch (Exception ex)
                {
                    throw new SoftFailureException("Invalid resume hash format", ex);
                }
            }
        }

        private static void CleanResumeHash(RunContext context)
        {
            var file = GetResumeHashFile(context);
            file.Delete();
        }

        private static FileInfo GetResumeHashFile(RunContext context)
        {
            return new FileInfo(Path.Combine(context.ProjectRoot, ".dbbm.resume"));
        }


        private string DiscoverProjectRoot()
        {
            var path = Environment.CurrentDirectory;
            do
            {
                if (File.Exists(Path.Combine(path, FileConstants.ProjectFileName)))
                    return path;

                path = Path.GetDirectoryName(path);
            } while (path != null);

            throw new SoftFailureException("Cannot find project root");
        }

        private void Beep(string type)
        {
            BeepConfig beep;
            if (mUserConfig.Beeps.TryGetValue(type, out beep))
            {
                Buzzer.Beep(beep.Frequency, beep.Duration, beep.Pulses, beep.DutyCycle);
            }
        }

        private ExecutionNode BuildRestoreDatabasesNode(DatabaseBackupInfo[] databases, RunContext context)
        {
            var node = new ExecutionNode("Restoring databases...", "All databases restored!");
            node.AddChild(new ExecutionNode(new RestoreDatabasesTransform(context.UserConfig.Databases.Connection, databases)));
            return node;
        }

        private ExecutionNode BuildReleaseNode(ReleaseConfig release, RunContext context)
        {
            var node = new ExecutionNode(string.Format("Begin release {0}", release.Name), string.Format("End release {0}", release.Name));
            foreach (var feature in release.Features)
            {
                node.AddChild(BuildFeatureNode(feature, context));
            }

            return node;
        }

        private ExecutionNode BuildFeatureNode(string featureName, RunContext context)
        {
            FeatureConfig feature;
            if (!context.Features.TryGet(featureName, out feature))
            {
                throw new SoftFailureException(string.Format("Cannot find feature {0}", featureName));
            }

            var node = new ExecutionNode(string.Format("Begin feature {0}", featureName), string.Format("End feature {0}", featureName));
            foreach (var taskConfig in feature.Recipe)
            {
                var task = context.TaskManager.CreateTask(taskConfig);
                var replacer = new VariableReplacer(context, feature, taskConfig);
                node.AddChild(new ExecutionNode(task, new TaskExecutionContext(context, feature, taskConfig, replacer)));
            }

            return node;
        }

        private ActionPlan BuildActionPlan(RunContext context)
        {
            var dbFiles = Directory.EnumerateFiles(mUserConfig.Databases.Backups.Root)
                .Select(x => new
                {
                    FullPath = x,
                    Match = mUserConfig.Databases.Backups.Pattern.Match(Path.GetFileName(x))
                })
                .Where(x => x.Match.Success)
                .Select(x => new
                {
                    x.FullPath,
                    DbName = x.Match.Groups["dbName"].Value,
                    Release = x.Match.Groups["release"].Value,
                    Environment = x.Match.Groups["env"] != null ? x.Match.Groups["env"].Value : null
                })
                .GroupBy(x => x.Release)
                .ToDictionary(x => x.Key, x => x.GroupBy(y => y.Environment)
                    .ToDictionary(y => y.Key, y =>
                        y.ToDictionary(z => z.DbName, z => z.FullPath)));

            var releaseStack = new Stack<ReleaseConfig>();
            var head = context.ActiveRelease;
            while (true)
            {
                var dbsForRelease = TryGetValue(dbFiles, head.Name);
                var userEnv = mUserConfig.EnvironmentVariables.GetOrDefault("environment");

                var dbs = GetDatabaseBackups(dbsForRelease, userEnv);
                if (dbs != null)
                {
                    return new ActionPlan(dbs, releaseStack.ToArray());
                }

                releaseStack.Push(head);
                if (head.Baseline == null)
                {
                    throw new SoftFailureException(string.Format("Cannot find a valid base to start. Last release found: {0}", head.Name));
                }
                if (!context.Releases.Releases.TryGet(head.Baseline, out head))
                {
                    throw new SoftFailureException(string.Format("Cannot find release {0} (baseline of {1})", head.Baseline, head.Name));
                }
            }
        }

        private DatabaseBackupInfo[] GetDatabaseBackups(Dictionary<string, Dictionary<string, string>> dbsByEnv, string userEnv)
        {
            if (dbsByEnv == null)
                return null;

            if (userEnv != null)
            {
                var dbsForEnv = TryGetValue(dbsByEnv, userEnv);
                if (dbsForEnv != null)
                {
                    var dbs = GetDatabaseBackups(dbsForEnv);
                    if (dbs != null)
                        return dbs;
                }
            }

            foreach (var kvp in dbsByEnv)
            {
                var dbs = GetDatabaseBackups(kvp.Value);
                if (dbs != null)
                    return dbs;
            }

            return null;
        }

        private DatabaseBackupInfo[] GetDatabaseBackups(Dictionary<string, string> dbs)
        {
            var result = mProjectConfig.Databases.Select(x => new DatabaseBackupInfo(x, TryGetValue(dbs, x))).ToArray();
            return result.Any(x => x.BackupFilePath == null) ? null : result;
        }

        private static TValue TryGetValue<TKey, TValue>(IDictionary<TKey, TValue> dict, TKey key)
        {
            TValue value;
            return dict.TryGetValue(key, out value) ? value : default(TValue);
        }

        private RunContext CreateRunContext()
        {
            var releasesFile = Path.Combine(mProjectRoot, mProjectConfig.Releases);
            var releases = ReleasesConfig.LoadFromJson(releasesFile);

            var featuresFiles = FileUtils.ExpandGlob(Path.Combine(mProjectRoot, mProjectConfig.Features));
            var features = FeatureConfigCollection.LoadFromMultipleJsons(featuresFiles);

            var tasksFiles = FileUtils.ExpandGlob(Path.Combine(mProjectRoot, mProjectConfig.Tasks));
            var tasks = TaskDefinitionConfigCollection.LoadFromMultipleJsons(tasksFiles);

            return new RunContext(mCommandLine, mProjectRoot, mUserConfig, mProjectConfig, releases, features, tasks, new TaskManager(tasks), new ConsoleLog());
        }

        private class NullCacheManager : ICacheManager
        {
            public bool TryGet(string dbName, StateHash state, out string path)
            {
                path = null;
                return false;
            }

            public void Add(DatabaseConnectionConfig dbConfig, string dbName, StateHash state)
            {
            }
        }

        private class ExecutionNode
        {
            private readonly string mLogPre;
            private readonly string mLogPost;
            private readonly IStateTransform mTransform;
            private readonly List<ExecutionNode> mChildren = new List<ExecutionNode>();

            public ExecutionNode(string logPre, string logPost)
            {
                mLogPre = logPre;
                mLogPost = logPost;
            }

            public ExecutionNode(ITask task, TaskExecutionContext context) :
                this(new TaskExecutionTransform(task, context))
            {
            }

            public ExecutionNode(IStateTransform transform)
            {
                mTransform = transform;
            }

            public void AddChild(ExecutionNode node)
            {
                if (mTransform != null)
                    throw new InvalidOperationException("Cannot add child nodes to an action-initialized execution node");

                mChildren.Add(node);
            }

            public Tuple<ExecutionNode, StateHash, bool> Calculate(RunContext context, StateHash hash, StateHash startingHash, ICacheManager cacheManager)
            {
                if (mTransform != null)
                {
                    hash = mTransform.CalculateTransform(hash);
                    if (hash == startingHash)
                        return Tuple.Create((ExecutionNode)null, hash, true);

                    var backups = GetCachedBackups(cacheManager, hash, context.ProjectConfig.Databases);
                    if (backups != null)
                    {
                        var node = new ExecutionNode("Restoring state from cache...", "Cache restored");
                        node.AddChild(new ExecutionNode(new RestoreDatabasesTransform(context.UserConfig.Databases.Connection, backups, hash)));

                        return Tuple.Create(node, hash, true);
                    }
                }
                else if (mChildren.Count > 0)
                {
                    var result = new ExecutionNode(mLogPre, mLogPost);
                    var changed = false;

                    foreach (var child in mChildren)
                    {
                        var calc = child.Calculate(context, hash, startingHash, cacheManager);
                        if (calc.Item3)
                        {
                            changed = true;
                            result.mChildren.Clear();
                        }

                        if (calc.Item1 != null)
                            result.mChildren.Add(calc.Item1);

                        hash = calc.Item2;
                    }

                    if (result.mChildren.Count == 0)
                        result = null;

                    return Tuple.Create(result, hash, changed);
                }

                return Tuple.Create(this, hash, false);
            }

            public StateHash Run(RunContext context, StateHash hash, ICacheManager cacheManager, bool first = true, bool last = true)
            {
                if (mLogPre != null)
                    context.Log.Log(mLogPre);

                if (mTransform != null)
                {
                    var stopWatch = new Stopwatch();
                    stopWatch.Start();
                    hash = mTransform.RunTransform(hash, context.DryRun, context.Log);
                    stopWatch.Stop();

                    var rhf = GetResumeHashFile(context);
                    File.WriteAllText(rhf.FullName, hash.ToHexString());

                    if (!first && !last && stopWatch.Elapsed >= context.UserConfig.Cache.MinDeployTime)
                    {
                        foreach (var db in context.ProjectConfig.Databases)
                        {
                            cacheManager.Add(context.UserConfig.Databases.Connection, db, hash);
                        }
                    }
                }
                else if (mChildren.Count > 0)
                {
                    using (context.Log.IndentScope())
                    {
                        for (var i = 0; i < mChildren.Count; i++)
                        {
                            var child = mChildren[i];
                            hash = child.Run(context, hash, cacheManager, first, last && i == mChildren.Count - 1);
                            first = false;
                        }
                    }
                }

                if (mLogPost != null)
                    context.Log.Log(mLogPost);

                return hash;
            }

            private DatabaseBackupInfo[] GetCachedBackups(ICacheManager cacheManager, StateHash hash, IReadOnlyCollection<string> dbs)
            {
                var result = new DatabaseBackupInfo[dbs.Count];
                var i = 0;

                foreach (var db in dbs)
                {
                    string path;
                    if (!cacheManager.TryGet(db, hash, out path))
                        return null;

                    result[i++] = new DatabaseBackupInfo(db, path);
                }

                return result;
            }
        }

        private class ActionPlan
        {
            private readonly DatabaseBackupInfo[] mDatabases;
            private readonly ReleaseConfig[] mReleases;

            public ActionPlan(DatabaseBackupInfo[] databases, ReleaseConfig[] releases)
            {
                mDatabases = databases;
                mReleases = releases;
            }

            public DatabaseBackupInfo[] Databases
            {
                get { return mDatabases; }
            }

            public ReleaseConfig[] Releases
            {
                get { return mReleases; }
            }
        }
    }
}
