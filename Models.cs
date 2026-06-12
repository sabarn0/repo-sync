using System;
using System.Collections.Generic;

namespace RepoScanner.Models
{
    public class ServerConfig
    {
        public string Name { get; set; } = string.Empty;
        public List<string> NetworkRootPaths { get; set; } = new();
        public List<BlacklistItem> Blacklist { get; set; } = new();
    }

    public class BlacklistItem
    {
        public string Path { get; set; } = string.Empty; // e.g. "Temp", "D:\Project\Bin"
        public string IgnoreCondition { get; set; } = "IGNORE_ENTIRE_FOLDER"; // "IGNORE_ENTIRE_FOLDER" or "IGNORE_FILES_ONLY"
    }

    public class FileInventoryItem
    {
        public string Name { get; set; } = string.Empty;
        public string RelativePath { get; set; } = string.Empty; // Relative path from the drive/root path
        public bool IsFolder { get; set; }
        public long Size { get; set; }
        public DateTime LastModified { get; set; }
        public string RootPath { get; set; } = string.Empty; // Keep track of which root drive/UNC path this belongs to
        public bool IsBlacklisted { get; set; }
    }

    public class ScanSnapshot
    {
        public string ServerName { get; set; } = string.Empty;
        public string RootPath { get; set; } = string.Empty;
        public DateTime ScannedAt { get; set; }
        public int TotalFiles { get; set; }
        public int TotalFolders { get; set; }
        public List<FileInventoryItem> Inventory { get; set; } = new();
    }

    public class DiffItem
    {
        public string RelativePath { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public bool IsFolder { get; set; }
        public string ActionType { get; set; } = string.Empty; // "ADD" (exists in base, missing in target), "REMOVE" (missing in base, exists in target), "MODIFY" (exists in both, mismatch in size/modified)
        public long? BaseSize { get; set; }
        public long? TargetSize { get; set; }
        public DateTime? BaseModified { get; set; }
        public DateTime? TargetModified { get; set; }
        public string Status { get; set; } = "Pending"; // Pending, Success, Failed
        public string BaseRootPath { get; set; } = string.Empty;
        public string TargetRootPath { get; set; } = string.Empty;
    }
    
    public class CompareRequest
    {
        public string BaseServer { get; set; } = string.Empty;
        public string TargetServer { get; set; } = string.Empty;
    }

    public class SyncRequest
    {
        public string BaseServer { get; set; } = string.Empty;
        public string TargetServer { get; set; } = string.Empty;
        public List<DiffItem> Items { get; set; } = new();
    }

    public class ScanRequest
    {
        public string Path { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public List<BlacklistItem> Blacklist { get; set; } = new();
    }
}
