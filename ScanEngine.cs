using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using RepoScanner.Models;

namespace RepoScanner.Services
{
    public static class ScanEngine
    {
        public static async Task<ScanSnapshot> ScanServerAsync(string serverName, string rootPath, List<BlacklistItem> blacklist)
        {
            var inventory = new ConcurrentBag<FileInventoryItem>();
            var scannedAt = DateTime.UtcNow;

            var blacklists = blacklist.Select(b => {
                string p = b.Path.Replace("/", "\\").ToLowerInvariant();
                if (!p.Contains("*") && !p.Contains("?") && !p.Contains("^") && !p.Contains("$"))
                {
                    p = p.TrimEnd('\\');
                }
                return (dynamic)new { Path = p, b.IgnoreCondition };
            }).ToList();

            if (Directory.Exists(rootPath))
            {
                await Task.Run(() => WalkDirectory(rootPath, rootPath, blacklists, inventory));
            }

            var inventoryList = inventory.ToList();
            int totalFiles = inventoryList.Count(i => !i.IsFolder);
            int totalFolders = inventoryList.Count(i => i.IsFolder);

            return new ScanSnapshot
            {
                ServerName = serverName,
                RootPath = rootPath,
                ScannedAt = scannedAt,
                TotalFiles = totalFiles,
                TotalFolders = totalFolders,
                Inventory = inventoryList
            };
        }

        private static void WalkDirectory(string currentDir, string rootPath, List<dynamic> blacklists, ConcurrentBag<FileInventoryItem> inventory)
        {
            try
            {
                // Determine relative path for blacklist check
                string relativePath = Path.GetRelativePath(rootPath, currentDir);
                if (relativePath == "." || string.IsNullOrEmpty(relativePath))
                {
                    relativePath = "";
                }

                string relativePathLower = relativePath.ToLowerInvariant();

                // Hardcoded check to completely ignore __reposync folder and its contents
                if (relativePathLower == "__reposync" || relativePathLower.StartsWith("__reposync\\") || relativePathLower.StartsWith("__reposync/"))
                {
                    return; // Skip completely
                }

                // Check entire folder blacklist
                var folderIgnore = blacklists.FirstOrDefault(b => 
                    b.IgnoreCondition == "IGNORE_ENTIRE_FOLDER" && 
                    PathMatchesPattern(relativePathLower, b.Path)
                );

                if (folderIgnore != null)
                {
                    return; // Skip completely
                }

                // Check ignore files only condition
                var filesIgnore = blacklists.FirstOrDefault(b => 
                    b.IgnoreCondition == "IGNORE_FILES_ONLY" && 
                    PathMatchesPattern(relativePathLower, b.Path)
                );

                // Ignore hidden/system folders (like $RECYCLE.BIN, System Volume Information)
                if (!string.IsNullOrEmpty(relativePath))
                {
                    var dirInfo = new DirectoryInfo(currentDir);
                    if ((dirInfo.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0 ||
                        dirInfo.Name.StartsWith("$", StringComparison.OrdinalIgnoreCase) ||
                        dirInfo.Name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase))
                    {
                        return; // Skip completely
                    }
                }

                // Inventory current directory itself (unless it's the root itself, or skipped)
                if (!string.IsNullOrEmpty(relativePath))
                {
                    var dirInfo = new DirectoryInfo(currentDir);
                    inventory.Add(new FileInventoryItem
                    {
                        Name = dirInfo.Name,
                        RelativePath = relativePath,
                        IsFolder = true,
                        Size = 0,
                        LastModified = dirInfo.LastWriteTimeUtc,
                        RootPath = rootPath
                    });
                }

                // Process files in the current folder if not ignored
                if (filesIgnore == null)
                {
                    try
                    {
                        var files = Directory.GetFiles(currentDir);
                        foreach (var filePath in files)
                        {
                            var fileInfo = new FileInfo(filePath);
                            if ((fileInfo.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0 ||
                                fileInfo.Name.StartsWith("$", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }
                            string fileRelativePath = Path.GetRelativePath(rootPath, filePath);
                            inventory.Add(new FileInventoryItem
                            {
                                Name = fileInfo.Name,
                                RelativePath = fileRelativePath,
                                IsFolder = false,
                                Size = fileInfo.Length,
                                LastModified = fileInfo.LastWriteTimeUtc,
                                RootPath = rootPath
                            });
                        }
                    }
                    catch (UnauthorizedAccessException) { }
                    catch (DirectoryNotFoundException) { }
                }

                // Recursively walk subdirectories
                try
                {
                    var subdirs = Directory.GetDirectories(currentDir);
                    // Use parallel execution for subfolders to maximize performance over network shares
                    Parallel.ForEach(subdirs, new ParallelOptions { MaxDegreeOfParallelism = 8 }, subdir =>
                    {
                        WalkDirectory(subdir, rootPath, blacklists, inventory);
                    });
                }
                catch (UnauthorizedAccessException) { }
                catch (DirectoryNotFoundException) { }
            }
            catch (Exception)
            {
                // Gracefully catch security/IO exceptions for scanning robustly
            }
        }

        private static bool PathMatchesPattern(string relativePath, string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return false;

            string normalizedPath = relativePath.Replace("/", "\\").ToLowerInvariant();
            string pathWithTrailing = normalizedPath.EndsWith("\\") ? normalizedPath : normalizedPath + "\\";
            string normalizedPattern = pattern.Replace("/", "\\").ToLowerInvariant();

            // 1. Try wildcard pattern matching
            if (normalizedPattern.Contains("*") || normalizedPattern.Contains("?"))
            {
                try
                {
                    string regexPattern = "^" + System.Text.RegularExpressions.Regex.Escape(normalizedPattern)
                                                     .Replace("\\*", ".*")
                                                     .Replace("\\?", ".") + "$";
                    
                    if (System.Text.RegularExpressions.Regex.IsMatch(normalizedPath, regexPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase) ||
                        System.Text.RegularExpressions.Regex.IsMatch(pathWithTrailing, regexPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    {
                        return true;
                    }
                }
                catch {}
            }

            // 2. Try raw Regex matching
            try
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(normalizedPath, normalizedPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase) ||
                    System.Text.RegularExpressions.Regex.IsMatch(pathWithTrailing, normalizedPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    return true;
                }
            }
            catch {}

            // 3. Fallback
            return normalizedPath == normalizedPattern || normalizedPath.StartsWith(normalizedPattern + "\\");
        }
    }
}
