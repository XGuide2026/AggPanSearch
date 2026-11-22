using System;
using System.Linq;
using System.Linq.Expressions;
using System.Collections.Generic;
using System.Threading.Tasks;
using AggPanSearch.Util;
using AggPanSearch.Util.Extension;
using AggPanSearch.Util.Model;
using AggPanSearch.Data.Repository;
using AggPanSearch.Entity.PanManage;
using AggPanSearch.Model.Param.PanManage;

namespace AggPanSearch.Service.PanManage
{
    /// <summary>
    /// 描 述：版权投诉数据服务（pan_copyright_complaint）
    /// </summary>
    public class PanCopyrightComplaintService : RepositoryFactory
    {
        #region 获取数据

        public async Task<List<PanCopyrightComplaintEntity>> GetList(PanCopyrightComplaintListParam param)
        {
            var expression = ListFilter(param);
            var list = await this.BaseRepository().FindList(expression);
            return list.OrderByDescending(t => t.BaseCreateTime).ThenByDescending(t => t.Id).ToList();
        }

        public async Task<List<PanCopyrightComplaintEntity>> GetPageList(PanCopyrightComplaintListParam param, Pagination pagination)
        {
            var expression = ListFilter(param);
            var list = await this.BaseRepository().FindList(expression, pagination);
            return list.ToList();
        }

        public async Task<PanCopyrightComplaintEntity> GetEntity(long id)
        {
            return await this.BaseRepository().FindEntity<PanCopyrightComplaintEntity>(id);
        }

        /// <summary>
        /// 所有已通过（Status=1）投诉的作品名称，用于重建版权屏蔽关键词库。
        /// </summary>
        public async Task<List<string>> GetAuditedTitles()
        {
            var expression = LinqExtensions.True<PanCopyrightComplaintEntity>();
            expression = expression.And(t => t.Status == 1);
            var list = await this.BaseRepository().FindList(expression);
            return list.Select(t => (t.Title ?? "").Trim())
                       .Where(t => t.Length > 0)
                       .Distinct(StringComparer.OrdinalIgnoreCase)
                       .ToList();
        }

        /// <summary>
        /// 统计指定 IP 在指定时间之后提交的投诉条数（用于同 IP 限频）。
        /// </summary>
        public async Task<int> GetCountByIp(string ip, DateTime since)
        {
            if (string.IsNullOrWhiteSpace(ip))
            {
                return 0;
            }
            var expression = LinqExtensions.True<PanCopyrightComplaintEntity>();
            expression = expression.And(t => t.ComplaintIp == ip && t.BaseCreateTime >= since);
            return this.BaseRepository().IQueryable(expression).Count();
        }

        #endregion

        #region 提交数据

        public async Task SaveForm(PanCopyrightComplaintEntity entity)
        {
            // BaseModifyEntity 的 Create() 只写 BaseCreateTime，需补 Modify() 回填 BaseModifyTime
            await entity.Create();
            await entity.Modify();
            await this.BaseRepository().Insert(entity);
        }

        /// <summary>
        /// 批量审核：Status=1 通过、2 驳回，同时写入处理备注、审核时间与审核人。
        /// </summary>
        public async Task UpdateStatus(List<long> ids, int status, string remark, long userId)
        {
            if (ids == null || ids.Count == 0)
            {
                return;
            }
            var expression = LinqExtensions.True<PanCopyrightComplaintEntity>();
            expression = expression.And(t => ids.Contains(t.Id.Value));
            List<PanCopyrightComplaintEntity> list = (await this.BaseRepository().FindList(expression)).ToList();
            foreach (PanCopyrightComplaintEntity entity in list)
            {
                entity.Status = status;
                entity.AuditRemark = string.IsNullOrWhiteSpace(remark) ? null : remark.Trim();
                entity.AuditTime = DateTime.Now;
                entity.AuditUserId = userId;
                await entity.Modify();
            }
            if (list.Count > 0)
            {
                await this.BaseRepository().Update(list);
            }
        }

        public async Task DeleteForm(string ids)
        {
            long[] idArr = TextHelper.SplitToArray<long>(ids, ',');
            if (idArr == null || idArr.Length == 0)
            {
                return;
            }
            await this.BaseRepository().Delete<PanCopyrightComplaintEntity>(idArr);
        }

        #endregion

        #region 私有方法

        private Expression<Func<PanCopyrightComplaintEntity, bool>> ListFilter(PanCopyrightComplaintListParam param)
        {
            var expression = LinqExtensions.True<PanCopyrightComplaintEntity>();
            if (param != null)
            {
                if (!string.IsNullOrWhiteSpace(param.title))
                {
                    string title = param.title.Trim();
                    expression = expression.And(t => t.Title.Contains(title));
                }
                if (!string.IsNullOrWhiteSpace(param.contact))
                {
                    string contact = param.contact.Trim();
                    expression = expression.And(t => t.Contact.Contains(contact));
                }
                if (!string.IsNullOrWhiteSpace(param.complaint_ip))
                {
                    string ip = param.complaint_ip.Trim();
                    expression = expression.And(t => t.ComplaintIp.Contains(ip));
                }
                if (param.status != null && param.status.Value >= 0)
                {
                    int status = param.status.Value;
                    expression = expression.And(t => t.Status == status);
                }
                if (param.startTime != null)
                {
                    DateTime start = param.startTime.Value.Date;
                    expression = expression.And(t => t.BaseCreateTime >= start);
                }
                if (param.endTime != null)
                {
                    DateTime end = param.endTime.Value.Date.AddDays(1);
                    expression = expression.And(t => t.BaseCreateTime < end);
                }
            }
            return expression;
        }

        #endregion
    }
}