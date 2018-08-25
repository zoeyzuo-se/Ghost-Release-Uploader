using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
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

        [FunctionName("ghost-release")]
        public static async Task<HttpResponseMessage> HttpStart(
            [HttpTrigger(AuthorizationLevel.Function, "post")]HttpRequestMessage req,
            [OrchestrationClient]DurableOrchestrationClient starter,
            TraceWriter log)
        {
            // Function input comes from the request content.
            FunctionParams requestData = await req.Content.ReadAsAsync<FunctionParams>();

            // Starting a new orchestrator with request data
            string instanceId = await starter.StartNewAsync("HttpTrigger_Orchestrator", requestData);

            log.Info($"Started orchestration with ID = '{instanceId}'.");

            var response = starter.CreateCheckStatusResponse(req, instanceId);
            return response;
        }

        [FunctionName("HttpTrigger_Orchestrator")]
        public static async Task<List<string>> RunOrchestrator(
            [OrchestrationTrigger] DurableOrchestrationContext context)
        {
            var outputs = new List<string>();

            outputs.Add(await context.CallActivityAsync<string>("Trigger_Prepare_Version", context.GetInput<FunctionParams>()));

            return outputs;
        }

        [FunctionName("Trigger_Prepare_Version")]
        public static async Task<string> Run([ActivityTrigger]FunctionParams funcParams, TraceWriter log, ExecutionContext context)
        {
            var resourcesPath = context.FunctionAppDirectory;
            var repoPath = Path.GetFullPath(Path.Combine(resourcesPath, @"..\Target-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmss")));
            try
            {
                var co = new CloneOptions
                {
                    CredentialsProvider = Handler
                };
                var gitPath = Repository.Clone($"https://github.com/{GitRepoOwner}/{GitRepoName}.git", repoPath, co);
                using (var repo = new Repository(gitPath))
                {
                    var repoDir = new DirectoryInfo(repoPath);
                    repoDir.Empty(true);

                    await repoDir.DownloadGhostVersion(funcParams.ReleaseUrl);

                    repoDir.EnrichPackageJson();

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
            }
            catch (Exception e)
            {
                log.Error(e.Message);
                log.Error(e.StackTrace);
                return "failed: " + e.Message;
            }

            return "finished";
        }
    }
}