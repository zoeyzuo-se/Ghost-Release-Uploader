using System.IO;

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
    }
}
