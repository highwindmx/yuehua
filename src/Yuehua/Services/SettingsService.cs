using System;
using System.IO;
using System.Text.Json;
using Yuehua.Models;

namespace Yuehua.Services;

/// <summary>
/// 应用设置持久化：%LOCALAPPDATA%\Yuehua\settings.json。
/// 走源生成 JSON（NativeAOT 安全）；读写任一步失败都退回默认值，绝不让设置问题影响启动。
/// </summary>
public static class SettingsService
{
    public static string ConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "Yuehua", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            var p = ConfigPath;
            if (File.Exists(p))
            {
                var json = File.ReadAllText(p);
                var s = JsonSerializer.Deserialize(json, JsonContext.Default.AppSettings);
                if (s != null) return s;
            }
        }
        catch { /* 设置损坏 → 用默认值继续 */ }

        return new AppSettings();
    }

    public static void Save(AppSettings s)
    {
        try
        {
            var p = ConfigPath;
            var dir = Path.GetDirectoryName(p);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(p, JsonSerializer.Serialize(s, JsonContext.Default.AppSettings));
        }
        catch { /* 保存失败不影响使用 */ }
    }
}
