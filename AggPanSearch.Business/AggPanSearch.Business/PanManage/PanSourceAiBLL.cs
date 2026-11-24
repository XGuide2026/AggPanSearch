using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AggPanSearch.AI;
using AggPanSearch.Business.PanManage.Quark;
using AggPanSearch.Entity.PanManage;
using AggPanSearch.Service.PanManage;
using AggPanSearch.Util.Model;

namespace AggPanSearch.Business.PanManage
{
    /// <summary>
    /// 资源介绍 AI 改写的编排层：读配置 → 校验网盘类型 → 读夸克分享真实文件清单
    /// → 组装事实 → 调用 AI → 清洗文案。只返回文本，不落库。
    /// </summary>
    public class PanSourceAiBLL
    {
        private const int MaxFileNames = 60;

        private const string SystemPrompt =
            "你是网盘资源介绍文案助手。根据用户给出的资源事实写一段中文介绍："
            + "只输出正文，不要标题、不要引号、不要列表符号、不要解释；"
            + "不得编造文件清单中不存在的剧集、画质、字幕或来源；"
            + "语言简洁有吸引力，控制在 80 到 200 字之间。";

        private const string DefaultPromptTemplate =
            "请为下面的网盘资源写一段介绍。\n"
            + "资源标题：{title}\n"
            + "分享标题：{shareTitle}\n"
            + "资源分类：{category}\n"
            + "网盘类型：{types}\n"
            + "原介绍：{old}\n"
            + "文件清单：{files}";

        private static readonly string[] CloudTypeNames = { "夸克网盘", "阿里网盘", "百度网盘", "UC网盘", "迅雷网盘" };

        private readonly PanSourceService panSourceService = new PanSourceService();
        private readonly PanSourceCategoryService panSourceCategoryService = new PanSourceCategoryService();
        private readonly PanConfBLL panConfBLL = new PanConfBLL();

        /// <summary>
        /// 仅读取链接，不调用 AI（不消耗 token）。失败时 Message 直接可展示。
        /// </summary>
        public async Task<TData<ShareFact>> BuildFact(long id, string passcodeOverride)
        {
            TData<ShareFact> obj = new TData<ShareFact>();

            PanSourceEntity entity = await panSourceService.GetEntity(id);
            if (entity == null)
            {
                obj.Message = "资源不存在或已被删除";
                return obj;
            }

            int isType = entity.IsType ?? 0;
            if (isType != 0)
            {
                obj.Message = $"仅支持夸克网盘，当前资源为{CloudTypeName(isType)}，未读取链接、未生成介绍";
                return obj;
            }

            string passcode = string.IsNullOrWhiteSpace(passcodeOverride)
                ? (entity.Code ?? string.Empty).Trim()
                : passcodeOverride.Trim();

            QuarkShareLink link;
            string parseError;
            if (!QuarkShareLink.TryParse(entity.Url, passcode, out link, out parseError))
            {
                obj.Message = parseError;
                return obj;
            }

            string cookie = await GetConfValue("quark_cookie");
            if (cookie.Length == 0)
            {
                obj.Message = "未配置夸克 Cookie，请到「基础设置 - 网盘设置」填写后再试";
                return obj;
            }

            try
            {
                QuarkPanClient client = new QuarkPanClient(cookie);
                QuarkShareToken share = await client.GetShareTokenAsync(link, CancellationToken.None);
                List<QuarkShareFile> files = await client.GetShareFilesAsync(link, share.Stoken, CancellationToken.None);

                string topDirName = string.Empty;
                if (files.Count == 1 && files[0].IsDir)
                {
                    List<QuarkShareFile> inner = await client.GetShareFilesAsync(link, share.Stoken, files[0].Fid, CancellationToken.None);
                    if (inner.Count > 0)
                    {
                        topDirName = files[0].FileName;
                        files = inner;
                    }
                }

                ShareFact fact = BuildShareFact(id, entity, share, files, topDirName);
                fact.CategoryName = await GetCategoryName(entity.SourceCategoryId);

                obj.Data = fact;
                obj.Tag = 1;
            }
            catch (QuarkPanException ex)
            {
                if (ex.CookieInvalid)
                {
                    obj.Message = "夸克 Cookie 已失效，请到「基础设置 - 网盘设置」重新获取并填写";
                }
                else if (ex.Code == 41006)
                {
                    obj.Message = "分享链接无法访问：可能已失效或被取消分享；若该分享需要提取码，请先在表单填写提取码后重试";
                }
                else
                {
                    obj.Message = "读取夸克分享失败：" + ex.Message;
                }
            }
            catch (Exception ex)
            {
                obj.Message = "读取夸克分享失败：" + ex.Message;
            }

            return obj;
        }

        /// <summary>
        /// 读取事实并调用 AI 生成介绍。只返回文本与用量，不写库。
        /// </summary>
        public async Task<TData<AiRewriteResult>> Rewrite(long id, string passcodeOverride)
        {
            TData<AiRewriteResult> obj = new TData<AiRewriteResult>();

            TData<ShareFact> factObj = await BuildFact(id, passcodeOverride);
            if (factObj.Tag != 1 || factObj.Data == null)
            {
                obj.Message = factObj.Message;
                return obj;
            }

            ShareFact fact = factObj.Data;

            AiProviderOptions options;
            string template;
            int maxChars;
            try
            {
                List<PanConfEntity> confList = await LoadConfList();
                string providerKey = GetValue(confList, "ai_provider");
                AiProviderDescriptor descriptor = AiProviderRegistry.GetDescriptor(providerKey);

                options = new AiProviderOptions
                {
                    ProviderKey = descriptor.Key,
                    BaseUrl = GetValue(confList, "ai_base_url"),
                    ApiKey = GetValue(confList, descriptor.ApiKeyConfKey),
                    Model = GetValue(confList, "ai_model"),
                    TimeoutSeconds = 30,
                    MaxTokens = 600
                };

                template = GetValue(confList, "ai_intro_prompt");
                maxChars = PickMaxChars(GetValue(confList, "ai_intro_max_chars"), 200);
            }
            catch (AiException ex)
            {
                obj.Message = ex.Message;
                return obj;
            }

            if (string.IsNullOrWhiteSpace(template))
            {
                template = DefaultPromptTemplate;
            }

            try
            {
                IAiChatClient client = AiProviderRegistry.Create(options);
                string prompt = RenderTemplate(template, fact);
                AiChatResponse response = await client.ChatAsync(SystemPrompt, prompt, 600, CancellationToken.None);
                string content = AiTextCleaner.Clean(response.Content, maxChars);

                if (content.Length == 0)
                {
                    obj.Message = "AI 返回内容为空，请重新生成";
                    return obj;
                }

                obj.Data = new AiRewriteResult
                {
                    Content = content,
                    CharCount = content.Length,
                    RawContent = response.Content,
                    Model = response.Model,
                    ElapsedMs = response.ElapsedMs,
                    PromptTokens = response.Usage.PromptTokens,
                    CompletionTokens = response.Usage.CompletionTokens,
                    Fact = fact
                };
                obj.Tag = 1;
            }
            catch (AiException ex)
            {
                obj.Message = ex.Message;
            }
            catch (Exception ex)
            {
                obj.Message = "AI 改写失败：" + ex.Message;
            }

            return obj;
        }

        private static ShareFact BuildShareFact(long id, PanSourceEntity entity, QuarkShareToken share, List<QuarkShareFile> files, string topDirName)
        {
            List<QuarkShareFile> fileItems = files.Where(t => !t.IsDir).ToList();
            long totalSize = fileItems.Where(t => t.Size > 0).Sum(t => t.Size);

            ShareFact fact = new ShareFact
            {
                SourceId = id,
                Title = (entity.Title ?? string.Empty).Trim(),
                OldContent = (entity.VodContent ?? string.Empty).Trim(),
                ShareTitle = (share.Title ?? string.Empty).Trim(),
                AuthorName = (share.AuthorName ?? string.Empty).Trim(),
                CloudType = CloudTypeName(entity.IsType ?? 0),
                TopDirName = topDirName,
                FileCount = fileItems.Count,
                DirCount = files.Count - fileItems.Count,
                TotalSize = totalSize,
                TotalSizeText = FormatSize(totalSize),
                TypeSummary = BuildTypeSummary(fileItems),
                FileNames = fileItems.Take(MaxFileNames).Select(t => t.FileName).ToList()
            };

            return fact;
        }

        private static string BuildTypeSummary(List<QuarkShareFile> files)
        {
            List<string> parts = files
                .Select(t => Path.GetExtension(t.FileName ?? string.Empty).TrimStart('.').ToLowerInvariant())
                .Where(t => t.Length > 0 && t.Length <= 8)
                .GroupBy(t => t)
                .OrderByDescending(t => t.Count())
                .ThenBy(t => t.Key, StringComparer.Ordinal)
                .Take(6)
                .Select(t => t.Key + "×" + t.Count())
                .ToList();

            return string.Join("、", parts);
        }

        private static string FormatSize(long bytes)
        {
            if (bytes <= 0)
            {
                return "未知";
            }

            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double size = bytes;
            int index = 0;
            while (size >= 1024 && index < units.Length - 1)
            {
                size /= 1024;
                index++;
            }

            return size.ToString(index <= 1 ? "0" : "0.#") + " " + units[index];
        }

        private static string RenderTemplate(string template, ShareFact fact)
        {
            return template
                .Replace("{title}", fact.Title)
                .Replace("{shareTitle}", fact.ShareTitle)
                .Replace("{category}", fact.CategoryName)
                .Replace("{types}", fact.CloudType)
                .Replace("{old}", string.IsNullOrWhiteSpace(fact.OldContent) ? "（无）" : fact.OldContent)
                .Replace("{files}", BuildFilesText(fact));
        }

        private static string BuildFilesText(ShareFact fact)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append("共 ").Append(fact.FileCount).Append(" 个文件，总容量 ").Append(fact.TotalSizeText);

            if (!string.IsNullOrWhiteSpace(fact.TypeSummary))
            {
                builder.Append("，类型分布 ").Append(fact.TypeSummary);
            }
            if (!string.IsNullOrWhiteSpace(fact.TopDirName))
            {
                builder.Append("，所属目录「").Append(fact.TopDirName).Append("」");
            }

            if (fact.FileNames.Count > 0)
            {
                builder.Append("。文件名：").Append(string.Join("、", fact.FileNames));
                if (fact.FileCount > fact.FileNames.Count)
                {
                    builder.Append(" 等（仅列出前 ").Append(fact.FileNames.Count).Append(" 个）");
                }
            }

            builder.Append("。");
            return builder.ToString();
        }

        private static string CloudTypeName(int isType)
        {
            if (isType >= 0 && isType < CloudTypeNames.Length)
            {
                return CloudTypeNames[isType];
            }
            return "未知网盘";
        }

        private static int ParseInt(string text, int defaultValue)
        {
            int value;
            if (int.TryParse((text ?? string.Empty).Trim(), out value) && value > 0)
            {
                return value;
            }
            return defaultValue;
        }

        /// <summary>
        /// 从「AI 介绍字数上限」配置值解析候选列表并随机取一个。
        /// 配置支持逗号、顿号、分号、空白等分隔多个候选值：
        /// 0 个有效候选→返回默认值；1 个→固定使用；多个→随机取一。
        /// </summary>
        private static int PickMaxChars(string text, int defaultValue)
        {
            // 覆盖中文输入法常见符号：逗号 、 顿号 、分号 、空格/制表符，忽略空项
            char[] separators = { ',', '，', '、', ';', '；', ' ', '\t', '\n' };
            List<int> candidates = new List<int>();
            foreach (string part in (text ?? string.Empty).Split(separators, StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(part.Trim(), out int value) && value > 0)
                {
                    candidates.Add(value);
                }
            }

            if (candidates.Count == 0)
            {
                return defaultValue;
            }
            if (candidates.Count == 1)
            {
                return candidates[0];
            }
            return candidates[new Random().Next(candidates.Count)];
        }

        private async Task<List<PanConfEntity>> LoadConfList()
        {
            TData<List<PanConfEntity>> obj = await panConfBLL.GetList(null);
            return obj.Data ?? new List<PanConfEntity>();
        }

        private static string GetValue(List<PanConfEntity> confList, string key)
        {
            PanConfEntity item = confList.FirstOrDefault(t => string.Equals(t.ConfKey, key, StringComparison.OrdinalIgnoreCase));
            return (item?.ConfValue ?? string.Empty).Trim();
        }

        private async Task<string> GetConfValue(string key)
        {
            List<string> keys = new List<string> { key };
            TData<Dictionary<string, string>> obj = await panConfBLL.GetConfValues(keys);
            Dictionary<string, string> values = obj == null ? null : obj.Data;
            string value;
            if (values != null && values.TryGetValue(key, out value))
            {
                return (value ?? string.Empty).Trim();
            }
            return string.Empty;
        }

        private async Task<string> GetCategoryName(int? categoryId)
        {
            int id = categoryId ?? 0;
            if (id <= 0)
            {
                return string.Empty;
            }

            PanSourceCategoryEntity entity = await panSourceCategoryService.GetEntity(id);
            return (entity?.Name ?? string.Empty).Trim();
        }
    }

    /// <summary>夸克分享的事实清单，供前端左栏展示与提示词渲染共用。</summary>
    public class ShareFact
    {
        public long SourceId { get; set; }

        /// <summary>资源标题（后台录入）。</summary>
        public string Title { get; set; }

        /// <summary>夸克分享标题。</summary>
        public string ShareTitle { get; set; }

        /// <summary>分享者昵称。</summary>
        public string AuthorName { get; set; }

        /// <summary>分类名称。</summary>
        public string CategoryName { get; set; }

        /// <summary>网盘类型文案。</summary>
        public string CloudType { get; set; }

        /// <summary>单层下钻时的目录名，未下钻为空。</summary>
        public string TopDirName { get; set; }

        public int FileCount { get; set; }

        public int DirCount { get; set; }

        public long TotalSize { get; set; }

        public string TotalSizeText { get; set; }

        /// <summary>扩展名分布，如 mkv×8、srt×3。</summary>
        public string TypeSummary { get; set; }

        public List<string> FileNames { get; set; }

        /// <summary>原资源介绍，供提示词参考。</summary>
        public string OldContent { get; set; }
    }

    /// <summary>AI 改写结果。</summary>
    public class AiRewriteResult
    {
        /// <summary>清洗后的正文，可直接回填。</summary>
        public string Content { get; set; }

        /// <summary>清洗前原文，便于用户对照。</summary>
        public string RawContent { get; set; }

        public int CharCount { get; set; }

        public string Model { get; set; }

        public long ElapsedMs { get; set; }

        public int PromptTokens { get; set; }

        public int CompletionTokens { get; set; }

        /// <summary>同次请求的事实清单，前端可一次渲染左右两栏。</summary>
        public ShareFact Fact { get; set; }
    }
}
