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
                bool isRegex = p.Contains("*") || p.Contains("?") || p.Contains("^") || p.Contains("$");
                if (!isRegex)
                {
                    p = p.TrimEnd('\\');
                }
                return new BlacklistRule { Path = p, IgnoreCondition = b.IgnoreCondition, IsRegex = isRegex };
            }).ToList();

            if (Directory.Exists(rootPath))
            {
                await Task.Run(() => WalkDirectory(rootPath, rootPath, blacklists, inventory, false));
            }

            var inventoryList = inventory.ToList();
            int totalFiles = inventoryList.Count(i => !i.IsFolder && !i.IsBlacklisted);
            int totalFolders = inventoryList.Count(i => i.IsFolder && !i.IsBlacklisted);

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

        private static void WalkDirectory(string currentDir, string rootPath, List<BlacklistRule> blacklists, ConcurrentBag<FileInventoryItem> inventory, bool parentIsBlacklisted)
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

                bool isFolderBlacklisted = parentIsBlacklisted;
                if (!isFolderBlacklisted)
                {
                    // Check entire folder blacklist
                    var folderIgnore = blacklists.FirstOrDefault(b => 
                        b.IgnoreCondition == "IGNORE_ENTIRE_FOLDER" && 
                        PathMatchesPattern(relativePathLower, b.Path)
                    );
                    if (folderIgnore != null)
                    {
                        if (folderIgnore.IsRegex)
                        {
                            isFolderBlacklisted = true;
                        }
                        else
                        {
                            return; // Skip completely
                        }
                    }
                }

                // Check ignore files only condition
                var filesIgnore = blacklists.FirstOrDefault(b => 
                    b.IgnoreCondition == "IGNORE_FILES_ONLY" && 
                    PathMatchesPattern(relativePathLower, b.Path)
                );

                bool filesAreRegexIgnored = isFolderBlacklisted || (filesIgnore != null && filesIgnore.IsRegex);
                bool filesAreNonRegexIgnored = filesIgnore != null && !filesIgnore.IsRegex;

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
                        RootPath = rootPath,
                        IsBlacklisted = isFolderBlacklisted
                    });
                }

                // Process files in the current folder
                if (!filesAreNonRegexIgnored)
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
                            
                            bool fileIsRegexBlacklisted = filesAreRegexIgnored;
                            bool fileIsNonRegexBlacklisted = false;
                            
                            // Check if file itself matches any pattern (e.g. wildcard or regex rule)
                            var fileIgnoreRule = blacklists.FirstOrDefault(b =>
                                PathMatchesPattern(fileRelativePath.ToLowerInvariant(), b.Path)
                            );
                            if (fileIgnoreRule != null)
                            {
                                if (fileIgnoreRule.IsRegex)
                                {
                                    fileIsRegexBlacklisted = true;
                                }
                                else
                                {
                                    fileIsNonRegexBlacklisted = true;
                                }
                            }

                            if (!fileIsNonRegexBlacklisted)
                            {
                                inventory.Add(new FileInventoryItem
                                {
                                    Name = fileInfo.Name,
                                    RelativePath = fileRelativePath,
                                    IsFolder = false,
                                    Size = fileInfo.Length,
                                    LastModified = fileInfo.LastWriteTimeUtc,
                                    RootPath = rootPath,
                                    IsBlacklisted = fileIsRegexBlacklisted
                                });
                            }
                        }
                    }
                    catch (UnauthorizedAccessException) { }
                    catch (DirectoryNotFoundException) { }
                }

                // Recursively walk subdirectories
                try
                {
                    var subdirs = Directory.GetDirectories(currentDir);
                    Parallel.ForEach(subdirs, new ParallelOptions { MaxDegreeOfParallelism = 8 }, subdir =>
                    {
                        WalkDirectory(subdir, rootPath, blacklists, inventory, isFolderBlacklisted);
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

        private class BlacklistRule
        {
            public string Path { get; set; } = string.Empty;
            public string IgnoreCondition { get; set; } = string.Empty;
            public bool IsRegex { get; set; }
        }
    }
}
