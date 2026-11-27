using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using AggPanSearch.Util;
using AggPanSearch.Util.Extension;
using AggPanSearch.Util.Model;
using AggPanSearch.Entity.PanManage;
using AggPanSearch.Model.Param.PanManage;
using AggPanSearch.Service.PanManage;

namespace AggPanSearch.Business.PanManage
{
    /// <summary>
    /// 创 建：admin
    /// 日 期：2026-11-03 03:01
    /// 描 述：业务类
    /// </summary>
    public class PanConfBLL
    {
        private PanConfService panConfService = new PanConfService();

        #region 获取数据
        public async Task<TData<List<PanConfEntity>>> GetList(PanConfListParam param)
        {
            TData<List<PanConfEntity>> obj = new TData<List<PanConfEntity>>();
            await EnsureDefaultConfAsync();
            List<PanConfEntity> data = await panConfService.GetList(param);
            // 按 ConfKey（忽略大小写）去重：同一参数只保留一条有效记录，
            // 避免「参数配置」页手动新增同名参数后，设置页按 conf_type 分组渲染出重复表单。
            obj.Data = data
                .GroupBy(t => t.ConfKey, StringComparer.OrdinalIgnoreCase)
                .Select(g => PickKeepItem(g))
                .ToList();
            obj.Total = obj.Data.Count;
            obj.Tag = 1;
            return obj;
        }

        /// <summary>
        /// 首次打开基础设置页时自动补齐必要配置项（已存在则跳过），
        /// 避免「网盘Cookie设置」Tab 内没有表单项可填。
        /// 补齐后自动清理历史重复记录，保证设置页只展示一条。
        /// </summary>
        private async Task EnsureDefaultConfAsync()
        {
            List<PanConfEntity> existing = await panConfService.GetList(new PanConfListParam());
            foreach (KeyValuePair<string, string> kv in DefaultConfValues)
            {
                if (existing.Any(t => string.Equals(t.ConfKey, kv.Key, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                if (!ConfMeta.TryGetValue(kv.Key, out var meta))
                {
                    continue;
                }
                await panConfService.SaveForm(new PanConfEntity
                {
                    ConfKey = kv.Key,
                    ConfValue = kv.Value,
                    ConfTitle = meta.Title,
                    ConfDesc = meta.Desc,
                    ConfType = meta.Type,
                    ConfSpec = meta.Spec,
                    ConfStatus = 1,
                    ConfSystem = 1,
                    ConfSort = 0,
                });
            }
            await CleanDuplicateConfAsync();
        }

        /// <summary>
        /// 清理 pan_conf 中 ConfKey（忽略大小写）重复的历史记录：
        /// 每组保留「系统参数优先、Id 最小」的一条，其余删除。
        /// 该方法为 GetList 必经步骤，打开一次设置页即可自动修复存量脏数据。
        /// </summary>
        private async Task CleanDuplicateConfAsync()
        {
            List<PanConfEntity> all = await panConfService.GetList(new PanConfListParam());
            var groups = all
                .GroupBy(t => t.ConfKey, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1);
            foreach (var group in groups)
            {
                PanConfEntity keep = PickKeepItem(group);
                string delIds = string.Join(",", group
                    .Where(t => t.Id != keep.Id && t.Id.HasValue)
                    .Select(t => t.Id.Value.ToString()));
                if (!string.IsNullOrEmpty(delIds))
                {
                    await panConfService.DeleteForm(delIds);
                }
            }
        }

        /// <summary>
        /// 去重保留策略：优先保留系统参数（ConfSystem == 1），其次保留 Id 最小者。
        /// </summary>
        private static PanConfEntity PickKeepItem(IEnumerable<PanConfEntity> items)
        {
            return items
                .OrderByDescending(t => t.ConfSystem == 1)
                .ThenBy(t => t.Id)
                .First();
        }

        public async Task<TData<List<PanConfEntity>>> GetPageList(PanConfListParam param, Pagination pagination)
        {
            TData<List<PanConfEntity>> obj = new TData<List<PanConfEntity>>();
            obj.Data = await panConfService.GetPageList(param, pagination);
            obj.Total = pagination.TotalCount;
            obj.Tag = 1;
            return obj;
        }

        public async Task<TData<PanConfEntity>> GetEntity(long id)
        {
            TData<PanConfEntity> obj = new TData<PanConfEntity>();
            obj.Data = await panConfService.GetEntity(id);
            if (obj.Data != null)
            {
                obj.Tag = 1;
            }
            return obj;
        }

        public async Task<TData<List<int?>>> GetConfTypeList()
        {
            TData<List<int?>> obj = new TData<List<int?>>();
            obj.Data = await panConfService.GetConfTypeList();
            obj.Total = obj.Data.Count;
            obj.Tag = 1;
            return obj;
        }
        #endregion

        #region 提交数据
        public async Task<TData<string>> SaveForm(PanConfEntity entity)
        {
            TData<string> obj = new TData<string>();
            // 新增（Id 为空）时校验 ConfKey 唯一（忽略大小写），防止「参数配置」页手动新增同名参数导致重复
            if (entity != null && entity.Id.IsNullOrZero() && !string.IsNullOrEmpty(entity.ConfKey))
            {
                List<PanConfEntity> existing = await panConfService.GetList(new PanConfListParam());
                if (existing.Any(t => string.Equals(t.ConfKey, entity.ConfKey, StringComparison.OrdinalIgnoreCase)))
                {
                    obj.Message = "参数名已存在";
                    return obj;
                }
            }
            await panConfService.SaveForm(entity);
            obj.Data = entity.Id.ParseToString();
            obj.Tag = 1;
            return obj;
        }

        public async Task<TData> DeleteForm(string ids)
        {
            TData obj = new TData();
            await panConfService.DeleteForm(ids);
            obj.Tag = 1;
            return obj;
        }

        public async Task<TData> SaveBatch(List<PanConfEntity> entities)
        {
            TData obj = new TData();
            await panConfService.SaveBatch(entities);
            obj.Tag = 1;
            return obj;
        }
        #endregion

        #region 主题配置（"切换主题"页面使用）
        /// <summary>
        /// 新增 conf_key 时的默认元数据（标题/描述/分类/控件类型），
        /// 保证后台"基础设置"页也能正确展示这些配置项。
        /// </summary>
        private static readonly Dictionary<string, (string Title, string Desc, int Type, int Spec)> ConfMeta
            = new Dictionary<string, (string, string, int, int)>
            {
                { "theme", ("站点主题", "red=暖红 blue=海洋蓝 green=森林绿 dark=暗夜黑，由\"网盘管理-切换主题\"设置", 3, 0) },
                { "theme_switch", ("允许访客切换主题", "开启=>1，关闭=>0；关闭后前台导航栏不再显示主题切换入口", 3, 0) },
                { "search_block_words", ("搜索屏蔽词", "每行一个，搜索结果标题/描述命中的条目会被过滤掉", 1, 1) },
                { "search_tg_channels", ("TG频道", "每行一个频道名（t.me/s/ 后的部分），留空使用内置默认频道列表", 1, 1) },
                { "search_plugins", ("搜索插件", "每行一个插件名，留空使用全部已注册插件", 1, 1) },
                { "search_cloud_types", ("网盘类型", "逗号分隔的网盘类型（如 quark,baidu,aliyun），留空返回全部类型", 1, 1) },
                { "copyright_block_keywords", ("版权屏蔽关键词", "由「网盘管理-版权投诉」审核通过后自动生成，每行一个作品名；与搜索屏蔽词一起参与结果过滤", 1, 1) },
                { "quark_cookie", ("夸克Cookie", "登录 pan.quark.cn 后按 F12 → Network → 任意 drive-pc.quark.cn 请求 → Request Headers → 复制 Cookie 整行（形如 __puus=...; __pus=...; __kp=...，必须含 __puus；不要填 JSON 文件内容）；用于采集资源转存并分享，不对外展示", 4, 1) },
                { "quark_transfer_dir", ("夸克默认转存目录", "网盘根目录下用于存放转存资源的文件夹名称（留空则默认「采集资源」）；转存时会自动在其中按资源分类建立子文件夹，不存在会自动创建", 4, 1) },
                { "ai_provider", ("AI 厂商", "当前启用的厂商标识：deepseek（默认）；接入新厂商后填其标识即可切换，留空按 deepseek 处理", 14, 0) },
                { "ai_base_url", ("AI 接口地址", "OpenAI 兼容地址；DeepSeek 填 https://api.deepseek.com（带不带 /v1 都可以，不要带 /chat/completions）；留空使用厂商默认地址", 14, 0) },
                { "ai_deepseek_apikey", ("AI 接口 Key", "DeepSeek 控制台创建的 API Key（sk- 开头），仅后端调用使用，不输出到前台", 14, 0) },
                { "ai_model", ("AI 模型", "默认 deepseek-flash；可手工修改，留空按厂商默认模型处理", 14, 0) },
                { "ai_intro_prompt", ("AI 介绍提示词", "支持占位符 {title} {shareTitle} {category} {types} {old} {files}，留空使用内置模板", 14, 1) },
                { "ai_intro_max_chars", ("AI 介绍字数上限", "AI 生成资源简介随机取其中一个作为本次字数上限。", 14, 0) }
            };

        /// <summary>
        /// 首次打开基础设置页时需要自动补齐的配置项及默认值（key 已存在则跳过）。
        /// </summary>
        private static readonly Dictionary<string, string> DefaultConfValues
            = new Dictionary<string, string>
            {
                { "quark_cookie", string.Empty },
                { "quark_transfer_dir", "采集资源" },
                { "ai_provider", "deepseek" },
                { "ai_base_url", "https://api.deepseek.com" },
                { "ai_deepseek_apikey", string.Empty },
                { "ai_model", "deepseek-flash" },
                { "ai_intro_prompt", string.Empty },
                { "ai_intro_max_chars", "200" }
            };

        /// <summary>
        /// 按 conf_key 读取配置值；不存在的 key 返回空字符串，方便页面直接使用。
        /// pan_conf 表数据量很小，这里取全量后内存过滤，避免表达式翻译差异。
        /// </summary>
        public async Task<TData<Dictionary<string, string>>> GetConfValues(List<string> keys)
        {
            TData<Dictionary<string, string>> obj = new TData<Dictionary<string, string>>();
            Dictionary<string, string> result = new Dictionary<string, string>();
            TData<List<PanConfEntity>> listObj = await GetList(null);
            List<PanConfEntity> list = listObj.Data ?? new List<PanConfEntity>();
            if (keys != null)
            {
                foreach (string key in keys)
                {
                    PanConfEntity item = list.FirstOrDefault(t => string.Equals(t.ConfKey, key, StringComparison.OrdinalIgnoreCase));
                    result[key] = item == null || item.ConfValue == null ? "" : item.ConfValue;
                }
            }
            obj.Data = result;
            obj.Tag = 1;
            return obj;
        }

        /// <summary>
        /// 保存指定 conf_key 的配置值：已存在则更新 conf_value，不存在则新增配置项。
        /// </summary>
        public async Task<TData> SaveConfValues(Dictionary<string, string> values)
        {
            TData obj = new TData();
            if (values == null || values.Count == 0)
            {
                obj.Message = "没有需要保存的配置";
                return obj;
            }
            TData<List<PanConfEntity>> listObj = await GetList(null);
            List<PanConfEntity> list = listObj.Data ?? new List<PanConfEntity>();
            foreach (KeyValuePair<string, string> kv in values)
            {
                PanConfEntity item = list.FirstOrDefault(t => string.Equals(t.ConfKey, kv.Key, StringComparison.OrdinalIgnoreCase));
                if (item != null && !item.Id.IsNullOrZero())
                {
                    await panConfService.SaveBatch(new List<PanConfEntity>
                    {
                        new PanConfEntity { Id = item.Id, ConfValue = kv.Value }
                    });
                }
                else
                {
                    (string Title, string Desc, int Type, int Spec) meta;
                    if (!ConfMeta.TryGetValue(kv.Key, out meta))
                    {
                        meta = (kv.Key, "", 3, 0);
                    }
                    await panConfService.SaveForm(new PanConfEntity
                    {
                        ConfKey = kv.Key,
                        ConfValue = kv.Value,
                        ConfTitle = meta.Title,
                        ConfDesc = meta.Desc,
                        ConfType = meta.Type,
                        ConfSpec = meta.Spec,
                        ConfStatus = 1,
                        ConfSystem = 1,
                        ConfSort = 90
                    });
                }
            }
            obj.Tag = 1;
            obj.Message = "保存成功";
            return obj;
        }
        #endregion

        #region 私有方法
        #endregion
    }
}
