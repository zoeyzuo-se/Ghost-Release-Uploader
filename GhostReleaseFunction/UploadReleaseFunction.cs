using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using LibGit2Sharp;
using LibGit2Sharp.Handlers;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Host;
using Newtonsoft.Json;

namespace GhostVersionFunctionApp
{
    public static class UploadReleaseFunction
    {
        public static string GitUserName { get; set; } = GetEnvironmentVariable("GitUserName");
        public static string GitPassword { get; set; } = GetEnvironmentVariable("GitPassword");

        public static string GitRepoOwner { get; set; } = GetEnvironmentVariable("GitRepoOwner");
        public static string GitRepoName { get; set; } = GetEnvironmentVariable("GitRepoName");
        public static string GitRepoBranch { get; set; } = GetEnvironmentVariable("GitRepoBranch");

        public static string GitAuthorName { get; set; } = GetEnvironmentVariable("GitAuthorName");
        public static string GitAuthorEmail { get; set; } = GetEnvironmentVariable("GitAuthorEmail");

        public static CredentialsHandler Handler => (_url, _user, _cred) =>
            new UsernamePasswordCredentials { Username = GitUserName, Password = GitPassword };

        private static string GetEnvironmentVariable(string name)
        {
            return Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process);
        }

        private static async Task CreateRelease(string releaseName, string releaseNotes)
        {
            var data = new
            {
                tag_name = releaseName,
                target_commitish = GitRepoBranch,
                name = releaseName,
                body = releaseNotes,
                draft = false,
                prerelease = false
            };
            var stringContent = new StringContent(JsonConvert.SerializeObject(data), Encoding.UTF8, "application/json");

            using (HttpClient hc = new HttpClient())
            {
                // You must set a user agent so that the CRLF requirement on the header parsing is met.
                // Otherwise you will get an excpetion message with "The server committed a protocol violation. Section=ResponseStatusLine"
                hc.DefaultRequestHeaders.UserAgent.Add(new System.Net.Http.Headers.ProductInfoHeaderValue("Mozilla", "5.0"));
                await hc.PostAsync($"https://api.github.com/repos/{GitRepoOwner}/{GitRepoName}/releases?access_token={GitPassword}", stringContent);
            }
        }

        private static void EnrichPackageJson(DirectoryInfo target)
        {
            var packageJsonLocation = Path.Combine(target.FullName, "package.json");
            string json = File.ReadAllText(packageJsonLocation);
            dynamic jsonObj = Newtonsoft.Json.JsonConvert.DeserializeObject(json);
            jsonObj.engines.node = ((string)jsonObj.engines.node).Split(new[] { "||" }, StringSplitOptions.None).LastOrDefault().Trim();
            jsonObj.dependencies.applicationinsights = "^1.0.0";
            string output = Newtonsoft.Json.JsonConvert.SerializeObject(jsonObj, Newtonsoft.Json.Formatting.Indented);
            File.WriteAllText(packageJsonLocation, output);
        }

        private static async Task DownloadGhostVersion(DirectoryInfo destination, string releaseUrl)
        {
            var ghostZipLocalUri = Path.Combine(destination.FullName, "ghost.zip");
            using (WebClient wc = new WebClient())
            {
                await wc.DownloadFileTaskAsync(new Uri(releaseUrl), ghostZipLocalUri);
            }

            using (ZipArchive archive = ZipFile.OpenRead(ghostZipLocalUri))
            {
                archive.ExtractToDirectory(destination.FullName);
            }

            File.Delete(ghostZipLocalUri);
        }

        [FunctionName("ghost-release")]
        public static async Task<string> Run([HttpTrigger(AuthorizationLevel.Function, "post", Route = null)]FunctionParams funcParams, TraceWriter log)
        {
            var binFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            var resourcesPath = Path.GetFullPath(Path.Combine(binFolder, @"..\"));
            var repoPath = Path.GetFullPath(Path.Combine(resourcesPath, @"..\Target-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmss")));

            var co = new CloneOptions
            {
                CredentialsProvider = Handler
            };
            var gitPath = Repository.Clone($"https://github.com/{GitRepoOwner}/{GitRepoName}.git", repoPath, co);
            using (var repo = new Repository(gitPath))
            {
                var repoDir = new DirectoryInfo(repoPath);
                repoDir.Empty(true);

                await DownloadGhostVersion(repoDir, funcParams.ReleaseUrl);

                EnrichPackageJson(repoDir);

                var azureResourcesDir = new DirectoryInfo(Path.Combine(resourcesPath, "AzureDeployment"));
                azureResourcesDir.CopyFilesRecursively(repoDir);

                Commands.Stage(repo, "*");

                var author = new Signature(GitAuthorName, GitAuthorEmail, DateTime.Now);
                var commit = repo.Commit($"Add v{funcParams.ReleaseName}", author, author);
                var options = new PushOptions
                {
                    CredentialsProvider = Handler
                };
                repo.Network.Push(repo.Branches[GitRepoBranch], options);

                await CreateRelease(funcParams.ReleaseName, funcParams.ReleaseNotes);
            }

            return "finished";
        }
    }
}