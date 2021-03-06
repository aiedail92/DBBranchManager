using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using DBBranchManager.Caching;
using DBBranchManager.Exceptions;
using DBBranchManager.Utils;
using DBBranchManager.Utils.Sql;

namespace DBBranchManager.Tasks
{
    internal class SqlTask : ITask
    {
        public const string TaskName = "sql";

        public string Name
        {
            get { return TaskName; }
        }

        public void GetRequirements(TaskExecutionContext context, IRequirementSink sink)
        {
        }

        public void Simulate(TaskExecutionContext context, ref StateHash hash)
        {
            hash = ExecuteCore(context, hash, false);
        }

        public void Execute(TaskExecutionContext context, ref StateHash hash)
        {
            hash = ExecuteCore(context, hash, true);
        }

        private StateHash ExecuteCore(TaskExecutionContext context, StateHash hash, bool notSimulate)
        {
            var path = FileUtils.ToLocalPath(context.GetParameter("path"));
            if (!Path.IsPathRooted(path))
                path = FileUtils.ToLocalPath(context.Feature.BaseDirectory, path);

            var regex = new Regex(context.GetParameter("regex"));
            var execute = bool.Parse(context.GetParameter("execute") ?? "true");
            var output = FileUtils.ToLocalPath(context.GetParameter("output"));
            var preTemplate = context.GetParameter("templates.pre");
            var postTemplate = context.GetParameter("templates.post");

            if (!Directory.Exists(path))
                return hash;

            var script = GenerateScript(context, path, regex, preTemplate, postTemplate, notSimulate && (!execute || context.DryRun || output != null), ref hash);

            if (notSimulate)
            {
                if (output != null)
                {
                    context.Log.LogFormat("Generating {0}", output);

                    if (!context.DryRun)
                        File.WriteAllText(output, script);
                }

                if (execute && !context.DryRun)
                {
                    using (var sqlcmdResult = SqlUtils.SqlCmdExec(context.Context.ApplicationContext.UserConfig.Databases.Connection, script))
                    {
                        foreach (var processOutputLine in sqlcmdResult.GetOutput())
                        {
                            if (processOutputLine.OutputType == ProcessOutputLine.OutputTypeEnum.StandardError)
                                context.Log.Log(processOutputLine.Line);
                        }

                        if (sqlcmdResult.ExitCode != 0)
                        {
                            throw new SoftFailureException("One or more errors occurred during scripts execution");
                        }
                    }
                }
            }

            return hash;
        }

        private string GenerateScript(TaskExecutionContext context, string path, Regex regex, string preTemplate, string postTemplate, bool log, ref StateHash hash)
        {
            using (var t = new HashTransformer(hash))
            {
                var sb = new StringBuilder();
                sb.AppendFormat("{0}\n", preTemplate);

                var filter = BuildFilter(context, regex);
                foreach (var file in FileUtils.EnumerateFiles2(path, regex.IsMatch))
                {
                    if (filter(file.FileName))
                    {
                        if (log)
                            context.Log.LogFormat("Adding {0}", file.FileName);
                        sb.AppendFormat("{0}\n", context.GetParameter("templates.item", new Dictionary<string, string> { { "file", file.FileName } }));

                        t.TransformWithFileSmart(file.FullPath);
                    }
                    else
                    {
                        if (log)
                            context.Log.LogFormat("Skipping {0}", file.FileName);
                    }
                }

                sb.AppendFormat("{0}\n", postTemplate);

                var script = sb.ToString();

                t.Transform(script);
                hash = t.GetResult();

                return script;
            }
        }

        private static Func<string, bool> BuildFilter(TaskExecutionContext context, Regex regex)
        {
            return file =>
            {
                var match = regex.Match(file);
                return !match.Groups["env"].Success || context.AcceptsEnvironment(match.Groups["env"].Value);
            };
        }
    }
}
