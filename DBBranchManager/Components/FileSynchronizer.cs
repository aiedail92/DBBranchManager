using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using DBBranchManager.Constants;
using DBBranchManager.Utils;

namespace DBBranchManager.Components
{
    internal class FileSynchronizer : ComponentBase
    {
        private readonly string mSourcePath;
        private readonly string mDestinationPath;
        private readonly Regex mFilter;

        public FileSynchronizer(string sourcePath, string destinationPath, Regex filter)
        {
            mSourcePath = sourcePath;
            mDestinationPath = destinationPath;
            mFilter = filter;
        }

        [RunAction(ActionConstants.Deploy)]
        private IEnumerable<string> DeployRun(string action, ComponentRunContext runContext)
        {
            if (Directory.Exists(mSourcePath))
            {
                if (!Directory.Exists(mDestinationPath))
                {
                    yield return string.Format("Creating {0}", mDestinationPath);
                    if (!runContext.DryRun)
                        Directory.CreateDirectory(mDestinationPath);
                }

                foreach (var f in FileUtils.EnumerateFiles2(mSourcePath, mFilter.IsMatch))
                {
                    var fileName = f.FileName;
                    Debug.Assert(fileName != null, "fileName != null");

                    var destFile = Path.Combine(mDestinationPath, fileName);

                    var fileInfo = new FileInfo(f.FullPath);
                    var destFileInfo = new FileInfo(destFile);

                    if (destFileInfo.Exists && destFileInfo.LastWriteTimeUtc == fileInfo.LastWriteTimeUtc)
                    {
                        yield return string.Format("Skipping {0}", fileName);
                    }
                    else
                    {
                        yield return string.Format("Copying {0} -> {1}", fileName, mDestinationPath);

                        if (!runContext.DryRun)
                        {
                            if (destFileInfo.Exists && (destFileInfo.Attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                            {
                                destFileInfo.Attributes &= ~FileAttributes.ReadOnly;
                            }
                            fileInfo.CopyTo(destFile, true);
                        }
                    }
                }
            }
        }
    }
}