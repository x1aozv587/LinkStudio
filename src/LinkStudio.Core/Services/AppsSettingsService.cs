using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LinkStudio.Core.Models;
using LinkStudio.Core.Services.Interfaces;
using Serilog;

namespace LinkStudio.Core.Services
{
    /// <summary>
    /// 基于本地 JSON 文件的配置持久化服务。
    /// 通过 SemaphoreSlim 串行化读写，并用“写临时文件 + 原子替换”避免异常退出损坏配置。
    /// </summary>
    public sealed class AppsSettingsService : IAppsSettingsService
    {
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        private readonly string _filePath;
        private readonly SemaphoreSlim _ioGate = new(1, 1);
        private bool _disposed;

        public AppsSettingsService(string? filePath = null)
        {
            _filePath = string.IsNullOrWhiteSpace(filePath)
                ? Path.Combine(AppContext.BaseDirectory, "config", "settings.json")
                : filePath;
        }

        public string FilePath => _filePath;

        public async Task<AppsSettings> LoadSettingsAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await _ioGate.WaitAsync(cancellationToken);
            try
            {
                if (!File.Exists(_filePath))
                {
                    return AppsSettings.CreateDefault();
                }

                var json = await File.ReadAllTextAsync(_filePath, Encoding.UTF8, cancellationToken);
                var loaded = JsonSerializer.Deserialize<AppsSettings>(json, SerializerOptions);
                return Normalize(loaded);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                // 配置文件损坏或无法解析时记录警告并回退默认，保证软件仍可正常启动。
                Log.Warning(error, "读取配置文件 {FilePath} 失败，已回退默认配置", _filePath);
                return AppsSettings.CreateDefault();
            }
            finally
            {
                _ioGate.Release();
            }
        }

        public async Task SaveSettingsAsync(AppsSettings appsSettings, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(appsSettings);
            ObjectDisposedException.ThrowIf(_disposed, this);

            await _ioGate.WaitAsync(cancellationToken);
            try
            {
                var directory = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var normalized = Normalize(appsSettings);
                var json = JsonSerializer.Serialize(normalized, SerializerOptions);
                var tempPath = _filePath + ".tmp";

                await File.WriteAllTextAsync(tempPath, json, Encoding.UTF8, cancellationToken);
                File.Move(tempPath, _filePath, overwrite: true);
            }
            finally
            {
                _ioGate.Release();
            }
        }

        private static AppsSettings Normalize(AppsSettings? settings)
        {
            var defaults = AppsSettings.CreateDefault();
            if (settings is null)
            {
                return defaults;
            }

            settings.Studio ??= defaults.Studio;
            settings.Connection ??= defaults.Connection;
            settings.Receive ??= defaults.Receive;
            settings.Send ??= defaults.Send;
            settings.SendPreset ??= defaults.SendPreset;

            if (string.IsNullOrWhiteSpace(settings.Studio.AppLogDirectory))
            {
                settings.Studio.AppLogDirectory = defaults.Studio.AppLogDirectory;
            }

            if (string.IsNullOrWhiteSpace(settings.Receive.SerialLogDirectory))
            {
                settings.Receive.SerialLogDirectory = defaults.Receive.SerialLogDirectory;
            }

            if (settings.Connection.BaudRate <= 0)
            {
                settings.Connection.BaudRate = defaults.Connection.BaudRate;
            }

            if (settings.Receive.FontSize < 7 || settings.Receive.FontSize > 40)
            {
                settings.Receive.FontSize = defaults.Receive.FontSize;
            }

            return settings;
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
