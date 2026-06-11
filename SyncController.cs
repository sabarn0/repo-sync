using System;
using System.IO;
using System.Threading.Tasks;
using RepoScanner.Models;

namespace RepoScanner.Services
{
    public static class SyncController
    {
        public static async Task<DiffItem> ResolveActionAsync(DiffItem item, string targetServerRootPath)
        {
            try
            {
                // Ensure target server root path is configured and exists
                if (string.IsNullOrEmpty(targetServerRootPath))
                {
                    item.Status = "Failed: Missing Target Server Root Path configuration";
                    return item;
                }

                // Locate the target system's path mapping
                string targetFullPath = Path.Combine(targetServerRootPath, item.RelativePath);

                if (item.ActionType == "ADD" || item.ActionType == "MODIFY")
                {
                    // Copy file/directory from BaseRootPath to target
                    string sourceFullPath = Path.Combine(item.BaseRootPath, item.RelativePath);

                    if (!File.Exists(sourceFullPath) && !Directory.Exists(sourceFullPath))
                    {
                        item.Status = $"Failed: Source path does not exist '{sourceFullPath}'";
                        return item;
                    }

                    // For MODIFY, if the file exists on the target, back it up first
                    if (item.ActionType == "MODIFY" && !item.IsFolder && File.Exists(targetFullPath))
                    {
                        try
                        {
                            string folderName = Path.GetFileName(targetServerRootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? "root";
                            string backupRoot = Path.Combine(targetServerRootPath, "__reposync", "removed items", folderName);
                            string backupPath = Path.Combine(backupRoot, item.RelativePath);
                            string? backupDir = Path.GetDirectoryName(backupPath);
                            if (!string.IsNullOrEmpty(backupDir))
                            {
                                Directory.CreateDirectory(backupDir);
                            }
                            File.Copy(targetFullPath, backupPath, overwrite: true);
                        }
                        catch {}
                    }

                    if (item.IsFolder)
                    {
                        Directory.CreateDirectory(targetFullPath);
                        item.Status = "Success";
                    }
                    else
                    {
                        // Ensure containing folder exists
                        string? parentDir = Path.GetDirectoryName(targetFullPath);
                        if (!string.IsNullOrEmpty(parentDir))
                        {
                            Directory.CreateDirectory(parentDir);
                        }

                        // Asynchronous buffer-based copy
                        await CopyFileAsync(sourceFullPath, targetFullPath);
                        
                        // Set the Last Write Time to match base exactly
                        if (item.BaseModified.HasValue)
                        {
                            File.SetLastWriteTimeUtc(targetFullPath, item.BaseModified.Value);
                        }

                        item.Status = "Success";
                    }
                }
                else if (item.ActionType == "REMOVE")
                {
                    // Resolve backup root folder path
                    string folderName = Path.GetFileName(targetServerRootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? "root";
                    string removedItemsRoot = Path.Combine(targetServerRootPath, "__reposync", "removed items", folderName);
                    
                    Directory.CreateDirectory(removedItemsRoot);

                    if (item.IsFolder)
                    {
                        if (Directory.Exists(targetFullPath))
                        {
                            string folderBackupPath = Path.Combine(removedItemsRoot, item.RelativePath);
                            string? parentFolder = Path.GetDirectoryName(folderBackupPath);
                            if (!string.IsNullOrEmpty(parentFolder))
                            {
                                Directory.CreateDirectory(parentFolder);
                            }

                            if (Directory.Exists(folderBackupPath))
                            {
                                Directory.Delete(folderBackupPath, recursive: true);
                            }
                            
                            Directory.Move(targetFullPath, folderBackupPath);
                        }
                        item.Status = "Success";
                    }
                    else
                    {
                        if (File.Exists(targetFullPath))
                        {
                            string fileBackupPath = Path.Combine(removedItemsRoot, item.RelativePath);
                            string? parentDir = Path.GetDirectoryName(fileBackupPath);
                            if (!string.IsNullOrEmpty(parentDir))
                            {
                                Directory.CreateDirectory(parentDir);
                            }

                            if (File.Exists(fileBackupPath))
                            {
                                File.Delete(fileBackupPath);
                            }

                            File.Move(targetFullPath, fileBackupPath);
                        }
                        item.Status = "Success";
                    }
                }
                else
                {
                    item.Status = "Failed: Unknown action type";
                }
            }
            catch (Exception ex)
            {
                item.Status = $"Failed: {ex.Message}";
            }

            return item;
        }

        private static async Task CopyFileAsync(string sourcePath, string destPath)
        {
            using (var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true))
            using (var destStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await sourceStream.CopyToAsync(destStream);
            }
        }
    }
}
