using LinkStudio.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LinkStudio.Core.Services.Interfaces
{
    public interface IAppsSettingsService : IDisposable
    {
        // 读取
        Task<AppsSettings> LoadSettingsAsync(CancellationToken cancellationToken = default);

        // 保存
        Task SaveSettingsAsync(AppsSettings appsSettings, CancellationToken cancellationToken = default);
    }
}
