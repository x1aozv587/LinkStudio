using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace LinkStudio.Core.Workspaces
{
    /// <summary>
    /// 基于本地 JSON 文件（.linkworkspace）的工作空间存储实现。
    /// 采用 SemaphoreSlim 串行化 I/O + 临时文件原子替换，防止异常退出损坏工作空间配置。
    /// </summary>
    public sealed class JsonWorkspaceStore : IWorkspaceStore
    {
        public const string FileExtension = ".linkworkspace";

        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        private readonly SemaphoreSlim _ioGate = new(1, 1);
        private bool _disposed;

        public JsonWorkspaceStore(string? directoryPath = null)
        {
            DirectoryPath = string.IsNullOrWhiteSpace(directoryPath)
                ? Path.Combine(AppContext.BaseDirectory, "workspaces")
                : directoryPath;
        }

        public string DirectoryPath { get; }

        public async Task<IReadOnlyList<WorkspaceDocument>> LoadAllAsync(CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await _ioGate.WaitAsync(ct);
            try
            {
                if (!Directory.Exists(DirectoryPath))
                {
                    return [];
                }

                var files = Directory.GetFiles(DirectoryPath, "*" + FileExtension, SearchOption.TopDirectoryOnly);
                var result = new List<WorkspaceDocument>(files.Length);

                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var json = await File.ReadAllTextAsync(file, Encoding.UTF8, ct);
                        var doc = TryDeserialize(json);
                        if (doc is not null)
                        {
                            result.Add(doc);
                        }
                        else
                        {
                            Log.Warning("工作空间文件 {FilePath} 内容为空或格式无效，已跳过", file);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception error)
                    {
                        // 单个损坏的工作空间文件不应阻止其余工作空间正常加载。
                        Log.Warning(error, "读取工作空间文件 {FilePath} 失败，已跳过", file);
                    }
                }

                return result
                    .OrderBy(d => d.CreatedAt)
                    .ThenBy(d => d.Name, StringComparer.Ordinal)
                    .ToArray();
            }
            finally
            {
                _ioGate.Release();
            }
        }

        public async Task SaveAsync(WorkspaceDocument document, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(document);
            ObjectDisposedException.ThrowIf(_disposed, this);

            await _ioGate.WaitAsync(ct);
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                Normalize(document);
                document.UpdatedAt = DateTimeOffset.UtcNow;

                var filePath = GetFilePath(document.Id);
                var tempPath = filePath + ".tmp";
                var json = Serialize(document);

                await File.WriteAllTextAsync(tempPath, json, Encoding.UTF8, ct);
                File.Move(tempPath, filePath, overwrite: true);
            }
            finally
            {
                _ioGate.Release();
            }
        }

        public async Task DeleteAsync(string workspaceId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(workspaceId))
            {
                return;
            }

            ObjectDisposedException.ThrowIf(_disposed, this);
            await _ioGate.WaitAsync(ct);
            try
            {
                var filePath = GetFilePath(workspaceId);
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
            finally
            {
                _ioGate.Release();
            }
        }

        public string Serialize(WorkspaceDocument document)
        {
            ArgumentNullException.ThrowIfNull(document);
            Normalize(document);
            return JsonSerializer.Serialize(document, SerializerOptions);
        }

        public WorkspaceDocument? TryDeserialize(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                var doc = JsonSerializer.Deserialize<WorkspaceDocument>(json, SerializerOptions);
                if (doc is null)
                {
                    return null;
                }

                Normalize(doc);
                return doc;
            }
            catch (JsonException error)
            {
                Log.Warning(error, "解析工作空间 JSON 失败");
                return null;
            }
        }

        private string GetFilePath(string workspaceId)
        {
            // 仅保留安全文件名字符，防止异常 Id 造成路径穿越或非法文件名。
            var safeChars = workspaceId
                .Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_')
                .ToArray();
            var safeId = safeChars.Length > 0 ? new string(safeChars) : Guid.NewGuid().ToString("N")[..10];
            return Path.Combine(DirectoryPath, safeId + FileExtension);
        }

        private static void Normalize(WorkspaceDocument doc)
        {
            if (doc.SchemaVersion <= 0)
            {
                doc.SchemaVersion = WorkspaceDocument.CurrentSchemaVersion;
            }

            if (string.IsNullOrWhiteSpace(doc.Id))
            {
                doc.Id = Guid.NewGuid().ToString("N")[..10];
            }

            if (string.IsNullOrWhiteSpace(doc.Name))
            {
                doc.Name = "未命名画板";
            }

            doc.Widgets ??= [];
            foreach (var widget in doc.Widgets)
            {
                if (string.IsNullOrWhiteSpace(widget.Id))
                {
                    widget.Id = Guid.NewGuid().ToString("N")[..8];
                }

                widget.ColSpan = Math.Clamp(widget.ColSpan, 3, 12);
                widget.DataSourceId = string.IsNullOrWhiteSpace(widget.DataSourceId) ? "*" : widget.DataSourceId.Trim();
                widget.BoundFields ??= [];
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _ioGate.Dispose();
        }
    }
}
