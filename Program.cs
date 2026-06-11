using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RepoScanner.Models;
using RepoScanner.Services;
using System.Linq;
using MiniExcelLibs;

var builder = WebApplication.CreateBuilder(args);

// Load mock config
var mockConfig = builder.Configuration.GetSection("MockTesting");
bool enableMock = mockConfig.GetValue<bool>("EnableMock");
string mockBaseServer = mockConfig.GetValue<string>("MockBaseServer") ?? "Server-1";
string mockTargetServer = mockConfig.GetValue<string>("MockTargetServer") ?? "Server-2";
string mockBasePath = mockConfig.GetValue<string>("MockBasePath") ?? "C:\\MockBase";
string mockTargetPath = mockConfig.GetValue<string>("MockTargetPath") ?? "C:\\MockTarget";

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

// Helper to get configuration file path
string GetConfigPath() => Path.Combine(AppContext.BaseDirectory, "config.json");
string GetSnapshotDir() => Path.Combine(AppContext.BaseDirectory, "snapshots");

// Guarantee configurations exist
if (!File.Exists(GetConfigPath()))
{
    var sampleConfigs = new List<ServerConfig>();
    for (int i = 1; i <= 8; i++)
    {
        sampleConfigs.Add(new ServerConfig
        {
            Name = $"Server-{i}",
            NetworkRootPaths = new List<string> { $"\\\\Server{i}\\D$", $"\\\\Server{i}\\E$" }
        });
    }
    File.WriteAllText(GetConfigPath(), JsonSerializer.Serialize(sampleConfigs, new JsonSerializerOptions { WriteIndented = true }));
}

if (!Directory.Exists(GetSnapshotDir()))
{
    Directory.CreateDirectory(GetSnapshotDir());
}

// Helper to fetch server config by name
ServerConfig? GetServerConfig(string name)
{
    if (!File.Exists(GetConfigPath())) return null;
    var json = File.ReadAllText(GetConfigPath());
    var configs = JsonSerializer.Deserialize<List<ServerConfig>>(json);
    var target = configs?.Find(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    if (target != null && enableMock)
    {
        // Intercept paths if mock testing is enabled
        if (target.Name.Equals(mockBaseServer, StringComparison.OrdinalIgnoreCase))
        {
            target.NetworkRootPaths = new List<string> { mockBasePath };
        }
        else if (target.Name.Equals(mockTargetServer, StringComparison.OrdinalIgnoreCase))
        {
            target.NetworkRootPaths = new List<string> { mockTargetPath };
        }
    }
    return target;
}

// Endpoints

app.MapGet("/api/mock-status", () => Results.Ok(new { enableMock }));

// 1. Get configs
app.MapGet("/api/config", () =>
{
    if (!File.Exists(GetConfigPath())) return Results.NotFound("config.json not found");
    var json = File.ReadAllText(GetConfigPath());
    var configs = JsonSerializer.Deserialize<List<ServerConfig>>(json);
    if (configs != null)
    {
        foreach (var c in configs)
        {
            var matched = GetServerConfig(c.Name);
            if (matched != null)
            {
                c.NetworkRootPaths = matched.NetworkRootPaths;
            }
        }
        return Results.Json(configs);
    }
    return Results.Content(json, "application/json");
});

// 2. Save configs
app.MapPost("/api/config", async (HttpContext context) =>
{
    using var reader = new StreamReader(context.Request.Body);
    var body = await reader.ReadToEndAsync();
    // Validate deserializable
    var parsed = JsonSerializer.Deserialize<List<ServerConfig>>(body);
    if (parsed == null) return Results.BadRequest("Invalid payload");
    
    File.WriteAllText(GetConfigPath(), JsonSerializer.Serialize(parsed, new JsonSerializerOptions { WriteIndented = true }));
    return Results.Ok();
});

// 3. Trigger Server Scan (Saves to snapshot files)
app.MapPost("/api/scan", async (ScanRequest req) =>
{
    if (string.IsNullOrEmpty(req.Path) || string.IsNullOrEmpty(req.Name))
    {
        return Results.BadRequest("Path and Name are required.");
    }

    var snapshot = await ScanEngine.ScanServerAsync(req.Name, req.Path, req.Blacklist);
    
    string snapshotPath = Path.Combine(GetSnapshotDir(), $"snapshot_{req.Name}.json");
    File.WriteAllText(snapshotPath, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));

    return Results.Ok(new { message = $"Successfully scanned {req.Name}", totalFiles = snapshot.TotalFiles });
});

// 4. List Snapshots in Cache
app.MapGet("/api/snapshots", () =>
{
    var dir = GetSnapshotDir();
    var files = Directory.GetFiles(dir, "snapshot_*.json");
    var results = new List<object>();

    foreach (var file in files)
    {
        try
        {
            var content = File.ReadAllText(file);
            var snapshot = JsonSerializer.Deserialize<ScanSnapshot>(content);
            if (snapshot != null)
            {
                results.Add(new
                {
                    serverName = snapshot.ServerName,
                    scannedAt = snapshot.ScannedAt,
                    totalFiles = snapshot.TotalFiles,
                    totalFolders = snapshot.TotalFolders
                });
            }
        }
        catch { }
    }

    return Results.Ok(results);
});

// 5. Download Raw Snapshot Report
app.MapGet("/api/snapshots/{serverName}/download", (string serverName) =>
{
    string snapshotPath = Path.Combine(GetSnapshotDir(), $"snapshot_{serverName}.json");
    if (!File.Exists(snapshotPath))
    {
        return Results.NotFound($"Snapshot not found for server '{serverName}'");
    }
    
    return Results.File(snapshotPath, "application/json", $"snapshot_{serverName}.json");
});

// 5.5 Delete Snapshot Cache
app.MapDelete("/api/snapshots/{serverName}", (string serverName) =>
{
    string snapshotPath = Path.Combine(GetSnapshotDir(), $"snapshot_{serverName}.json");
    if (!File.Exists(snapshotPath))
    {
        return Results.NotFound($"Snapshot not found for server '{serverName}'");
    }
    try
    {
        File.Delete(snapshotPath);
        return Results.Ok();
    }
    catch (Exception ex)
    {
        return Results.Problem($"Failed to delete snapshot: {ex.Message}");
    }
});

// 6. Compare Base and Target Server Snapshots
app.MapPost("/api/compare", (CompareRequest req) =>
{
    string baseSnapPath = Path.Combine(GetSnapshotDir(), $"snapshot_{req.BaseServer}.json");
    string targetSnapPath = Path.Combine(GetSnapshotDir(), $"snapshot_{req.TargetServer}.json");

    if (!File.Exists(baseSnapPath) || !File.Exists(targetSnapPath))
    {
        return Results.BadRequest("Snapshots for one or both servers do not exist in cache. Please perform a scan first.");
    }

    var baseSnap = JsonSerializer.Deserialize<ScanSnapshot>(File.ReadAllText(baseSnapPath));
    var targetSnap = JsonSerializer.Deserialize<ScanSnapshot>(File.ReadAllText(targetSnapPath));

    if (baseSnap == null || targetSnap == null)
    {
        return Results.BadRequest("Failed to load one or both snapshots.");
    }

    var diffs = DiffEngine.CompareSnapshots(baseSnap, targetSnap);
    return Results.Ok(diffs);
});

// 6.5 Download Excel Comparison Report
app.MapPost("/api/compare/excel-download", (CompareRequest req) =>
{
    string baseSnapPath = Path.Combine(GetSnapshotDir(), $"snapshot_{req.BaseServer}.json");
    string targetSnapPath = Path.Combine(GetSnapshotDir(), $"snapshot_{req.TargetServer}.json");

    if (!File.Exists(baseSnapPath) || !File.Exists(targetSnapPath))
    {
        return Results.BadRequest("Snapshots for one or both servers do not exist in cache. Please perform a scan first.");
    }

    var baseSnap = JsonSerializer.Deserialize<ScanSnapshot>(File.ReadAllText(baseSnapPath));
    var targetSnap = JsonSerializer.Deserialize<ScanSnapshot>(File.ReadAllText(targetSnapPath));

    if (baseSnap == null || targetSnap == null)
    {
        return Results.BadRequest("Failed to load one or both snapshots.");
    }

    var diffs = DiffEngine.CompareSnapshots(baseSnap, targetSnap);
    
    var memoryStream = new MemoryStream();
    var rows = diffs.Select(d => new {
        Accept = false,
        RelativePath = d.RelativePath,
        ActionType = d.ActionType,
        IsFolder = d.IsFolder,
        BaseSize = d.BaseSize,
        TargetSize = d.TargetSize,
        BaseModified = d.BaseModified,
        TargetModified = d.TargetModified,
        BaseRootPath = d.BaseRootPath,
        TargetRootPath = d.TargetRootPath,
        Name = d.Name,
        Status = d.Status
    });
    
    MiniExcel.SaveAs(memoryStream, rows);
    memoryStream.Position = 0;
    
    return Results.File(memoryStream, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"reposync_comparison_{req.BaseServer}_vs_{req.TargetServer}.xlsx");
});

// 6.6 Upload Excel to Sync Changes
app.MapPost("/api/compare/excel-upload", async (HttpRequest request) =>
{
    if (!request.HasFormContentType) return Results.BadRequest("Expected a multipart form content type.");
    var form = await request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file == null || file.Length == 0) return Results.BadRequest("No file uploaded.");

    string baseServer = form["baseServer"].ToString();
    string targetServer = form["targetServer"].ToString();

    if (string.IsNullOrEmpty(baseServer) || string.IsNullOrEmpty(targetServer))
    {
        return Results.BadRequest("baseServer and targetServer names are required.");
    }

    var tempPath = Path.GetTempFileName();
    using (var stream = new FileStream(tempPath, FileMode.Create))
    {
        await file.CopyToAsync(stream);
    }

    List<DiffItem> itemsToSync = new();
    try
    {
        var rows = MiniExcel.Query(tempPath, excelType: ExcelType.XLSX).ToList();
        foreach (IDictionary<string, object> row in rows.Skip(1)) // Skip header row
        {
            string getVal(params string[] keys)
            {
                foreach (var k in keys)
                {
                    if (row.TryGetValue(k, out var val) && val != null)
                        return val.ToString()!;
                }
                return "";
            }

            bool getBool(params string[] keys)
            {
                var valStr = getVal(keys).ToLowerInvariant();
                return valStr == "true" || valStr == "1" || valStr == "yes" || valStr == "y" || valStr == "checked";
            }

            long? getLong(params string[] keys)
            {
                var valStr = getVal(keys);
                return long.TryParse(valStr, out var res) ? res : null;
            }

            DateTime? getDate(params string[] keys)
            {
                var valStr = getVal(keys);
                return DateTime.TryParse(valStr, out var res) ? res : null;
            }

            bool accept = getBool("A", "Accept");
            if (!accept) continue;

            var item = new DiffItem
            {
                RelativePath = getVal("B", "RelativePath"),
                ActionType = getVal("C", "ActionType"),
                IsFolder = getBool("D", "IsFolder"),
                BaseSize = getLong("E", "BaseSize"),
                TargetSize = getLong("F", "TargetSize"),
                BaseModified = getDate("G", "BaseModified"),
                TargetModified = getDate("H", "TargetModified"),
                BaseRootPath = getVal("I", "BaseRootPath"),
                TargetRootPath = getVal("J", "TargetRootPath"),
                Name = getVal("K", "Name"),
                Status = "Pending"
            };
            itemsToSync.Add(item);
        }
    }
    finally
    {
        if (File.Exists(tempPath)) File.Delete(tempPath);
    }

    if (itemsToSync.Count == 0)
    {
        return Results.BadRequest("No accepted items found in the Excel sheet (make sure column A is marked TRUE/checked/1).");
    }

    string targetRootPath = itemsToSync[0].TargetRootPath;
    if (string.IsNullOrEmpty(targetRootPath))
    {
        string targetSnapPath = Path.Combine(GetSnapshotDir(), $"snapshot_{targetServer}.json");
        if (File.Exists(targetSnapPath))
        {
            var targetSnap = JsonSerializer.Deserialize<ScanSnapshot>(File.ReadAllText(targetSnapPath));
            targetRootPath = targetSnap?.RootPath ?? "";
        }
    }

    if (string.IsNullOrEmpty(targetRootPath))
    {
        return Results.BadRequest("Target server root path not resolved in items or snapshot.");
    }

    // Ensure all items have root paths set if they were missing in Excel
    foreach (var item in itemsToSync)
    {
        if (string.IsNullOrEmpty(item.TargetRootPath))
        {
            item.TargetRootPath = targetRootPath;
        }
        if (string.IsNullOrEmpty(item.BaseRootPath))
        {
            string baseSnapPath = Path.Combine(GetSnapshotDir(), $"snapshot_{baseServer}.json");
            if (File.Exists(baseSnapPath))
            {
                var baseSnap = JsonSerializer.Deserialize<ScanSnapshot>(File.ReadAllText(baseSnapPath));
                item.BaseRootPath = baseSnap?.RootPath ?? "";
            }
        }
    }

    var resolvedItems = new List<DiffItem>();
    foreach (var item in itemsToSync)
    {
        var resolved = await SyncController.ResolveActionAsync(item, item.TargetRootPath);
        resolvedItems.Add(resolved);
    }

    // Write Sync Action Report (JSON)
    try
    {
        string logsBackupRoot = Path.Combine(targetRootPath, "__reposync", "logs");
        Directory.CreateDirectory(logsBackupRoot);
        
        string reportFilename = $"sync_report_{targetServer}_to_{baseServer}.json";
        string reportFullPath = Path.Combine(logsBackupRoot, reportFilename);
        
        List<object> allActions = new List<object>();
        
        if (File.Exists(reportFullPath))
        {
            try
            {
                var existingJson = File.ReadAllText(reportFullPath);
                var existingData = JsonSerializer.Deserialize<JsonElement>(existingJson);
                if (existingData.TryGetProperty("Actions", out var actionsProp) && actionsProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var elem in actionsProp.EnumerateArray())
                    {
                        var deserializedElem = JsonSerializer.Deserialize<object>(elem.GetRawText());
                        if (deserializedElem != null)
                        {
                            allActions.Add(deserializedElem);
                        }
                    }
                }
            }
            catch {}
        }
        
        foreach (var item in resolvedItems)
        {
            allActions.Add(new {
                item.RelativePath,
                item.Name,
                item.IsFolder,
                item.ActionType,
                item.BaseSize,
                item.TargetSize,
                item.BaseModified,
                item.TargetModified,
                item.Status,
                Timestamp = DateTime.UtcNow,
                item.BaseRootPath,
                item.TargetRootPath
            });
        }
        
        var reportData = new
        {
            SourceServer = baseServer,
            TargetServer = targetServer,
            LastSyncTimestamp = DateTime.UtcNow,
            Actions = allActions
        };
        
        File.WriteAllText(reportFullPath, JsonSerializer.Serialize(reportData, new JsonSerializerOptions { WriteIndented = true }));
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Excel Sync Report Error: {ex.Message}");
    }

    return Results.Ok(resolvedItems);
});

// 7. Real-Time Sync Action
app.MapPost("/api/sync", async (SyncRequest req) =>
{
    if (req.Items == null || req.Items.Count == 0)
    {
        return Results.Ok(new List<DiffItem>());
    }

    string targetRootPath = req.Items[0].TargetRootPath;
    if (string.IsNullOrEmpty(targetRootPath))
    {
        return Results.BadRequest("Target server root path not resolved in items.");
    }

    var resolvedItems = new List<DiffItem>();
    foreach (var item in req.Items)
    {
        var resolved = await SyncController.ResolveActionAsync(item, item.TargetRootPath);
        resolvedItems.Add(resolved);
    }

    // Write Sync Action Report (JSON)
    try
    {
        string logsBackupRoot = Path.Combine(targetRootPath, "__reposync", "logs");
        Directory.CreateDirectory(logsBackupRoot);
        
        string reportFilename = $"sync_report_{req.TargetServer}_to_{req.BaseServer}.json";
        string reportFullPath = Path.Combine(logsBackupRoot, reportFilename);
        
        List<object> allActions = new List<object>();
        
        if (File.Exists(reportFullPath))
        {
            try
            {
                var existingJson = File.ReadAllText(reportFullPath);
                var existingData = JsonSerializer.Deserialize<JsonElement>(existingJson);
                if (existingData.TryGetProperty("Actions", out var actionsProp) && actionsProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var elem in actionsProp.EnumerateArray())
                    {
                        var deserializedElem = JsonSerializer.Deserialize<object>(elem.GetRawText());
                        if (deserializedElem != null)
                        {
                            allActions.Add(deserializedElem);
                        }
                    }
                }
            }
            catch {}
        }
        
        foreach (var item in resolvedItems)
        {
            allActions.Add(new {
                item.RelativePath,
                item.Name,
                item.IsFolder,
                item.ActionType,
                item.BaseSize,
                item.TargetSize,
                item.BaseModified,
                item.TargetModified,
                item.Status,
                Timestamp = DateTime.UtcNow,
                item.BaseRootPath,
                item.TargetRootPath
            });
        }
        
        var reportData = new
        {
            SourceServer = req.BaseServer,
            TargetServer = req.TargetServer,
            LastSyncTimestamp = DateTime.UtcNow,
            Actions = allActions
        };
        
        File.WriteAllText(reportFullPath, JsonSerializer.Serialize(reportData, new JsonSerializerOptions { WriteIndented = true }));
    }
    catch { }

    return Results.Ok(resolvedItems);
});

// 8. Revert Sync Action Endpoint
app.MapPost("/api/revert", async (SyncRequest req) =>
{
    if (req.Items == null || req.Items.Count == 0)
    {
        return Results.Ok(new List<DiffItem>());
    }

    string targetRootPath = req.Items[0].TargetRootPath;
    if (string.IsNullOrEmpty(targetRootPath))
    {
        return Results.BadRequest("Target server root path not resolved in items.");
    }

    string folderName = Path.GetFileName(targetRootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? "root";
    string backupRoot = Path.Combine(targetRootPath, "__reposync", "removed items", folderName);

    var resolvedItems = new List<DiffItem>();
    foreach (var item in req.Items)
    {
        string targetFullPath = Path.Combine(targetRootPath, item.RelativePath);

        try
        {
            if (item.ActionType == "REMOVE")
            {
                if (item.IsFolder)
                {
                    if (Directory.Exists(targetFullPath))
                    {
                        Directory.Delete(targetFullPath, recursive: true);
                    }
                }
                else
                {
                    if (File.Exists(targetFullPath))
                    {
                        File.Delete(targetFullPath);
                    }
                }
                item.Status = "Success";
            }
            else if (item.ActionType == "ADD" || item.ActionType == "MODIFY")
            {
                string backupPath = Path.Combine(backupRoot, item.RelativePath);
                if (item.IsFolder)
                {
                    Directory.CreateDirectory(targetFullPath);
                    item.Status = "Success";
                }
                else
                {
                    if (!File.Exists(backupPath))
                    {
                        item.Status = $"Failed: Backup file not found";
                    }
                    else
                    {
                        string? parentDir = Path.GetDirectoryName(targetFullPath);
                        if (!string.IsNullOrEmpty(parentDir))
                        {
                            Directory.CreateDirectory(parentDir);
                        }
                        File.Copy(backupPath, targetFullPath, overwrite: true);
                        item.Status = "Success";
                    }
                }
            }
            else
            {
                item.Status = "Failed: Unknown revert action type";
            }
        }
        catch (Exception ex)
        {
            item.Status = $"Failed: {ex.Message}";
        }

        resolvedItems.Add(item);
    }

    return Results.Ok(resolvedItems);
});

// 9. List logs for target server
app.MapGet("/api/logs", (string targetRootPath) =>
{
    if (string.IsNullOrEmpty(targetRootPath))
    {
        return Results.BadRequest("targetRootPath is required.");
    }

    string logsDir = Path.Combine(targetRootPath, "__reposync", "logs");

    var list = new List<object>();
    if (Directory.Exists(logsDir))
    {
        var files = Directory.GetFiles(logsDir, "*.json");
        foreach (var file in files)
        {
            var info = new FileInfo(file);
            list.Add(new
            {
                filename = info.Name,
                sizeBytes = info.Length,
                lastWriteTime = info.LastWriteTimeUtc
            });
        }
    }

    return Results.Ok(list);
});

// 10. Download specific log file
app.MapGet("/api/logs/download", (string targetRootPath, string filename) =>
{
    if (string.IsNullOrEmpty(targetRootPath))
    {
        return Results.BadRequest("targetRootPath is required.");
    }

    string fileFullPath = Path.Combine(targetRootPath, "__reposync", "logs", filename);

    if (!File.Exists(fileFullPath))
    {
        return Results.NotFound("Log file not found.");
    }

    return Results.File(fileFullPath, "application/json", filename);
});

app.Run();
