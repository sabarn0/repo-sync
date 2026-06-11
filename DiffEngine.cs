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

                if (!targetDict.TryGetValue(normalizedPath, out var targetItem))
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
                else
                {
                    // Item exists in both places. If it's a file, verify Size and Modified date.
                    if (!baseItem.IsFolder && !targetItem.IsFolder)
                    {
                        // Check for size difference or modified difference
                        bool sizeDiff = baseItem.Size != targetItem.Size;
                        // Precision check: allow 2-second window for network systems/FAT copy adjustments
                        bool timeDiff = Math.Abs((baseItem.LastModified - targetItem.LastModified).TotalSeconds) > 2;

                        if (sizeDiff || timeDiff)
                        {
                            diffs.Add(new DiffItem
                            {
                                RelativePath = baseItem.RelativePath,
                                Name = baseItem.Name,
                                IsFolder = false,
                                ActionType = "MODIFY",
                                BaseSize = baseItem.Size,
                                TargetSize = targetItem.Size,
                                BaseModified = baseItem.LastModified,
                                TargetModified = targetItem.LastModified,
                                BaseRootPath = baseSnap.RootPath,
                                TargetRootPath = targetSnap.RootPath
                            });
                        }
                    }
                }
            }

            // 2. Check for Deletions (Target compared to Base)
            foreach (var targetKv in targetDict)
            {
                var targetItem = targetKv.Value;
                var normalizedPath = targetKv.Key;

                if (!baseDict.ContainsKey(normalizedPath))
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

            // Order diffs: directories first (sorted by length so root directories get created/deleted in order)
            // For REMOVE action, we should delete files first then folders.
            // For ADD action, we should create folders first then copy files.
            return diffs.OrderBy(d => d.IsFolder ? 0 : 1)
                        .ThenBy(d => d.RelativePath.Length)
                        .ToList();
        }
    }
}
