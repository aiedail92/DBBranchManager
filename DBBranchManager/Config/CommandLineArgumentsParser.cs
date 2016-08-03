using System.Collections.Generic;
using DBBranchManager.Entities;
using DBBranchManager.Exceptions;

namespace DBBranchManager.Config
{
    internal class CommandLineArgumentsParser
    {
        private readonly string[] mArgs;
        private int mIndex;
        private bool mGotCommand;

        public CommandLineArgumentsParser(string[] args)
        {
            mArgs = args;
        }

        public CommandLineArguments Parse()
        {
            var unparsed = new List<string>();
            var command = "help";
            string release = null;
            var dryRun = false;
            var resume = false;

            mIndex = 0;
            while (mIndex < mArgs.Length)
            {
                string value;
                if (TryGetArg("-r", "--release=", out value))
                {
                    release = value;
                }
                else if (TryGetFlag("-n", "--dry-run"))
                {
                    dryRun = true;
                }
                else if (TryGetFlag("-s", "--resume"))
                {
                    resume = true;
                }
                else if (!mArgs[mIndex].StartsWith("-"))
                {
                    if (mGotCommand)
                        throw new SoftFailureException("Multiple commands specified");

                    command = mArgs[mIndex];
                    mGotCommand = true;
                }
                else
                {
                    unparsed.Add(mArgs[mIndex]);
                }

                ++mIndex;
            }

            return new CommandLineArguments(command, release, dryRun, resume, unparsed.ToArray());
        }

        private bool TryGetFlag(string shortName, string longName)
        {
            if (mArgs[mIndex] == shortName)
                return true;
            if (mArgs[mIndex] == longName)
                return true;

            return false;
        }

        private bool TryGetArg(string shortName, string longName, out string value)
        {
            if (mArgs[mIndex] == shortName)
                value = mArgs[++mIndex];
            else if (mArgs[mIndex].StartsWith(longName))
                value = mArgs[mIndex].Substring(longName.Length);
            else
            {
                value = null;
                return false;
            }

            return true;
        }
    }
}