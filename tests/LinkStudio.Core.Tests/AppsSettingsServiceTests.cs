using System.IO.Ports;
using LinkStudio.Core.Models;
using LinkStudio.Core.Services;
using Xunit;

namespace LinkStudio.Core.Tests
{
    public sealed class AppsSettingsServiceTests : IDisposable
    {
        private readonly string _tempDir = Path.Combine(
            Path.GetTempPath(),
            "LinkStudioTests",
            Guid.NewGuid().ToString("N"));

        [Fact]
        public async Task LoadSettingsAsync_ReturnsDefaultsWhenFileMissing()
        {
            var path = Path.Combine(_tempDir, "settings.json");
            using var service = new AppsSettingsService(path);

            var loaded = await service.LoadSettingsAsync();

            Assert.Equal(AppsSettings.CurrentVersion, loaded.Version);
            Assert.Equal("Light", loaded.Studio.Theme);
            Assert.Equal(1000000, loaded.Connection.BaudRate);
            Assert.Equal(16, loaded.Receive.FontSize);
            Assert.True(loaded.Send.EnterToSend);
        }

        [Fact]
        public async Task SaveAndLoadSettingsAsync_RoundTripsAllSettings()
        {
            var path = Path.Combine(_tempDir, "nested", "settings.json");
            using var service = new AppsSettingsService(path);

            var expected = AppsSettings.CreateDefault();
            expected.Studio.Theme = "WarmPaper";
            expected.Studio.AutoSaveAppLogOnStartup = false;
            expected.Studio.AppLogDirectory = Path.Combine(_tempDir, "app-logs");
            expected.Connection.PortName = "COM7";
            expected.Connection.BaudRate = 921600;
            expected.Connection.Parity = Parity.Even;
            expected.Receive.FontSize = 20;
            expected.Receive.DisplayMode = "Hex";
            expected.Receive.ShowDirection = false;
            expected.Receive.AutoSaveSerialLogOnOpen = true;
            expected.Receive.SerialLogFormat = ".bin";
            expected.Send.IsHex = true;
            expected.Send.AppendCr = true;
            expected.Send.EnterToSend = false;

            await service.SaveSettingsAsync(expected);
            var actual = await service.LoadSettingsAsync();

            Assert.Equal("WarmPaper", actual.Studio.Theme);
            Assert.False(actual.Studio.AutoSaveAppLogOnStartup);
            Assert.Equal(expected.Studio.AppLogDirectory, actual.Studio.AppLogDirectory);
            Assert.Equal("COM7", actual.Connection.PortName);
            Assert.Equal(921600, actual.Connection.BaudRate);
            Assert.Equal(Parity.Even, actual.Connection.Parity);
            Assert.Equal(20, actual.Receive.FontSize);
            Assert.Equal("Hex", actual.Receive.DisplayMode);
            Assert.False(actual.Receive.ShowDirection);
            Assert.True(actual.Receive.AutoSaveSerialLogOnOpen);
            Assert.Equal(".bin", actual.Receive.SerialLogFormat);
            Assert.True(actual.Send.IsHex);
            Assert.True(actual.Send.AppendCr);
            Assert.False(actual.Send.EnterToSend);
        }

        [Fact]
        public async Task LoadSettingsAsync_FallsBackToDefaultWhenJsonCorrupt()
        {
            Directory.CreateDirectory(_tempDir);
            var path = Path.Combine(_tempDir, "settings.json");
            await File.WriteAllTextAsync(path, "{ invalid json !!!");

            using var service = new AppsSettingsService(path);
            var loaded = await service.LoadSettingsAsync();

            Assert.Equal(1000000, loaded.Connection.BaudRate);
            Assert.Equal(16, loaded.Receive.FontSize);
        }

        public void Dispose()
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
    }
}
