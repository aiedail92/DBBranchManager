﻿using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace DBBranchManager.Components
{
    internal class ReportsComponent : IComponent
    {
        private static readonly Regex ReportFileRegex = new Regex(@"^[DTX]_\d+.+\.x(?:lsm|ml)$");
        private readonly string mReportsPath;
        private readonly string mDeployPath;

        public ReportsComponent(string reportsPath, string deployPath)
        {
            mReportsPath = reportsPath;
            mDeployPath = deployPath;
        }

        public IEnumerable<string> Run(ComponentState componentState)
        {
            if (Directory.Exists(mReportsPath))
            {
                yield return string.Format("Reports: {0} -> {1}", mReportsPath, mDeployPath);

                var synchronizer = new FileSynchronizer(mReportsPath, mDeployPath, ReportFileRegex);
                foreach (var log in synchronizer.Run(componentState))
                {
                    yield return log;
                }
            }
        }
    }
}