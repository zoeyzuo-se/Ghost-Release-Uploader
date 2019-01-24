using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using LibGit2Sharp;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System.Linq;

namespace GhostVersionFunctionApp
{
    public static class UploadReleaseFunction
    {
        private static readonly Settings Settings = new Settings();
        private static readonly HttpClient HttpClient = new HttpClient();
        private static readonly JsonSerializerSettings SerializerSettings = new JsonSerializerSettings
        {
            MissingMemberHandling = MissingMemberHandling.Ignore,
            MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
            DateParseHandling = DateParseHandling.None,
            Converters =
            {
                new IsoDateTimeConverter { DateTimeStyles = DateTimeStyles.AssumeUniversal }
            },
        };

        [Singleton]
        [FunctionName("ghost-release-processor")]
        public static async Task StartAsync(
            [TimerTrigger("0 0 16 * * *")]TimerInfo myTimer,
            [OrchestrationClient]DurableOrchestrationClient starter,
            TraceWriter log)
        {
            // Starting a new orchestrator with request data
            string instanceId = await starter.StartNewAsync("release-processor-orchestrator", null);

            log.Info($"Started orchestration with ID = '{instanceId}'.");
        }

        [FunctionName("release-processor-orchestrator")]
        public static async Task RunAsync([OrchestrationTrigger] DurableOrchestrationContext context, TraceWriter log)
        {
            var processedRelease = context.CallActivityAsync<ReleaseInfo>("ghost-processed-release", null);
            var allReleases = context.CallActivityAsync<ReleaseInfo[]>("ghost-all-releases", null);

            await Task.WhenAll(processedRelease, allReleases);

            var releases = await context.CallActivityAsync<List<ReleaseInfo>>("ghost-remaining-releases", (processedRelease.Result, allReleases.Result));

            foreach (var release in releases)
            {
                await context.CallActivityAsync<string>("ghost-process-release", release);
            }
        }

        [FunctionName("ghost-processed-release")]
        public static async Task<ReleaseInfo> GetProcessedReleaseAsync([ActivityTrigger]object funcParams, TraceWriter log)
        {
            log.Info($"Loading latest processed Ghost release.");

            var message = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Settings.GitRepoOwner}/{Settings.GitRepoName}/releases/latest?access_token={Settings.GitPassword}");
            message.Headers.UserAgent.Add(new System.Net.Http.Headers.ProductInfoHeaderValue("Mozilla", "5.0"));
            var response = await HttpClient.SendAsync(message);

            var responseContent = await response.Content.ReadAsStringAsync();
            var release = JsonConvert.DeserializeObject<Release>(responseContent, SerializerSettings);

            log.Info($"Latest processed Ghost release: {release.Name}.");

            return new ReleaseInfo()
            {
                ReleaseName = release.Name,
                ReleaseNotes = release.Body,
                ReleaseUrl = release.Assets.FirstOrDefault()?.BrowserDownloadUrl.AbsoluteUri
            };
        }

        [FunctionName("ghost-all-releases")]
        public static async Task<List<ReleaseInfo>> GetAllReleasesAsync([ActivityTrigger]object funcParams, TraceWriter log)
        {
            log.Info($"Loading newest Ghost releases.");

            var message = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/TryGhost/Ghost/releases?access_token={Settings.GitPassword}");
            message.Headers.UserAgent.Add(new System.Net.Http.Headers.ProductInfoHeaderValue("Mozilla", "5.0"));
            var response = await HttpClient.SendAsync(message);

            var responseContent = await response.Content.ReadAsStringAsync();
            var releases = JsonConvert.DeserializeObject<List<Release>>(responseContent, SerializerSettings);

            var v2Releases = releases.Where(r => r.Name.StartsWith("2.", StringComparison.OrdinalIgnoreCase)).Select(r => new ReleaseInfo()
            {
                ReleaseName = r.Name,
                ReleaseNotes = r.Body,
                ReleaseUrl = r.Assets.FirstOrDefault()?.BrowserDownloadUrl.AbsoluteUri
            }).ToList();

            log.Info($"Available Ghost releases: {releases.Count}.");

            return v2Releases;
        }

        [FunctionName("ghost-remaining-releases")]
        public static List<ReleaseInfo> DetermineRemainingReleasesAsync([ActivityTrigger]Tuple<ReleaseInfo, List<ReleaseInfo>> releaseInfo, TraceWriter log)
        {
            log.Info($"Determining new Ghost releases that need processing.");

            var (processedRelease, allReleases) = releaseInfo;

            var remainingReleases = new List<ReleaseInfo>();

            if (!string.IsNullOrEmpty(processedRelease.ReleaseName))
            {
                var i = allReleases.FindIndex(ar => ar.ReleaseName.Equals(processedRelease.ReleaseName, StringComparison.OrdinalIgnoreCase));

                if (i >= 0)
                {
                    remainingReleases = allReleases.GetRange(0, i);
                    remainingReleases.Reverse();
                }
            }
            else
            {
                log.Warning("There was a problem determining the latest processed release.");
            }

            log.Info($"New Ghost releases that need processing: {remainingReleases.Count}.");

            return remainingReleases;
        }

        [FunctionName("ghost-process-release")]
        public static async Task ProcessReleaseAsync([ActivityTrigger]ReleaseInfo funcParams, TraceWriter log, ExecutionContext context)
        {
            log.Info($"Processing Ghost release: {funcParams.ReleaseName}.");

            var resourcesPath = context.FunctionAppDirectory;
            var repoPath = Path.GetFullPath(Path.Combine(resourcesPath, @"..\Target-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmss")));
            try
            {
                var co = new CloneOptions
                {
                    CredentialsProvider = Settings.Handler
                };
                var gitPath = Repository.Clone($"https://github.com/{Settings.GitRepoOwner}/{Settings.GitRepoName}.git", repoPath, co);
                using (var repo = new Repository(gitPath))
                {
                    var repoDir = new DirectoryInfo(repoPath);
                    repoDir.Empty(true);

                    await repoDir.DownloadGhostVersion(funcParams.ReleaseUrl);

                    repoDir.EnrichPackageJson();

                    var azureResourcesDir = new DirectoryInfo(Path.Combine(resourcesPath, "AzureDeployment"));
                    azureResourcesDir.CopyFilesRecursively(repoDir);

                    Commands.Stage(repo, "*");

                    var author = new Signature(Settings.GitAuthorName, Settings.GitAuthorEmail, DateTime.Now);
                    var commit = repo.Commit($"Add v{funcParams.ReleaseName}", author, author);
                    var options = new PushOptions
                    {
                        CredentialsProvider = Settings.Handler
                    };
                    repo.Network.Push(repo.Branches[Settings.GitRepoBranch], options);

                    await CreateRelease(funcParams.ReleaseName, funcParams.ReleaseNotes);
                }
            }
            catch (Exception e)
            {
                log.Error(e.Message);
                log.Error(e.StackTrace);
            }
            finally
            {
                log.Info($"Finished processing Ghost release: {funcParams.ReleaseName}.");
            }
        }

        private static async Task CreateRelease(string releaseName, string releaseNotes)
        {
            var data = new
            {
                tag_name = releaseName,
                target_commitish = Settings.GitRepoBranch,
                name = releaseName,
                body = releaseNotes,
                draft = false,
                prerelease = false
            };
            var stringContent = new StringContent(JsonConvert.SerializeObject(data), Encoding.UTF8, "application/json");

            // You must set a user agent so that the CRLF requirement on the header parsing is met.
            // Otherwise you will get an excpetion message with "The server committed a protocol violation. Section=ResponseStatusLine"
            var message = new HttpRequestMessage(HttpMethod.Post, $"https://api.github.com/repos/{Settings.GitRepoOwner}/{Settings.GitRepoName}/releases?access_token={Settings.GitPassword}")
            {
                Content = stringContent
            };
            message.Headers.UserAgent.Add(new System.Net.Http.Headers.ProductInfoHeaderValue("Mozilla", "5.0"));
            await HttpClient.SendAsync(message);
        }
    }
}