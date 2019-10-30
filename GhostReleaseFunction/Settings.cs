using LibGit2Sharp;
using LibGit2Sharp.Handlers;
using System;

namespace GhostVersionFunctionApp
{
    internal class Settings
    {
        internal string GitUserName { get; set; } = GetEnvironmentVariable("GitUserName");
        internal string GitPassword { get; set; } = GetEnvironmentVariable("GitPassword");

        internal string GitRepoOwner { get; set; } = GetEnvironmentVariable("GitRepoOwner");
        internal string GitRepoName { get; set; } = GetEnvironmentVariable("GitRepoName");
        internal string GitRepoBranch { get; set; } = GetEnvironmentVariable("GitRepoBranch");
        internal string GitRepoBranchV2 { get; set; } = GetEnvironmentVariable("GitRepoBranchV2");

        internal string GitAuthorName { get; set; } = GetEnvironmentVariable("GitAuthorName");
        internal string GitAuthorEmail { get; set; } = GetEnvironmentVariable("GitAuthorEmail");

        internal CredentialsHandler Handler => (_url, _user, _cred) =>
            new UsernamePasswordCredentials { Username = GitUserName, Password = GitPassword };

        private static string GetEnvironmentVariable(string name)
        {
            return Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process);
        }
    }
}
