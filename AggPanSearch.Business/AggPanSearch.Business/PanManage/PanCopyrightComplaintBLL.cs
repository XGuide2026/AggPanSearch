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
    /// 描 述：版权投诉业务类（pan_copyright_complaint）。
    /// 前台提交 → 后台审核；审核通过的作品名写入 pan_conf.copyright_block_keywords，
    /// 由 /api/other/web_search 合并到搜索屏蔽词中过滤结果。
    /// </summary>
    public class PanCopyrightComplaintBLL
    {
        private PanCopyrightComplaintService panCopyrightComplaintService = new PanCopyrightComplaintService();
        private PanConfBLL panConfBLL = new PanConfBLL();

        /// <summary>版权关键词配置键（与人工维护的 search_block_words 分开，撤销时互不影响）</summary>
        public const string KeywordsConfKey = "copyright_block_keywords";

        /// <summary>同一 IP 每小时最多提交次数</summary>
        public const int MaxPerHourPerIp = 5;

        #region 获取数据

        public async Task<TData<List<PanCopyrightComplaintEntity>>> GetList(PanCopyrightComplaintListParam param)
        {
            TData<List<PanCopyrightComplaintEntity>> obj = new TData<List<PanCopyrightComplaintEntity>>();
            obj.Data = await panCopyrightComplaintService.GetList(param);
            obj.Total = obj.Data.Count;
            obj.Tag = 1;
            return obj;
        }

        public async Task<TData<List<PanCopyrightComplaintEntity>>> GetPageList(PanCopyrightComplaintListParam param, Pagination pagination)
        {
            TData<List<PanCopyrightComplaintEntity>> obj = new TData<List<PanCopyrightComplaintEntity>>();
            obj.Data = await panCopyrightComplaintService.GetPageList(param, pagination);
            obj.Total = pagination.TotalCount;
            obj.Tag = 1;
            return obj;
        }

        public async Task<TData<PanCopyrightComplaintEntity>> GetEntity(long id)
        {
            TData<PanCopyrightComplaintEntity> obj = new TData<PanCopyrightComplaintEntity>();
            obj.Data = await panCopyrightComplaintService.GetEntity(id);
            if (obj.Data != null)
            {
                obj.Tag = 1;
            }
            return obj;
        }

        /// <summary>
        /// 指定 IP 最近一小时已提交的投诉条数（前台页面可提前提示）。
        /// </summary>
        public async Task<TData<int>> GetRecentCountByIp(string ip)
        {
            TData<int> obj = new TData<int>();
            obj.Data = await panCopyrightComplaintService.GetCountByIp(ip, DateTime.Now.AddHours(-1));
            obj.Tag = 1;
            return obj;
        }

        #endregion

        #region 提交数据

        /// <summary>
        /// 前台提交投诉：字段校验 + 同 IP 限频，通过后以「待处理（status=0）」入库。
        /// </summary>
        public async Task<TData> SubmitComplaint(PanCopyrightComplaintEntity entity, string ip)
        {
            TData obj = new TData();
            if (entity == null)
            {
                obj.Message = "参数不能为空";
                return obj;
            }

            entity.Title = (entity.Title ?? "").Trim();
            entity.InfringingUrl = (entity.InfringingUrl ?? "").Trim();
            entity.Complainant = (entity.Complainant ?? "").Trim();
            entity.Contact = (entity.Contact ?? "").Trim();
            entity.Description = (entity.Description ?? "").Trim();
            entity.EvidenceImages = (entity.EvidenceImages ?? "").Trim();

            if (entity.Title.Length < 2)
            {
                obj.Message = "请填写作品名称（至少 2 个字符）";
                return obj;
            }
            if (entity.Title.Length > 255)
            {
                obj.Message = "作品名称不能超过 255 个字符";
                return obj;
            }
            // 侵权链接为选填：填写了才校验格式
            if (entity.InfringingUrl.Length > 0 && !IsHttpUrl(entity.InfringingUrl))
            {
                obj.Message = "侵权链接需以 http:// 或 https:// 开头";
                return obj;
            }
            if (entity.InfringingUrl.Length > 1000)
            {
                obj.Message = "侵权链接不能超过 1000 个字符";
                return obj;
            }
            if (entity.Contact.Length == 0)
            {
                obj.Message = "请填写联系方式，便于处理结果反馈";
                return obj;
            }
            if (entity.Contact.Length > 200)
            {
                obj.Message = "联系方式不能超过 200 个字符";
                return obj;
            }
            if (entity.Complainant.Length > 100)
            {
                obj.Message = "投诉人不能超过 100 个字符";
                return obj;
            }
            if (entity.Description.Length > 2000)
            {
                obj.Message = "侵权说明不能超过 2000 个字符";
                return obj;
            }
            if (entity.EvidenceImages.Length > 1000)
            {
                obj.Message = "证明材料数量超出限制";
                return obj;
            }
            if (SplitEvidenceImages(entity).Count() == 0)
            {
                obj.Message = "请上传证明材料（至少 1 张）";
                return obj;
            }
            if (SplitEvidenceImages(entity).Count() > 3)
            {
                obj.Message = "证明材料最多上传 3 张";
                return obj;
            }

            if (!string.IsNullOrEmpty(ip))
            {
                int recent = await panCopyrightComplaintService.GetCountByIp(ip, DateTime.Now.AddHours(-1));
                if (recent >= MaxPerHourPerIp)
                {
                    obj.Message = "提交过于频繁，请稍后再试";
                    return obj;
                }
            }

            entity.Status = 0;
            entity.ComplaintIp = string.IsNullOrEmpty(ip) ? null : (ip.Length > 64 ? ip.Substring(0, 64) : ip);
            entity.AuditRemark = null;
            entity.AuditTime = null;
            entity.AuditUserId = 0;
            await panCopyrightComplaintService.SaveForm(entity);

            obj.Tag = 1;
            obj.Message = "投诉已提交，我们会尽快核实处理";
            return obj;
        }

        /// <summary>
        /// 批量审核：status=1 通过、status=2 驳回，备注必填。
        /// 审核完成后重建版权关键词库。
        /// </summary>
        public async Task<TData> Audit(string ids, int status, string remark, long userId)
        {
            TData obj = new TData();
            if (status != 1 && status != 2)
            {
                obj.Message = "审核状态不正确";
                return obj;
            }
            long[] idArr = TextHelper.SplitToArray<long>(ids, ',');
            if (idArr == null || idArr.Length == 0)
            {
                obj.Message = "请选择要审核的投诉";
                return obj;
            }
            remark = (remark ?? "").Trim();
            if (remark.Length == 0)
            {
                obj.Message = "请填写处理备注";
                return obj;
            }
            if (remark.Length > 1000)
            {
                obj.Message = "处理备注不能超过 1000 个字符";
                return obj;
            }

            await panCopyrightComplaintService.UpdateStatus(idArr.ToList(), status, remark, userId);
            TData syncObj = await SyncCopyrightKeywords();
            if (syncObj.Tag != 1)
            {
                // 审核本身已成功，关键词同步失败只提示，不影响状态流转
                obj.Tag = 1;
                obj.Message = status == 1 ? "审核通过，但关键词同步失败：" + syncObj.Message : "已驳回";
                return obj;
            }

            obj.Tag = 1;
            obj.Message = status == 1 ? "审核通过，作品名称已加入搜索屏蔽关键词" : "已驳回";
            return obj;
        }

        /// <summary>
        /// 删除投诉记录，并重建版权关键词库（被删投诉对应关键词自动失效）。
        /// </summary>
        public async Task<TData> DeleteForm(string ids)
        {
            TData obj = new TData();
            long[] idArr = TextHelper.SplitToArray<long>(ids, ',');
            if (idArr == null || idArr.Length == 0)
            {
                obj.Message = "请选择要删除的投诉";
                return obj;
            }
            await panCopyrightComplaintService.DeleteForm(ids);
            TData syncObj = await SyncCopyrightKeywords();
            obj.Tag = 1;
            obj.Message = syncObj.Tag == 1 ? "删除成功" : "删除成功，但关键词同步失败：" + syncObj.Message;
            return obj;
        }

        /// <summary>
        /// 重建版权屏蔽关键词库（幂等）：取全部 status=1 投诉的作品名，去重后整库覆盖写入
        /// pan_conf.copyright_block_keywords。调用方需在成功后清理搜索配置缓存使其立即生效。
        /// </summary>
        public async Task<TData> SyncCopyrightKeywords()
        {
            TData obj = new TData();
            try
            {
                List<string> titles = await panCopyrightComplaintService.GetAuditedTitles();
                // 过短的作品名（如单个字）命中率过高，容易误伤，不参与过滤
                List<string> keywords = titles
                    .Select(t => t.Trim())
                    .Where(t => t.Length >= 2)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                TData saveObj = await panConfBLL.SaveConfValues(
                    new Dictionary<string, string> { { KeywordsConfKey, string.Join("\n", keywords) } });
                obj.Tag = saveObj.Tag;
                obj.Message = saveObj.Message;
            }
            catch (Exception ex)
            {
                obj.Message = "关键词库同步失败：" + ex.Message;
            }
            return obj;
        }

        #endregion

        #region 私有方法

        private static bool IsHttpUrl(string url)
        {
            return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>拆分证明材料图片路径（英文逗号分隔）</summary>
        private static IEnumerable<string> SplitEvidenceImages(PanCopyrightComplaintEntity entity)
        {
            return (entity.EvidenceImages ?? "")
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim())
                .Where(t => t.Length > 0);
        }

        #endregion
    }
}
