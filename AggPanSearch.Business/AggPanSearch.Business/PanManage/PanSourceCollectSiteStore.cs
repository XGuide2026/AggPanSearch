using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using AggPanSearch.Util;

namespace AggPanSearch.Business.PanManage
{
    /// <summary>
    /// 描 述：站点配置 JSON 仓库。
    /// 读取时按文件内容实时加载（文件很小）；写入时加锁并原子替换，避免并发写坏文件。
    /// </summary>
    public static class PanSourceCollectSiteStore
    {
        private const string FolderName = "AppData";
        private const string FileName = "collect-sites.json";

        private static readonly object SyncRoot = new object();

        private static readonly JsonSerializerSettings SerializerSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            DateFormatString = "yyyy-MM-dd HH:mm:ss",
        };

        /// <summary>读取全部站点，按创建时间倒序返回。</summary>
        public static List<PanSourceCollectSite> GetAll()
        {
            lock (SyncRoot)
            {
                return Read()
                    .OrderByDescending(t => t.CreateTime)
                    .ThenBy(t => t.SiteName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        public static PanSourceCollectSite GetById(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }
            lock (SyncRoot)
            {
                return Read().FirstOrDefault(t => string.Equals(t.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
            }
        }

        /// <summary>新增或按 Id 覆盖保存。</summary>
        public static void Save(PanSourceCollectSite site)
        {
            if (site == null)
            {
                return;
            }
            lock (SyncRoot)
            {
                List<PanSourceCollectSite> sites = Read();
                int index = sites.FindIndex(t => string.Equals(t.Id, site.Id, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                {
                    sites[index] = site;
                }
                else
                {
                    sites.Add(site);
                }
                Write(sites);
            }
        }

        /// <summary>删除指定站点，返回是否命中。</summary>
        public static bool Delete(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return false;
            }
            lock (SyncRoot)
            {
                List<PanSourceCollectSite> sites = Read();
                int removed = sites.RemoveAll(t => string.Equals(t.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
                if (removed == 0)
                {
                    return false;
                }
                Write(sites);
                return true;
            }
        }

        private static string GetFilePath()
        {
            string root = GlobalContext.HostingEnvironment?.ContentRootPath;
            if (string.IsNullOrWhiteSpace(root))
            {
                root = AppContext.BaseDirectory;
            }
            return Path.Combine(root, FolderName, FileName);
        }

        private static List<PanSourceCollectSite> Read()
        {
            string path = GetFilePath();
            try
            {
                if (!File.Exists(path))
                {
                    return new List<PanSourceCollectSite>();
                }
                string json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return new List<PanSourceCollectSite>();
                }
                List<PanSourceCollectSite> sites = JsonConvert.DeserializeObject<List<PanSourceCollectSite>>(json, SerializerSettings);
                return sites ?? new List<PanSourceCollectSite>();
            }
            catch (Exception ex)
            {
                // 文件损坏时不阻断页面，返回空列表并记录日志。
                LogHelper.Error(ex);
                return new List<PanSourceCollectSite>();
            }
        }

        private static void Write(List<PanSourceCollectSite> sites)
        {
            string path = GetFilePath();
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string json = JsonConvert.SerializeObject(sites, SerializerSettings);
            // 先写临时文件再整体替换，避免写入中断造成 JSON 截断。
            string tempPath = path + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, path, true);
        }
    }
}
