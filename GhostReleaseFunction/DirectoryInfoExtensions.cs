using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace GhostVersionFunctionApp
{
    public static class DirectoryInfoExtensions
    {
        public static void Empty(this DirectoryInfo directory, bool skipGit = false)
        {
            foreach (System.IO.FileInfo file in directory.GetFiles())
            {
                file.Delete();
            }
            foreach (System.IO.DirectoryInfo subDirectory in directory.GetDirectories())
            {
                if (skipGit && subDirectory.Name == ".git")
                {
                    continue;
                }
                subDirectory.Delete(true);
            }
        }

        public static void CopyFilesRecursively(this DirectoryInfo source, DirectoryInfo target)
        {
            foreach (DirectoryInfo dir in source.GetDirectories())
                CopyFilesRecursively(dir, target.CreateSubdirectory(dir.Name));
            foreach (FileInfo file in source.GetFiles())
                file.CopyTo(Path.Combine(target.FullName, file.Name), true);
        }

        public static void EnrichPackageJson(this DirectoryInfo target)
        {
            var packageJsonLocation = Path.Combine(target.FullName, "package.json");
            string json = File.ReadAllText(packageJsonLocation);
            dynamic jsonObj = Newtonsoft.Json.JsonConvert.DeserializeObject(json);
            //jsonObj.engines.node = ((string)jsonObj.engines.node).Split(new[] { "||" }, StringSplitOptions.None).LastOrDefault().Trim();
            jsonObj.dependencies.applicationinsights = "^1.0.0";
            string output = Newtonsoft.Json.JsonConvert.SerializeObject(jsonObj, Newtonsoft.Json.Formatting.Indented);
            File.WriteAllText(packageJsonLocation, output);
        }

        public static async Task DownloadGhostVersion(this DirectoryInfo destination, string releaseUrl)
        {
            var ghostZipLocalUri = Path.Combine(destination.FullName, "ghost.zip");
            using (WebClient wc = new WebClient())
            {
                await wc.DownloadFileTaskAsync(new Uri(releaseUrl), ghostZipLocalUri);
            }

            using (ZipArchive zip = new ZipArchive(File.OpenRead(ghostZipLocalUri), ZipArchiveMode.Read))
            {
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    //make sure it's not a folder
                    if (!string.IsNullOrEmpty(Path.GetExtension(entry.FullName)) || entry.FullName == "LICENSE")
                    {
                        var deflateStream = entry.Open();
                        using (var fileStream = File.Create(Path.Combine(destination.FullName, entry.FullName)))
                        {
                            deflateStream.CopyTo(fileStream);
                        }
                    }
                    else
                    {
                        Directory.CreateDirectory(Path.Combine(destination.FullName, entry.FullName));
                    }
                }
            }

            File.Delete(ghostZipLocalUri);
        }
    }
}
