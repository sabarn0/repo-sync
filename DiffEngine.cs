using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RepoScanner.Models;

namespace RepoScanner.Services
{
    public static class DiffEngine
    {
        public static List<DiffItem> CompareSnapshots(ScanSnapshot baseSnap, ScanSnapshot targetSnap)
        {
            var diffs = new List<DiffItem>();

            // Map structures by relative path (case-insensitive keys for Windows compatibility)
            var baseDict = baseSnap.Inventory.ToDictionary(item => item.RelativePath.Replace("/", "\\").ToLowerInvariant(), item => item);
            var targetDict = targetSnap.Inventory.ToDictionary(item => item.RelativePath.Replace("/", "\\").ToLowerInvariant(), item => item);

            // 1. Check for Additions or Modifications (Base compared to Target)
            foreach (var baseKv in baseDict)
            {
                var baseItem = baseKv.Value;
                var normalizedPath = baseKv.Key;

                if (baseItem.IsBlacklisted && targetDict.ContainsKey(normalizedPath))
                {
                    continue; // Skip blacklisted path that exists in target
                }

                if (!targetDict.TryGetValue(normalizedPath, out var targetItem))
                {
                    if (!IsUnnecessaryNonSyncFile(baseItem.RelativePath, baseItem.IsFolder))
                    {
                        // Item exists in Base but is missing in Target -> ADD to Target
                        diffs.Add(new DiffItem
                        {
                            RelativePath = baseItem.RelativePath,
                            Name = baseItem.Name,
                            IsFolder = baseItem.IsFolder,
                            ActionType = "ADD",
                            BaseSize = baseItem.IsFolder ? null : baseItem.Size,
                            TargetSize = null,
                            BaseModified = baseItem.LastModified,
                            TargetModified = null,
                            BaseRootPath = baseSnap.RootPath,
                            TargetRootPath = targetSnap.RootPath
                        });
                    }
                }
            }

            // 2. Check for Deletions (Target compared to Base)
            foreach (var targetKv in targetDict)
            {
                var targetItem = targetKv.Value;
                var normalizedPath = targetKv.Key;

                if (targetItem.IsBlacklisted)
                {
                    continue; // Skip blacklisted target items
                }

                if (!baseDict.ContainsKey(normalizedPath))
                {
                    if (!IsUnnecessaryNonSyncFile(targetItem.RelativePath, targetItem.IsFolder))
                    {
                        // Item exists in Target but is missing in Base -> REMOVE from Target
                        diffs.Add(new DiffItem
                        {
                            RelativePath = targetItem.RelativePath,
                            Name = targetItem.Name,
                            IsFolder = targetItem.IsFolder,
                            ActionType = "REMOVE",
                            BaseSize = null,
                            TargetSize = targetItem.IsFolder ? null : targetItem.Size,
                            BaseModified = null,
                            TargetModified = targetItem.LastModified,
                            BaseRootPath = baseSnap.RootPath,
                            TargetRootPath = targetSnap.RootPath
                        });
                    }
                }
            }

            // Order diffs: directories first (sorted by length so root directories get created/deleted in order)
            // For REMOVE action, we should delete files first then folders.
            // For ADD action, we should create folders first then copy files.
            return diffs.OrderBy(d => d.IsFolder ? 0 : 1)
                        .ThenBy(d => d.RelativePath.Length)
                        .ToList();
        }

        private static bool IsUnnecessaryNonSyncFile(string relativePath, bool isFolder)
        {
            if (isFolder) return false;

            var lowerPath = relativePath.ToLowerInvariant();

            // Ignore common log/temporary file extensions
            if (lowerPath.EndsWith(".log") || lowerPath.EndsWith(".tmp") || lowerPath.EndsWith(".temp"))
            {
                return true;
            }

            // Ignore files in directories (or filenames) containing log, report, output, temp, tmp keywords
            var segments = lowerPath.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var segment in segments)
            {
                if (segment.Contains("log") || 
                    segment.Contains("report") || 
                    segment.Contains("output") || 
                    segment.Contains("temp") || 
                    segment.Contains("tmp"))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
